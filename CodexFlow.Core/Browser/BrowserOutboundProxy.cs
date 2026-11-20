// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodexFlow.Core.Models;
using CodexFlow.Core.Security;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Browser;

/// <summary>
/// Localhost HTTP/CONNECT outbound proxy that serves as the real network enforcement boundary for
/// managed Chromium contexts. Every TCP connection (HTTP, HTTPS, WebSocket, ServiceWorker, loopback)
/// is resolved inside this proxy, checked against <see cref="BrowserTargetPolicyGuard"/>, and dialed
/// directly to the verified <see cref="IPAddress"/> to eliminate DNS rebinding and browser-side bypasses.
/// </summary>
public sealed class BrowserOutboundProxy : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly BrowserTargetPolicyGuard _guard;
    private readonly ProtectedLocalPortRegistry _portRegistry;
    private readonly Func<SessionWorkMode> _workModeProvider;
    private readonly Func<IReadOnlySet<int>?> _approvedLoopbackPortsProvider;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    public int Port { get; }

    public string ProxyServerUrl => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}");

    public BrowserOutboundProxy(
        BrowserTargetPolicyGuard guard,
        ProtectedLocalPortRegistry portRegistry,
        Func<SessionWorkMode> workModeProvider,
        Func<IReadOnlySet<int>?> approvedLoopbackPortsProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(portRegistry);
        ArgumentNullException.ThrowIfNull(workModeProvider);
        ArgumentNullException.ThrowIfNull(approvedLoopbackPortsProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _guard = guard;
        _portRegistry = portRegistry;
        _workModeProvider = workModeProvider;
        _approvedLoopbackPortsProvider = approvedLoopbackPortsProvider;
        _logger = logger;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _portRegistry.RegisterPort(Port, "browser-proxy");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException ex)
            {
                client?.Dispose();
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "BrowserOutboundProxy accept error on port {Port}", Port);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var clientStream = client.GetStream();

            var headerResult = await ReadHeadersAsync(clientStream, ct).ConfigureAwait(false);
            if (headerResult is null)
            {
                return;
            }

            var (headerText, leftover) = headerResult.Value;
            var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
            var requestLine = firstLineEnd >= 0 ? headerText[..firstLineEnd] : headerText;
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                await WriteErrorResponseAsync(clientStream, 400, "Bad Request", "Malformed proxy request line.", ct).ConfigureAwait(false);
                return;
            }

            var method = parts[0];
            var target = parts[1];
            var version = parts[2];

            if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(clientStream, target, leftover, ct).ConfigureAwait(false);
            }
            else
            {
                await HandleHttpForwardAsync(clientStream, method, target, version, headerText, firstLineEnd, leftover, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectAsync(
        NetworkStream clientStream,
        string authority,
        ReadOnlyMemory<byte> leftover,
        CancellationToken ct)
    {
        if (!TryParseHostPort(authority, defaultPort: 443, out var host, out var port))
        {
            await WriteErrorResponseAsync(clientStream, 400, "Bad Request", "Invalid CONNECT authority.", ct).ConfigureAwait(false);
            return;
        }

        var eval = await _guard
            .ResolveAndValidateEndpointAsync(host, port, _workModeProvider(), _approvedLoopbackPortsProvider(), ct)
            .ConfigureAwait(false);
        if (!eval.Allowed || eval.VerifiedAddresses is null || eval.VerifiedAddresses.Count == 0)
        {
            _logger.LogWarning("BrowserOutboundProxy blocked CONNECT {Host}:{Port} ({Code}): {Message}", host, port, eval.ReasonCode, eval.Message);
            await WriteErrorResponseAsync(clientStream, 403, "Forbidden", eval.Message ?? "Blocked by CodexFlow browser network policy.", ct).ConfigureAwait(false);
            return;
        }

        using var upstream = await DialVerifiedAsync(eval.VerifiedAddresses, port, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            await WriteErrorResponseAsync(clientStream, 502, "Bad Gateway", $"Failed to connect to {host}:{port}.", ct).ConfigureAwait(false);
            return;
        }

        await using var upstreamStream = upstream.GetStream();
        await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await clientStream.FlushAsync(ct).ConfigureAwait(false);

        if (!leftover.IsEmpty)
        {
            await upstreamStream.WriteAsync(leftover, ct).ConfigureAwait(false);
        }

        await TunnelBidirectionalAsync(clientStream, upstreamStream, ct).ConfigureAwait(false);
    }

    private async Task HandleHttpForwardAsync(
        NetworkStream clientStream,
        string method,
        string requestTarget,
        string version,
        string headerText,
        int firstLineEnd,
        ReadOnlyMemory<byte> leftover,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(requestTarget, UriKind.Absolute, out var targetUri) ||
            !string.Equals(targetUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorResponseAsync(clientStream, 403, "Forbidden", "Only http:// absolute URIs are permitted over plain proxy forwarding.", ct).ConfigureAwait(false);
            return;
        }

        var host = targetUri.IdnHost;
        var port = targetUri.IsDefaultPort ? 80 : targetUri.Port;

        var eval = await _guard
            .ResolveAndValidateEndpointAsync(host, port, _workModeProvider(), _approvedLoopbackPortsProvider(), ct)
            .ConfigureAwait(false);
        if (!eval.Allowed || eval.VerifiedAddresses is null || eval.VerifiedAddresses.Count == 0)
        {
            _logger.LogWarning("BrowserOutboundProxy blocked HTTP {Method} {Host}:{Port} ({Code}): {Message}", method, host, port, eval.ReasonCode, eval.Message);
            await WriteErrorResponseAsync(clientStream, 403, "Forbidden", eval.Message ?? "Blocked by CodexFlow browser network policy.", ct).ConfigureAwait(false);
            return;
        }

        using var upstream = await DialVerifiedAsync(eval.VerifiedAddresses, port, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            await WriteErrorResponseAsync(clientStream, 502, "Bad Gateway", $"Failed to connect to {host}:{port}.", ct).ConfigureAwait(false);
            return;
        }

        // Rewrite proxy-form request line to origin-form, reject smuggling patterns,
        // and force Connection: close except for explicit WebSocket upgrades.
        var pathAndQuery = string.IsNullOrEmpty(targetUri.PathAndQuery) ? "/" : targetUri.PathAndQuery;
        var rawHeaders = firstLineEnd >= 0 ? headerText[(firstLineEnd + 2)..] : string.Empty;
        var headerLines = rawHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var contentLengthCount = 0;
        var hasTransferEncoding = false;
        var isWebSocketUpgrade = false;
        foreach (var line in headerLines)
        {
            if (line.Contains('\r', StringComparison.Ordinal) || line.Contains('\n', StringComparison.Ordinal))
            {
                await WriteErrorResponseAsync(clientStream, 400, "Bad Request", "Malformed header line.", ct).ConfigureAwait(false);
                return;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLengthCount++;
            }
            else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                hasTransferEncoding = true;
            }
            else if (line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                isWebSocketUpgrade = true;
            }
        }

        if (contentLengthCount > 1 || (contentLengthCount > 0 && hasTransferEncoding))
        {
            await WriteErrorResponseAsync(clientStream, 400, "Bad Request", "Ambiguous request framing (CL/TE smuggling rejected).", ct).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(pathAndQuery).Append(' ').Append(version).Append("\r\n");

        var effectiveAuthority = targetUri.IsDefaultPort
            ? targetUri.IdnHost
            : string.Create(CultureInfo.InvariantCulture, $"{targetUri.IdnHost}:{port}");
        var hasConnectionHeader = false;
        var hasHostHeader = false;
        foreach (var line in headerLines)
        {
            if (line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                hasHostHeader = true;
                sb.Append("Host: ").Append(effectiveAuthority).Append("\r\n");
                continue;
            }

            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                hasConnectionHeader = true;
                if (isWebSocketUpgrade && line.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("Connection: Upgrade\r\n");
                }
                else
                {
                    sb.Append("Connection: close\r\n");
                }
                continue;
            }

            sb.Append(line).Append("\r\n");
        }

        if (!hasHostHeader)
        {
            sb.Append("Host: ").Append(effectiveAuthority).Append("\r\n");
        }

        if (!hasConnectionHeader)
        {
            sb.Append("Connection: close\r\n");
        }

        sb.Append("\r\n");

        await using var upstreamStream = upstream.GetStream();
        var rewrittenHeaderBytes = Encoding.Latin1.GetBytes(sb.ToString());
        await upstreamStream.WriteAsync(rewrittenHeaderBytes, ct).ConfigureAwait(false);
        if (!leftover.IsEmpty)
        {
            await upstreamStream.WriteAsync(leftover, ct).ConfigureAwait(false);
        }

        await upstreamStream.FlushAsync(ct).ConfigureAwait(false);
        await TunnelBidirectionalAsync(clientStream, upstreamStream, ct).ConfigureAwait(false);
    }

    private static async Task<TcpClient?> DialVerifiedAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken ct)
    {
        foreach (var ip in addresses)
        {
            var client = new TcpClient(ip.AddressFamily) { NoDelay = true };
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                await client.ConnectAsync(ip, port, timeoutCts.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception)
            {
                client.Dispose();
            }
        }

        return null;
    }

    private static async Task TunnelBidirectionalAsync(NetworkStream clientStream, NetworkStream upstreamStream, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var c2u = CopySafeAsync(clientStream, upstreamStream, linked.Token);
        var u2c = CopySafeAsync(upstreamStream, clientStream, linked.Token);

        await Task.WhenAny(c2u, u2c).ConfigureAwait(false);
        try { await linked.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        try { await Task.WhenAll(c2u, u2c).ConfigureAwait(false); } catch (Exception) { }
    }

    private static async Task CopySafeAsync(Stream source, Stream destination, CancellationToken ct)
    {
        try
        {
            await source.CopyToAsync(destination, 16384, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Expected on tunnel close.
        }
    }

    private static async Task<(string HeaderText, ReadOnlyMemory<byte> Leftover)?> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (ms.Length < MaxHeaderBytes)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                return null;
            }

            if (read <= 0)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
            var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var terminatorIdx = span.IndexOf(HeaderTerminator);
            if (terminatorIdx >= 0)
            {
                var headerBytes = span[..terminatorIdx];
                var headerText = Encoding.Latin1.GetString(headerBytes);
                var bodyStart = terminatorIdx + HeaderTerminator.Length;
                var leftover = bodyStart < span.Length
                    ? span[bodyStart..].ToArray()
                    : ReadOnlyMemory<byte>.Empty;
                return (headerText, leftover);
            }
        }

        return null;
    }

    private static async Task WriteErrorResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string body,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header);
        try
        {
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Ignore write errors on closing client.
        }
    }

    internal static bool TryParseHostPort(string authority, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        var trimmed = authority.Trim();
        if (trimmed.StartsWith('['))
        {
            var endBracket = trimmed.IndexOf(']');
            if (endBracket <= 1)
            {
                return false;
            }

            host = trimmed[1..endBracket];
            var remainder = trimmed[(endBracket + 1)..];
            if (remainder.Length == 0)
            {
                return true;
            }

            if (remainder[0] != ':' || !int.TryParse(remainder[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return false;
            }

            return port is > 0 and <= 65535;
        }

        var colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(trimmed[(colonIdx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            host = trimmed[..colonIdx];
            port = parsedPort;
            return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
        }

        host = trimmed;
        return !string.IsNullOrWhiteSpace(host);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _portRegistry.UnregisterPort(Port);
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (SocketException) { }
        _listener.Dispose();
        try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        _cts.Dispose();
    }
}
