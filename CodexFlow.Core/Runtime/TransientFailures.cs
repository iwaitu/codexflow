// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Runtime;

/// <summary>
/// The single place that decides "will this failure probably pass on its own?". It used to be written out three times in the background runner and
/// twice for transport exceptions (kernel and runtime engine), and the copies drifted apart. Exceptions are judged by type first and message
/// second; a reported failure (a worker's detail code and message, which are already text by the time they reach the runner) can only be judged
/// by the markers a provider or the host is known to emit.
/// </summary>
public static class TransientFailures
{
    private static readonly string[] ThrottleMarkers = ["Throttling", "token-limit", "quota", "rate limit"];

    private static readonly string[] ProviderMarkers =
    [
        "QRE-backed QueryRuntime model stream timed out", "TimeoutException", "Vllm error: event:error",
        "InternalError.Algo.InternalPostprocessError", "operation was canceled", "connection closed",
        // the detail code the runtime reports when the connection to the model failed and its own retries ran out
        "provider_transport_failed",
    ];

    private static readonly string[] EnvironmentMarkers =
    [
        "WorkspaceUnavailable", "Win32Exception", "trying to start process '/usr/bin/git'", "No such file or directory",
    ];

    private static readonly string[] TransportMessageMarkers =
    [
        "Response ended prematurely", "ResponseEnded", "An error occurred while sending the request", "connection reset",
    ];

    /// <summary>The provider asked us to slow down (rate/quota/token limit).</summary>
    public static bool IsProviderThrottle(string? detail) => ContainsAny(detail, ThrottleMarkers);

    /// <summary>A reported provider-side failure (timeout, dropped stream, throttle) that a later attempt can succeed at.</summary>
    public static bool IsTransientProviderFailure(string? detailCode, string? message)
    {
        var combined = detailCode + "\n" + message;
        return ContainsAny(combined, ThrottleMarkers) || ContainsAny(combined, ProviderMarkers);
    }

    /// <summary>As <see cref="IsTransientProviderFailure"/>, plus host-environment failures (workspace not mounted yet, git could not start).</summary>
    public static bool IsTransientWorkerFailure(string? detailCode, string? message) =>
        IsTransientProviderFailure(detailCode, message) || ContainsAny(detailCode + "\n" + message, EnvironmentMarkers);

    /// <summary>An exception thrown while talking to the model endpoint that is worth retrying. Cancellation the caller asked for is never transient.</summary>
    public static bool IsTransientTransport(Exception ex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        if (ex is OperationCanceledException or HttpRequestException)
        {
            return true;
        }

        var root = ex.GetBaseException();
        return root is HttpRequestException || ContainsAny($"{ex.Message} {root.Message}", TransportMessageMarkers);
    }

    /// <summary>
    /// The connection itself failed (refused, reset, dropped; no HTTP status involved), so sending the same request again is safe. Judged by
    /// type through the whole chain, then by the messages a provider SDK is known to keep when it rethrows as a plain exception.
    /// </summary>
    public static bool IsConnectionFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return false;
            }

            if (current is HttpRequestException { StatusCode: null } or IOException or System.Net.Sockets.SocketException ||
                ContainsAny(current.Message, TransportMessageMarkers))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string? text, string[] markers)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
