// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Contracts.Browser;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Browser.Cdp;

internal sealed record BrowserPanelViewport(
    string TargetId, long NavigationRevision, double Width, double Height, double PageX, double PageY, double Scale);

// Page state is read and changed only while holding the owning session's ExecutionLock.
// The cancellation timer and explicit handoff can revoke access without waiting for that lock.
internal sealed class BrowserPanelLease : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTimeOffset _expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
    private readonly object _gate = new();
    private DateTimeOffset _lastInput;
    private DateTimeOffset _lastFrame;
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CancellationToken Token { get; }
    public BrowserPanelViewport? Viewport { get; set; }
    public string? ViewId { get; set; }
    public Dictionary<string, (int Width, int Height)> ResizedTargets { get; } = new(StringComparer.Ordinal);

    public BrowserPanelLease() => Token = _stop.Token;

    public void Start(Action onEnd)
    {
        Token.Register(onEnd);
        RenewHeartbeat();
    }

    public bool PermitRequest(bool frame)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (frame)
            {
                if (now - _lastFrame < TimeSpan.FromMilliseconds(320)) return false;
                _lastFrame = now;
            }
            else
            {
                if (now - _lastInput < TimeSpan.FromMilliseconds(80)) return false;
                _lastInput = now;
            }
            return true;
        }
    }

    public void RenewHeartbeat()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var remaining = _expiresAt - DateTimeOffset.UtcNow;
            _stop.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 0, 15_000)));
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            if (!_disposed) _stop.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Dispose();
        }
    }
}

public sealed partial class BrowserSessionManager
{
    private const int MaxPanelImageBytes = 1_572_864;
    private const int MaxPanelWidth = 1920;
    private const int MaxPanelHeight = 1440;

    private static BrowserSessionStateDto GetPublishedBrowserState(CdpBrowserSessionState session, BrowserOptions options)
        => session.ToDto("running") with
        {
            PanelControlAvailable = !session.IsRemote && string.IsNullOrWhiteSpace(options.RemoteCdpAddress) &&
                options.Enabled && options.EnablePanelControl
        };

    public async Task<BrowserPanelResult> ControlPanelAsync(
        BrowserTrustedCallerContext caller, BrowserPanelRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action == "end")
        {
            if (FindPanelLease(caller.SessionId, request.LeaseId) is { } ending) ending.Revoke();
            return PanelSuccess(active: false);
        }

        var options = _options.CurrentValue;
        if (caller.PlanModeActive)
            return PanelError("plan_mode_active", "只读计划模式下无法操作侧栏浏览器。");
        if (!options.Enabled || !options.EnablePanelControl)
            return PanelError("browser_panel_disabled", "侧栏浏览器操作不可用。");
        if (!string.IsNullOrWhiteSpace(options.RemoteCdpAddress))
            return PanelError("browser_panel_remote", "远端 CDP 会话仅支持预览。");
        if (request.Action == "start")
            return await StartPanelAsync(caller, ct).ConfigureAwait(false);

