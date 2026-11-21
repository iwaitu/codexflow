// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Security;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Browser.Cdp;

/// <summary>
/// Abstraction over either a CodexFlow-managed local Chromium process or a verified remote CDP Grid WebSocket endpoint.
/// </summary>
internal interface ICdpBrowserRuntime : IAsyncDisposable
{
    CdpConnection Connection { get; }

    bool IsAlive { get; }

    bool IsRemote { get; }

    bool Headless { get; }

    BrowserProfileMode ProfileMode { get; }

    string RuntimeKey { get; }

    event Action? Terminated;
}

/// <summary>
/// Connects to an external/remote CDP Grid browser instance over verified ws:// or wss:// WebSocket endpoints
/// (direct IP pinning after DNS verification). Disposing disconnects without terminating the remote browser process.
/// </summary>
internal sealed class CdpRemoteBrowserRuntime : ICdpBrowserRuntime
{
    private readonly ILogger _logger;
    private int _disposed;
    private int _terminatedNotified;

    public CdpConnection Connection { get; }

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && Connection.IsConnected;

    public bool IsRemote => true;

    public bool Headless => true;

    public BrowserProfileMode ProfileMode => BrowserProfileMode.Ephemeral;

    public string RuntimeKey { get; }

    public event Action? Terminated;

    private CdpRemoteBrowserRuntime(CdpConnection connection, string normalizedWsUrl, ILogger logger)
    {
        Connection = connection;
        RuntimeKey = $"remote:{normalizedWsUrl}";
        _logger = logger;
        Connection.Disconnected += (_, _) => RaiseTerminated();
    }

    public static async Task<CdpRemoteBrowserRuntime> ConnectAsync(
        string remoteWsUrl,
        ProtectedLocalPortRegistry portRegistry,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(portRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        var verification = await BrowserRemoteEndpointGuard
            .ResolveAndVerifyRemoteCdpEndpointAsync(remoteWsUrl, portRegistry, ct: ct)
            .ConfigureAwait(false);
        if (!verification.Allowed || verification.NormalizedUri is null || verification.VerifiedAddresses is null)
        {
            throw new InvalidOperationException(
                verification.ErrorMessage ?? "远端 CDP WebSocket 地址安全校验失败。");
        }

        await using var connectionOwner = new AsyncResourceOwner<CdpConnection>();
        var conn = await connectionOwner.AcquireAsync(() => CdpConnection.ConnectVerifiedAsync(
            verification.NormalizedUri, verification.VerifiedAddresses, logger, ct)).ConfigureAwait(false);
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(8));
            await conn.SendAsync("Browser.getVersion", ct: probeCts.Token).ConfigureAwait(false);
            var runtime = new CdpRemoteBrowserRuntime(conn, verification.NormalizedUri.AbsoluteUri, logger);
            connectionOwner.RelinquishOwnership();
            return runtime;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"连接或验证远端 CDP 浏览器失败 ({BrowserRemoteEndpointGuard.MaskRemoteCdpWsUrl(verification.NormalizedUri.AbsoluteUri)}): {ex.Message}",
                ex);
        }
    }

    private void RaiseTerminated()
    {
        if (Interlocked.Exchange(ref _terminatedNotified, 1) != 0)
        {
            return;
        }

        var handler = Terminated;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unhandled exception in remote CDP runtime Terminated callback.");
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Remote CDP Grid endpoints are shared/managed externally: never send Browser.close.
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