        var lease = FindPanelLease(caller.SessionId, request.LeaseId);
        if (lease is null || lease.Token.IsCancellationRequested)
            return InvalidPanelLease();
        var tabAction = request.Action switch
        {
            "tab_new" => "new",
            "tab_switch" => "switch",
            "tab_close" => "close",
            _ => null
        };
        if (tabAction is null && request.Action is not ("frame" or "navigate" or "click" or "wheel" or "key" or "text"))
            return InvalidPanelInput();
        if ((tabAction is "switch" or "close") && (string.IsNullOrWhiteSpace(request.TabId) || request.TabId.Length > 256))
            return InvalidPanelInput();
        if ((request.ViewportWidth is not null || request.ViewportHeight is not null) &&
            (request.Action != "frame" || request.ViewportWidth is not (>= 1 and <= MaxPanelWidth) ||
             request.ViewportHeight is not (>= 1 and <= MaxPanelHeight)))
            return InvalidPanelInput();
        if (!lease.PermitRequest(request.Action == "frame"))
            return PanelError("browser_panel_rate_limit", "浏览器操作过快。");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(request.Action == "navigate" || tabAction is not null ? 5 : 2));
        try
        {
            if (tabAction is not null)
            {
                var result = await TabsCoreAsync(caller, tabAction, request.TabId, null, lease, operation.Token).ConfigureAwait(false);
                return result.Succeeded ? PanelSuccess() : PanelError(result.ErrorCode ?? "browser_error", result.Summary ?? result.Output);
            }

            if (request.Action == "navigate")
            {
                if (request.Direction is not ("goto" or "back" or "forward" or "reload") ||
                    (request.Direction == "goto" && request.Url is not { Length: > 0 and <= 2048 }))
                    return InvalidPanelInput();

                var result = await NavigateCoreAsync(caller, request.Direction, request.Url, lease, operation.Token).ConfigureAwait(false);
                return result.Succeeded
                    ? PanelSuccess()
                    : PanelError(result.ErrorCode ?? "navigation_failed", result.Summary ?? result.Output);
            }

            if (!_sessions.TryGetValue(caller.SessionId, out var session))
                return InvalidPanelLease();
            await session.ExecutionLock.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                if (session.IsClosed || !ReferenceEquals(session.PanelLease, lease) || lease.Token.IsCancellationRequested)
                    return InvalidPanelLease();
                if (_sharedRuntime is not { IsAlive: true } runtime || session.GetActiveTab() is not { } tab)
                    return PanelError("browser_panel_frame_unavailable", "受管页面暂时不可用。");

                session.SyncWorkMode(caller.WorkMode);
                if (request.Action == "frame")
                {
                    lease.RenewHeartbeat();
                    if (request.ViewportWidth is int width && request.ViewportHeight is int height &&
                        (!lease.ResizedTargets.TryGetValue(tab.TargetId, out var previous) || previous != (width, height)))
                    {
                        lease.Viewport = null;
                        try
                        {
                            await SetTabViewportAsync(runtime.Connection, tab.CdpSessionId, width, height, operation.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // The command may have reached Chromium before cancellation. Restore on the next frame or on release.
                            lease.ResizedTargets[tab.TargetId] = (0, 0);
                            throw;
                        }
                        lease.ResizedTargets[tab.TargetId] = (width, height);
                    }
                    return await CapturePanelFrameAsync(runtime.Connection, session, tab, lease, operation.Token).ConfigureAwait(false);
                }

                return await DispatchPanelInputAsync(runtime.Connection, tab, lease, request, operation.Token).ConfigureAwait(false);
            }
            finally
            {
                session.ExecutionLock.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return lease.Token.IsCancellationRequested
                ? InvalidPanelLease()
                : PanelError("browser_busy", "浏览器操作超时，请稍后再试。");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return PanelError("browser_panel_frame_unavailable", "受管页面暂时不可用。");
        }
    }

    private BrowserPanelLease? FindPanelLease(string sessionId, string? nonce)
        => nonce is { Length: 32 } && _sessions.TryGetValue(sessionId, out var session) &&
            session.PanelLease is { } lease && string.Equals(lease.Id, nonce, StringComparison.Ordinal)
                ? lease : null;

    private async Task<BrowserPanelResult> StartPanelAsync(BrowserTrustedCallerContext caller, CancellationToken ct)
    {
        var (session, error) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (session is null)
            return PanelError(error?.ErrorCode ?? "browser_panel_frame_unavailable", error?.Summary ?? error?.Output ?? "浏览器不可用。");
        if (!await session.ExecutionLock.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            return PanelError("browser_busy", "浏览器正忙，请稍后再试。");
        try
        {
            if (session.IsRemote)
                return PanelError("browser_panel_remote", "远端 CDP 会话仅支持预览。");
            if (session.IsClosed || session.TakeoverActive)
                return PanelError("browser_busy", "浏览器正处于其他接管状态。");

            var runtime = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            _ = await EnsureActiveTabAsync(runtime, session, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var lease = new BrowserPanelLease();
            session.BeginPanelTakeover(lease);
            lease.Start(() => _ = Task.Run(() => FinishPanelAsync(session, lease)));
            await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
            return new BrowserPanelResult(new BrowserPanelResponse(true, lease.Id, ResponsiveViewport: true));
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    private async Task FinishPanelAsync(CdpBrowserSessionState session, BrowserPanelLease lease)
    {
        try
        {
            await session.ExecutionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!session.IsClosed && ReferenceEquals(session.PanelLease, lease))
                {
                    await RestorePanelViewportsAsync(session, lease).ConfigureAwait(false);
                    if (session.CompleteTakeover(note: null, panelLease: lease))
                        await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                session.ExecutionLock.Release();
            }
        }
        catch (ObjectDisposedException) when (session.IsClosed)
        {
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task RestorePanelViewportsAsync(CdpBrowserSessionState session, BrowserPanelLease lease)
    {
        if (lease.ResizedTargets.Count == 0 || _sharedRuntime is not { IsAlive: true } runtime)
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var options = _options.CurrentValue;
        var width = Math.Clamp(options.DefaultViewportWidth, 640, MaxPanelWidth);
        var height = Math.Clamp(options.DefaultViewportHeight, 480, MaxPanelHeight);
        foreach (var tab in session.GetTabsSnapshot())
        {
            if (!lease.ResizedTargets.ContainsKey(tab.TargetId) || timeout.IsCancellationRequested)
                continue;
            try
            {
                await SetTabViewportAsync(runtime.Connection, tab.CdpSessionId, width, height, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Restoring the default viewport failed for tab {TargetId}.", tab.TargetId);
            }
        }
    }

    private async Task<BrowserOperationResult> ClosePanelTabAsync(
        ICdpBrowserRuntime runtime, CdpBrowserSessionState session, BrowserPanelLease lease, string targetId, CancellationToken ct)
    {
        var tabs = session.GetTabsSnapshot();
        var tab = tabs.FirstOrDefault(item => item.TargetId == targetId);
        if (tab is null)
            return Fail("tab_not_found", "要关闭的标签页已不存在。");

        var wasActive = session.ActiveTargetId == targetId;
        // Create a replacement first so the context keeps a usable tab if Chromium refuses the close.
        if (tabs.Count == 1)
            _ = await CreateAndAttachTabAsync(runtime, session, makeActive: false, ct).ConfigureAwait(false);

        try
        {
            var closed = await runtime.Connection.SendJsonAsync("Target.closeTarget",
                new JsonObject { ["targetId"] = targetId }.ToJsonString(), ct: ct).ConfigureAwait(false);
            if (closed.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return Fail("browser_error", "浏览器暂时无法关闭该标签页。");

            _trackedTargets.TryRemove(targetId, out _);
            session.RemoveTab(targetId, out _);
            _cdpSessionToAppSession.TryRemove(tab.CdpSessionId, out _);
            foreach (var frame in tab.ChildFrames)
            {
                _cdpSessionToAppSession.TryRemove(frame.CdpSessionId, out _);
                _trackedTargets.TryRemove(frame.TargetId, out _);
            }
            lease.ResizedTargets.Remove(targetId);
            if (wasActive && session.GetActiveTab() is { } next)
                await runtime.Connection.SendJsonAsync("Target.activateTarget",
                    new JsonObject { ["targetId"] = next.TargetId }.ToJsonString(), ct: ct).ConfigureAwait(false);

            session.RecordAction("browser_tabs", "close", targetId, tab.Url, true, "关闭标签页", 0);
            return new BrowserOperationResult(true, "已关闭标签页。");
        }
        finally
        {
            await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task<JsonElement> SetTabViewportAsync(
        CdpConnection connection, string cdpSessionId, int width, int height, CancellationToken ct)
        => connection.SendJsonAsync("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false
        }.ToJsonString(), cdpSessionId, ct: ct);

    private async Task<BrowserPanelResult> CapturePanelFrameAsync(
        CdpConnection connection, CdpBrowserSessionState session, BrowserTabRecord tab,
        BrowserPanelLease lease, CancellationToken ct)
    {
        var viewport = await ReadPanelViewportAsync(connection, tab, ct).ConfigureAwait(false);
        var screenshot = await TryCaptureViewportJpegAsync(connection, session, tab, ct, panelFrame: true).ConfigureAwait(false);
        if (screenshot is null || screenshot.Bytes.Length > MaxPanelImageBytes || viewport !=
            await ReadPanelViewportAsync(connection, tab, ct).ConfigureAwait(false))
            return PanelError("browser_panel_frame_unavailable", "画面正在变化，请稍后重试。");
        if (lease.Viewport != viewport)
        {
            lease.Viewport = viewport;
            lease.ViewId = Guid.NewGuid().ToString("N");
        }

        return new BrowserPanelResult(new BrowserPanelResponse(true, Frame: new BrowserPanelFrame(
            lease.ViewId!, viewport.Width, viewport.Height, Convert.ToBase64String(screenshot.Bytes.Span), tab.Url, tab.Title)));
    }

    private static async Task<BrowserPanelViewport> ReadPanelViewportAsync(
        CdpConnection connection, BrowserTabRecord tab, CancellationToken ct)
    {
        var layout = await connection.SendAsync("Page.getLayoutMetrics", sessionId: tab.CdpSessionId, ct: ct).ConfigureAwait(false);
        var viewport = layout.GetProperty("cssVisualViewport");
        return new BrowserPanelViewport(tab.TargetId, Interlocked.Read(ref tab.NavigationRevision),
            viewport.GetProperty("clientWidth").GetDouble(), viewport.GetProperty("clientHeight").GetDouble(),
            viewport.GetProperty("pageX").GetDouble(), viewport.GetProperty("pageY").GetDouble(), viewport.GetProperty("scale").GetDouble());
    }

    private static async Task<BrowserPanelResult> DispatchPanelInputAsync(
        CdpConnection connection, BrowserTabRecord tab, BrowserPanelLease lease, BrowserPanelRequest request, CancellationToken ct)
    {
        if (lease.Viewport is not { } expected || string.IsNullOrEmpty(request.ViewId) || request.ViewId != lease.ViewId ||
            expected != await ReadPanelViewportAsync(connection, tab, ct).ConfigureAwait(false))
            return PanelError("browser_panel_stale_view", "画面已变化，请在更新后继续操作。");

        var id = tab.CdpSessionId;
        if (request.Action == "key")
        {
            if (request.Key is not ("Tab" or "Enter" or "Escape" or "Backspace" or "Delete" or "ArrowUp" or "ArrowDown" or "ArrowLeft" or "ArrowRight"))
                return InvalidPanelInput();
            await DispatchKeyPressAsync(connection, id, request.Key, ct).ConfigureAwait(false);
        }
        else if (request.Action == "text")
        {
            if (request.Text is not { Length: > 0 and <= 1024 })
                return InvalidPanelInput();
            await connection.SendJsonAsync("Input.insertText",
                new JsonObject { ["text"] = request.Text }.ToJsonString(), id, ct: ct).ConfigureAwait(false);
        }
        else
        {
            if (request.X is not double x || request.Y is not double y ||
                !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= expected.Width || y >= expected.Height)
                return InvalidPanelInput();
            if (request.Action == "click")
            {
                await DispatchMouseMoveAsync(connection, id, x, y, ct).ConfigureAwait(false);
                await DispatchMouseClickAsync(connection, id, x, y, 1, ct).ConfigureAwait(false);
            }
            else
            {
                if (request.DeltaX is not double dx || request.DeltaY is not double dy ||
                    !double.IsFinite(dx) || !double.IsFinite(dy) || Math.Abs(dx) > 1200 || Math.Abs(dy) > 1200)
                    return InvalidPanelInput();
                await connection.SendJsonAsync("Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mouseWheel", ["x"] = x, ["y"] = y, ["deltaX"] = dx, ["deltaY"] = dy
                }.ToJsonString(), id, ct: ct).ConfigureAwait(false);
            }
        }
        return PanelSuccess();
    }

    private static BrowserPanelResult PanelSuccess(bool active = true) => new(new BrowserPanelResponse(active));

    private static BrowserPanelResult PanelError(string error, string message) => new(null, error, message);
    private static BrowserPanelResult InvalidPanelInput() => PanelError("browser_panel_invalid_input", "浏览器操作参数无效。");
    private static BrowserPanelResult InvalidPanelLease() => PanelError("browser_panel_lease_invalid", "侧栏接管已结束。");
}
