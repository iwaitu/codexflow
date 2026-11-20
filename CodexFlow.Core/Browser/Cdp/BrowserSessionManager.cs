// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Browser.Cdp;

/// <summary>
/// Manages a single shared headless Chromium process with per-session isolated <c>Target.createBrowserContext</c>
/// instances, per-session outbound SSRF proxies, isolated-world DOM inspection, live password/clickjacking/TOCTOU
/// defenses, throttled SSE state emission, and idle timeout reclamation.
/// </summary>
public sealed class BrowserSessionManager : IBrowserSessionManager
{
    private const string IsolatedWorldName = "__codexflow_isolated__";
    private static readonly TimeSpan StatePublishThrottle = TimeSpan.FromMilliseconds(300);

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly IBrowserExecutableLocator _locator;
    private readonly BrowserTargetPolicyGuard _policyGuard;
    private readonly ProtectedLocalPortRegistry _portRegistry;
    private readonly IBrowserApprovalCoordinator? _approvalCoordinator;
    private readonly IBrowserStateEventPublisher? _eventPublisher;
    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly string _runtimeBaseDir;

    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CdpBrowserSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _cdpSessionToAppSession = new(StringComparer.Ordinal);
    private CdpManagedBrowserProcess? _sharedProcess;
    private int _disposed;

    public BrowserSessionManager(
        IOptionsMonitor<BrowserOptions> options,
        IBrowserExecutableLocator locator,
        BrowserTargetPolicyGuard policyGuard,
        ProtectedLocalPortRegistry portRegistry,
        ILogger<BrowserSessionManager> logger,
        IBrowserApprovalCoordinator? approvalCoordinator = null,
        IBrowserStateEventPublisher? eventPublisher = null,
        string? runtimeBaseDir = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(policyGuard);
        ArgumentNullException.ThrowIfNull(portRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _locator = locator;
        _policyGuard = policyGuard;
        _portRegistry = portRegistry;
        _approvalCoordinator = approvalCoordinator;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _runtimeBaseDir = string.IsNullOrWhiteSpace(runtimeBaseDir)
            ? Path.Combine(CodexFlowPaths.GlobalDirectory(), "browser-runtime")
            : runtimeBaseDir;

        CdpManagedBrowserProcess.ReapOrphanedRuntimeDirectories(_runtimeBaseDir, _logger);
    }

    public async Task<BrowserOperationResult> NavigateAsync(
        BrowserTrustedCallerContext caller,
        string action,
        string? url,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "goto" : action.Trim().ToLowerInvariant();
        if (normalizedAction is not ("goto" or "back" or "forward" or "reload"))
        {
            return Fail("invalid_action", $"不支持的导航动作 '{action}'，允许值: goto, back, forward, reload。");
        }

        if (normalizedAction == "goto" && string.IsNullOrWhiteSpace(url))
        {
            return Fail("missing_url", "执行 goto 导航时必须提供 url 参数。");
        }

        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        int? oneTimePortGranted = null;
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭，请重试。");
            }

            session.Touch();
            var proc = await EnsureProcessAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            if (normalizedAction == "goto")
            {
                var approvedPorts = session.GetApprovedLoopbackPortsSnapshot();
                var eval = _policyGuard.ValidateNavigationUrl(url, caller.WorkMode, approvedPorts);
                if (!eval.Allowed && eval.RequiresLoopbackPortApproval && eval.NormalizedUri is not null)
                {
                    var approvalResult = await RequestLoopbackPortApprovalAsync(session, eval.NormalizedUri, eval.TargetPort, ct).ConfigureAwait(false);
                    if (!approvalResult.Approved)
                    {
                        var reason = approvalResult.ErrorMessage ?? eval.Message ?? "访问非白名单本机端口未获批准。";
                        session.RecordAction("browser_navigate", "goto", SanitizeOneLine(url, 200), tab.Url, false, reason, sw.ElapsedMilliseconds);
                        await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                        return Fail(approvalResult.ErrorCode ?? BrowserTargetPolicyGuard.ReasonLoopbackApprovalRequired, reason);
                    }

                    if (approvalResult.GrantedForSession)
                    {
                        session.ApproveLoopbackPortForSession(eval.TargetPort);
                    }
                    else
                    {
                        session.GrantOneTimeLoopbackPort(eval.TargetPort);
                        oneTimePortGranted = eval.TargetPort;
                    }

                    eval = _policyGuard.ValidateNavigationUrl(url, caller.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
                }

                if (!eval.Allowed || eval.NormalizedUri is null)
                {
                    session.RecordAction("browser_navigate", "goto", SanitizeOneLine(url, 200), tab.Url, false, eval.Message, sw.ElapsedMilliseconds);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return Fail(eval.ReasonCode ?? "policy_denied", eval.Message ?? "URL 被浏览器安全策略拦截。");
                }

                var navJson = JsonSerializer.Serialize(new { url = eval.NormalizedUri.AbsoluteUri });
                var navResult = await proc.Connection.SendJsonAsync("Page.navigate", navJson, tab.CdpSessionId, ct).ConfigureAwait(false);
                if (navResult.TryGetProperty("errorText", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                {
                    var navErr = errProp.GetString()!;
                    session.RecordAction("browser_navigate", "goto", eval.NormalizedUri.AbsoluteUri, tab.Url, false, navErr, sw.ElapsedMilliseconds);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return Fail("navigation_failed", $"导航失败: {navErr}");
                }
            }
            else if (normalizedAction == "reload")
            {
                await proc.Connection.SendJsonAsync("Page.reload", "{\"ignoreCache\":false}", tab.CdpSessionId, ct).ConfigureAwait(false);
            }
            else
            {
                var history = await proc.Connection.SendAsync("Page.getNavigationHistory", sessionId: tab.CdpSessionId, ct: ct).ConfigureAwait(false);
                if (history.TryGetProperty("currentIndex", out var idxProp) &&
                    history.TryGetProperty("entries", out var entriesProp) &&
                    entriesProp.ValueKind == JsonValueKind.Array)
                {
                    var currentIndex = idxProp.GetInt32();
                    var targetIndex = normalizedAction == "back" ? currentIndex - 1 : currentIndex + 1;
                    var entriesCount = entriesProp.GetArrayLength();
                    if (targetIndex < 0 || targetIndex >= entriesCount)
                    {
                        return Fail("history_boundary", normalizedAction == "back" ? "已到达浏览器历史最前页，无法后退。" : "已到达浏览器历史最新页，无法前进。");
                    }

                    var targetEntry = entriesProp[targetIndex];
                    var entryUrl = targetEntry.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(entryUrl) && !string.Equals(entryUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        var historyEval = _policyGuard.ValidateNavigationUrl(entryUrl, caller.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
                        if (!historyEval.Allowed)
                        {
                            return Fail(historyEval.ReasonCode ?? "policy_denied", $"历史记录目标 '{entryUrl}' 不符合当前安全策略: {historyEval.Message}");
                        }
                    }

                    var entryId = targetEntry.GetProperty("id").GetInt32();
                    await proc.Connection.SendJsonAsync(
                        "Page.navigateToHistoryEntry",
                        string.Create(CultureInfo.InvariantCulture, $"{{\"entryId\":{entryId}}}"),
                        tab.CdpSessionId,
                        ct).ConfigureAwait(false);
                }
            }

            session.ClearTabElementRefs(tab.TargetId);
            await WaitForDocumentSettleAsync(proc.Connection, tab, ct).ConfigureAwait(false);

            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            var screenshot = await TryCaptureViewportJpegAsync(proc.Connection, session, tab.CdpSessionId, ct).ConfigureAwait(false);

            session.Touch();
            session.RecordAction(
                "browser_navigate",
                normalizedAction,
                SanitizeOneLine(url ?? normalizedAction, 200),
                tab.Url,
                true,
                SanitizeOneLine(string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title, 140),
                sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);

            var formatted = BrowserSnapshotScript.FormatSafeSnapshotForModel(snapshot, maxChars: 6000);
            var summary = $"已导航至 {tab.Url}" + (string.IsNullOrWhiteSpace(tab.Title) ? string.Empty : $" ({tab.Title})");
            return new BrowserOperationResult(
                Succeeded: true,
                Output: $"{summary}\n\n{formatted}",
                Summary: summary,
                Url: tab.Url,
                Title: tab.Title,
                Screenshot: screenshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "browser_navigate failed for session {SessionId}", caller.SessionId);
            session.RecordAction("browser_navigate", normalizedAction, SanitizeOneLine(url, 200), session.GetActiveTab()?.Url, false, ex.Message, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
            return Fail("browser_error", $"浏览器导航失败: {ex.Message}");
        }
        finally
        {
            if (oneTimePortGranted is { } grantedPort)
            {
                session.RevokeOneTimeLoopbackPort(grantedPort);
            }

            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> SnapshotAsync(
        BrowserTrustedCallerContext caller,
        int maxChars = 12000,
        bool includeConsole = false,
        bool includeNetworkErrors = false,
        bool includeScreenshot = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭，请重试。");
            }

            session.Touch();
            var proc = await EnsureProcessAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            BrowserScreenshotData? screenshot = null;
            if (includeScreenshot)
            {
                screenshot = await TryCaptureViewportJpegAsync(proc.Connection, session, tab.CdpSessionId, ct).ConfigureAwait(false);
            }

            var console = includeConsole ? session.GetRecentConsoleEntries() : null;
            var netErrors = includeNetworkErrors ? session.GetRecentNetworkErrors() : null;
            var formatted = BrowserSnapshotScript.FormatSafeSnapshotForModel(snapshot, maxChars, console, netErrors);

            var elemCount = snapshot.Elements?.Count ?? 0;
            var summary = string.Create(CultureInfo.InvariantCulture, $"已获取页面快照 ({elemCount} 个可交互元素): {tab.Url}");
            session.Touch();
            session.RecordAction("browser_snapshot", includeScreenshot ? "snapshot+screenshot" : "snapshot", null, tab.Url, true, summary, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: includeScreenshot, ct: ct).ConfigureAwait(false);

            return new BrowserOperationResult(
                Succeeded: true,
                Output: $"{summary}\n\n{formatted}",
                Summary: summary,
                Url: tab.Url,
                Title: tab.Title,
                Screenshot: screenshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "browser_snapshot failed for session {SessionId}", caller.SessionId);
            return Fail("browser_error", $"获取页面快照失败: {ex.Message}");
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> ActAsync(
        BrowserTrustedCallerContext caller,
        BrowserActRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (caller.PlanModeActive)
        {
            return Fail("plan_mode_active", "当前处于只读 Plan Mode，禁止执行 browser_act 页面交互。");
        }

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("click" or "double_click" or "hover" or "type" or "clear" or "press_key" or "select_option" or "scroll" or "wait"))
        {
            return Fail("invalid_action", $"不支持的交互动作 '{request.Action}'，允许值: click, double_click, hover, type, clear, press_key, select_option, scroll, wait。");
        }

        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭，请重试。");
            }

            session.Touch();
            var proc = await EnsureProcessAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            CdpSnapshotElement? targetElement = null;
            CdpLiveRefProbe? preActionLiveProbe = null;
            CdpActiveElementProbe? activeProbeForKey = null;

            if (action is "click" or "double_click" or "hover" or "type" or "clear" or "select_option")
            {
                if (string.IsNullOrWhiteSpace(request.Ref))
                {
                    return Fail("missing_ref", $"动作 '{action}' 必须提供元素 ref 编号（请先调用 browser_snapshot 获取 [e1] 等编号）。");
                }

                var refId = request.Ref.Trim();
                if (!session.TryGetTabElementRef(tab.TargetId, refId, out targetElement) || targetElement is null)
                {
                    return Fail("stale_ref", $"元素编号 '{request.Ref}' 不存在或已随页面导航失效，请重新调用 browser_snapshot 获取最新编号。");
                }

                // P0-2: Always verify live DOM in isolated world before any interaction (prevents TOCTOU & clickjacking).
                preActionLiveProbe = await LocateAndVerifyRefInIsolatedWorldAsync(proc.Connection, tab, refId, ct).ConfigureAwait(false);
                if (preActionLiveProbe is null || !preActionLiveProbe.Ok)
                {
                    return Fail("stale_ref", $"元素 [{refId}] 已从页面消失或不可见 ({preActionLiveProbe?.Error ?? "not_found"})，请重新调用 browser_snapshot。");
                }

                if (action is "click" or "double_click" or "type" or "clear" && preActionLiveProbe.Obscured)
                {
                    return Fail("element_obscured", $"元素 [{refId}] 当前被其他浮层遮挡（可能存在弹窗遮罩或点击劫持），已拒绝操作，请先重新快照检查页面。");
                }

                // Enforce password/OTP prohibition using BOTH snapshot AND live DOM probe.
                if (action is "type" or "clear" && (targetElement.IsPassword || preActionLiveProbe.IsPassword))
                {
                    return Fail(
                        "password_input_forbidden",
                        $"安全策略禁止 AI 直接向密码、支付卡或验证码/OTP 字段 [{targetElement.Ref}] 输入内容。请提示用户自行处理登录/验证。");
                }
            }
            else if (action == "press_key")
            {
                if (string.IsNullOrWhiteSpace(request.Key))
                {
                    return Fail("missing_key", "动作 'press_key' 必须提供 key 参数（例如 Enter、Escape、Tab）。");
                }

                // P0-1: Inspect live activeElement in isolated world before sending keys.
                var keyTrimmed = request.Key.Trim();
                activeProbeForKey = await InspectActiveElementInIsolatedWorldAsync(proc.Connection, tab, ct).ConfigureAwait(false);
                var isSafeNavigationKey = keyTrimmed is "Escape" or "Tab";
                if (!isSafeNavigationKey && activeProbeForKey is { IsPassword: true })
                {
                    return Fail(
                        "password_input_forbidden",
                        "安全策略禁止通过 press_key 向当前聚焦的密码/OTP/支付敏感字段输入字符。请提示用户自行输入。");
                }

                if (!isSafeNavigationKey && activeProbeForKey is { InIframe: true })
                {
                    return Fail(
                        "iframe_input_forbidden",
                        "当前焦点位于 iframe 内部，无法校验目标输入框是否为密码/敏感字段，已拒绝 press_key 输入。");
                }
            }

            var urlBeforeApproval = tab.Url;
            var isSensitive =
                targetElement?.IsSensitiveSubmit == true ||
                preActionLiveProbe?.IsSensitiveSubmit == true ||
                (action == "press_key" &&
                 IsSubmitTriggerKey(request.Key) &&
                 activeProbeForKey?.IsSensitiveSubmitContext == true);

            // Approval gate for sensitive interactions or AlwaysAsk policy.
            var (approvalRefusal, waitedForApproval) = await EnforceInteractionApprovalAsync(
                caller,
                session,
                tab,
                action,
                request,
                targetElement,
                preActionLiveProbe,
                activeProbeForKey,
                isSensitive,
                ct).ConfigureAwait(false);
            if (approvalRefusal is not null)
            {
                session.RecordAction("browser_act", action, SanitizeOneLine(request.Ref ?? request.Key, 80), tab.Url, false, approvalRefusal.Output, sw.ElapsedMilliseconds);
                await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                return approvalRefusal;
            }

            // P1-5: Re-verify URL origin and live DOM target after human approval to eliminate TOCTOU.
            if (waitedForApproval)
            {
                var currentOrigin = ExtractOrigin(tab.Url);
                var priorOrigin = ExtractOrigin(urlBeforeApproval);
                if (!string.Equals(currentOrigin, priorOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("stale_ref", "等待审批期间页面已跳转至其他站点，已取消本次交互以防止越权操作，请重新快照。");
                }

                if (targetElement is not null)
                {
                    var postApprovalProbe = await LocateAndVerifyRefInIsolatedWorldAsync(proc.Connection, tab, targetElement.Ref, ct).ConfigureAwait(false);
                    if (postApprovalProbe is null || !postApprovalProbe.Ok || postApprovalProbe.Obscured ||
                        (action is "type" or "clear" && postApprovalProbe.IsPassword) ||
                        !string.Equals(postApprovalProbe.Role, preActionLiveProbe?.Role, StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("stale_ref", $"等待审批期间元素 [{targetElement.Ref}] 状态已发生变化，已中止执行，请重新调用 browser_snapshot。");
                    }

                    preActionLiveProbe = postApprovalProbe;
                }
            }

            switch (action)
            {
                case "wait":
                {
                    var ms = Math.Clamp(request.TimeoutMs ?? 1000, 100, 15000);
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                    break;
                }
                case "scroll":
                {
                    var deltaY = Math.Clamp(request.DeltaY ?? 500, -4000, 4000);
                    var expr = string.Create(CultureInfo.InvariantCulture, $"window.scrollBy(0, {deltaY}); window.scrollY;");
                    await EvaluateInIsolatedWorldAsync(proc.Connection, tab, expr, ct).ConfigureAwait(false);
                    break;
                }
                case "press_key":
                {
                    await DispatchKeyPressAsync(proc.Connection, tab.CdpSessionId, request.Key!.Trim(), ct).ConfigureAwait(false);
                    break;
                }
                case "hover":
                case "click":
                case "double_click":
                {
                    var x = preActionLiveProbe!.X;
                    var y = preActionLiveProbe.Y;
                    await DispatchMouseMoveAsync(proc.Connection, tab.CdpSessionId, x, y, ct).ConfigureAwait(false);
                    if (action is "click" or "double_click")
                    {
                        var clickCount = action == "double_click" ? 2 : 1;
                        await DispatchMouseClickAsync(proc.Connection, tab.CdpSessionId, x, y, clickCount, ct).ConfigureAwait(false);
                    }
                    break;
                }
                case "clear":
                case "type":
                {
                    if (action == "type" && request.Text is null)
                    {
                        return Fail("missing_text", "动作 'type' 必须提供 text 参数。");
                    }

                    var x = preActionLiveProbe!.X;
                    var y = preActionLiveProbe.Y;
                    await DispatchMouseClickAsync(proc.Connection, tab.CdpSessionId, x, y, 1, ct).ConfigureAwait(false);

                    var clearRes = await EvaluateInIsolatedWorldAsync(
                        proc.Connection,
                        tab,
                        BrowserSnapshotScript.BuildClearEditableRefJs(EscapeJsString(targetElement!.Ref)),
                        ct).ConfigureAwait(false);

                    if (string.Equals(clearRes, "password_forbidden", StringComparison.Ordinal))
                    {
                        return Fail("password_input_forbidden", $"元素 [{targetElement.Ref}] 为密码或敏感字段，禁止输入。");
                    }

                    if (!string.Equals(clearRes, "ok_input", StringComparison.Ordinal) &&
                        !string.Equals(clearRes, "ok_contenteditable", StringComparison.Ordinal))
                    {
                        return Fail("not_editable", $"元素 [{targetElement.Ref}] 当前不可编辑 ({clearRes ?? "unknown"})，请重新快照。");
                    }

                    // P0-2: Re-verify live activeElement right before Input.insertText to stop focus-steal attacks.
                    var focusedBeforeInsert = await InspectActiveElementInIsolatedWorldAsync(proc.Connection, tab, ct).ConfigureAwait(false);
                    if (focusedBeforeInsert is { IsPassword: true } or { InIframe: true } || focusedBeforeInsert is not { IsEditable: true })
                    {
                        return Fail("password_input_forbidden", "点击后实际获得焦点的元素不是安全的非密码可编辑输入框，已中止输入。");
                    }

                    await DispatchKeyPressAsync(proc.Connection, tab.CdpSessionId, "Backspace", ct).ConfigureAwait(false);

                    if (action == "type" && !string.IsNullOrEmpty(request.Text))
                    {
                        var insertJson = JsonSerializer.Serialize(new { text = request.Text });
                        await proc.Connection.SendJsonAsync("Input.insertText", insertJson, tab.CdpSessionId, ct).ConfigureAwait(false);
                    }
                    break;
                }
                case "select_option":
                {
                    if (string.IsNullOrWhiteSpace(request.Text))
                    {
                        return Fail("missing_text", "动作 'select_option' 必须通过 text 参数指定要选择的选项值或文本。");
                    }

                    var selectRes = await EvaluateInIsolatedWorldAsync(
                        proc.Connection,
                        tab,
                        BrowserSnapshotScript.BuildSelectOptionRefJs(
                            EscapeJsString(targetElement!.Ref),
                            EscapeJsString(request.Text.Trim())),
                        ct).ConfigureAwait(false);

                    if (selectRes is null || !selectRes.StartsWith("ok:", StringComparison.Ordinal))
                    {
                        return Fail("select_failed", $"下拉框选择失败 ({selectRes ?? "unknown"})，请检查 ref 与选项值。");
                    }
                    break;
                }
            }

            await WaitForDocumentSettleAsync(proc.Connection, tab, ct, maxWaitMs: 2500).ConfigureAwait(false);
            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            var screenshot = await TryCaptureViewportJpegAsync(proc.Connection, session, tab.CdpSessionId, ct).ConfigureAwait(false);

            var targetLabel = targetElement is not null
                ? $"[{targetElement.Ref}] {targetElement.Role} \"{SanitizeOneLine(targetElement.Name, 60)}\""
                : SanitizeOneLine(request.Key ?? request.DeltaY?.ToString(CultureInfo.InvariantCulture), 60);
            var summary = $"已执行 {action}" + (string.IsNullOrWhiteSpace(targetLabel) ? string.Empty : $" ({targetLabel})") + $" -> {tab.Url}";
            session.Touch();
            session.RecordAction("browser_act", action, targetLabel, tab.Url, true, summary, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);

            var formatted = BrowserSnapshotScript.FormatSafeSnapshotForModel(snapshot, maxChars: 6000);
            return new BrowserOperationResult(
                Succeeded: true,
                Output: $"{summary}\n\n{formatted}",
                Summary: summary,
                Url: tab.Url,
                Title: tab.Title,
                Screenshot: screenshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "browser_act ({Action}) failed for session {SessionId}", action, caller.SessionId);
            session.RecordAction("browser_act", action, SanitizeOneLine(request.Ref ?? request.Key, 80), session.GetActiveTab()?.Url, false, ex.Message, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
            return Fail("browser_error", $"浏览器交互失败: {ex.Message}");
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> TabsAsync(
        BrowserTrustedCallerContext caller,
        string action,
        string? tabId = null,
        string? url = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var normalized = string.IsNullOrWhiteSpace(action) ? "list" : action.Trim().ToLowerInvariant();
        if (normalized == "close_browser")
        {
            await CloseSessionAsync(caller.SessionId, "closed_by_tool", ct).ConfigureAwait(false);
            return new BrowserOperationResult(true, "已关闭当前会话的受管浏览器上下文并清理隔离状态。", Summary: "已关闭浏览器会话");
        }

        if (normalized == "list" && !_sessions.ContainsKey(caller.SessionId))
        {
            return new BrowserOperationResult(true, "当前会话尚未启动浏览器（0 个标签页）。", Summary: "无活跃浏览器标签页");
        }

        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭。");
            }

            session.Touch();
            var proc = await EnsureProcessAsync(ct).ConfigureAwait(false);

            switch (normalized)
            {
                case "list":
                {
                    var state = session.ToDto("running", _options.CurrentValue.ProfileMode.ToString());
                    var sb = new StringBuilder();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"当前会话共有 {state.Tabs.Count} 个标签页:");
                    foreach (var t in state.Tabs)
                    {
                        var marker = t.Active ? "*" : " ";
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  {marker} [{t.TabId}] {BrowserSnapshotScript.StripForgedVisualMarkers(t.Title)} ({BrowserSnapshotScript.StripForgedVisualMarkers(t.Url)})");
                    }

                    return new BrowserOperationResult(true, UntrustedText.Wrap("browser_tabs", sb.ToString().TrimEnd()), Summary: $"共 {state.Tabs.Count} 个标签页");
                }
                case "new":
                {
                    Uri? targetUri = null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var eval = _policyGuard.ValidateNavigationUrl(url, caller.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
                        if (!eval.Allowed || eval.NormalizedUri is null)
                        {
                            return Fail(eval.ReasonCode ?? "policy_denied", eval.Message ?? "新标签页 URL 被安全策略拦截。");
                        }

                        targetUri = eval.NormalizedUri;
                    }

                    // P1-12: Always create on about:blank so Fetch.enable and domains are armed BEFORE any navigation.
                    var tab = await CreateAndAttachTabAsync(proc, session, makeActive: true, ct).ConfigureAwait(false);
                    if (targetUri is not null)
                    {
                        var navJson = JsonSerializer.Serialize(new { url = targetUri.AbsoluteUri });
                        await proc.Connection.SendJsonAsync("Page.navigate", navJson, tab.CdpSessionId, ct).ConfigureAwait(false);
                        await WaitForDocumentSettleAsync(proc.Connection, tab, ct).ConfigureAwait(false);
                        _ = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
                        _ = await TryCaptureViewportJpegAsync(proc.Connection, session, tab.CdpSessionId, ct).ConfigureAwait(false);
                    }

                    session.RecordAction("browser_tabs", "new", tab.TargetId, tab.Url, true, $"新建标签页 {tab.TargetId}", 0);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return new BrowserOperationResult(true, $"已新建并切换到标签页 [{tab.TargetId}] ({tab.Url})。", Summary: $"新建标签页 {tab.TargetId}", Url: tab.Url, Title: tab.Title);
                }
                case "switch":
                {
                    if (string.IsNullOrWhiteSpace(tabId))
                    {
                        return Fail("missing_tab_id", "切换标签页必须提供 tab_id。");
                    }

                    if (!session.TrySetActiveTab(tabId.Trim(), out var targetTab) || targetTab is null)
                    {
                        return Fail("tab_not_found", $"未找到标签页 '{tabId}'。");
                    }

                    await proc.Connection.SendJsonAsync("Target.activateTarget", JsonSerializer.Serialize(new { targetId = targetTab.TargetId }), ct: ct).ConfigureAwait(false);
                    _ = await TryCaptureViewportJpegAsync(proc.Connection, session, targetTab.CdpSessionId, ct).ConfigureAwait(false);
                    session.RecordAction("browser_tabs", "switch", targetTab.TargetId, targetTab.Url, true, $"切换至 {targetTab.TargetId}", 0);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return new BrowserOperationResult(true, $"已切换到标签页 [{targetTab.TargetId}] ({targetTab.Url})。", Summary: $"切换标签页 {targetTab.TargetId}", Url: targetTab.Url, Title: targetTab.Title);
                }
                case "close":
                {
                    var closeTargetId = string.IsNullOrWhiteSpace(tabId) ? session.ActiveTargetId : tabId.Trim();
                    if (string.IsNullOrWhiteSpace(closeTargetId) || !session.RemoveTab(closeTargetId, out var removed) || removed is null)
                    {
                        return Fail("tab_not_found", $"未找到要关闭的标签页 '{tabId ?? "(active)"}'。");
                    }

                    _cdpSessionToAppSession.TryRemove(removed.CdpSessionId, out _);
                    try
                    {
                        await proc.Connection.SendJsonAsync("Target.closeTarget", JsonSerializer.Serialize(new { targetId = removed.TargetId }), ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    session.RecordAction("browser_tabs", "close", removed.TargetId, removed.Url, true, $"关闭标签页 {removed.TargetId}", 0);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return new BrowserOperationResult(true, $"已关闭标签页 [{removed.TargetId}]。", Summary: $"关闭标签页 {removed.TargetId}");
                }
                default:
                    return Fail("invalid_action", $"不支持的标签页动作 '{action}'，允许值: list, new, switch, close, close_browser。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "browser_tabs ({Action}) failed for session {SessionId}", normalized, caller.SessionId);
            return Fail("browser_error", $"标签页操作失败: {ex.Message}");
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    public BrowserSessionStateDto GetSessionState(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) && !session.IsClosed)
        {
            return session.ToDto("running", _options.CurrentValue.ProfileMode.ToString());
        }

        return new BrowserSessionStateDto(
            SessionId: sessionId ?? string.Empty,
            Status: "idle",
            Headless: _options.CurrentValue.Headless,
            ProfileMode: _options.CurrentValue.ProfileMode.ToString(),
            Url: null,
            Title: null,
            ScreenshotSeq: 0,
            ConsoleErrorCount: 0,
            Tabs: [],
            RecentActions: [],
            RecentConsole: [],
            UpdatedAtUtc: null);
    }

    public string? GetSessionGenerationId(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) && !session.IsClosed
            ? session.GenerationId
            : null;

    public bool TryGetLatestScreenshot(string sessionId, out BrowserScreenshotData? screenshot)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) && !session.IsClosed)
        {
            return session.TryGetLatestScreenshot(out screenshot);
        }

        screenshot = null;
        return false;
    }

    public async Task CloseSessionAsync(string sessionId, string reason = "closed", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _approvalCoordinator?.CancelSessionApprovals(sessionId, reason);
        _approvalCoordinator?.ClearSessionAuthorizations(sessionId);

        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        session.MarkClosed();

        foreach (var kvp in _cdpSessionToAppSession)
        {
            if (string.Equals(kvp.Value, sessionId, StringComparison.Ordinal))
            {
                _cdpSessionToAppSession.TryRemove(kvp.Key, out _);
            }
        }

        try
        {
            var proc = _sharedProcess;
            if (proc is not null && proc.IsAlive)
            {
                using var disposeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                disposeCts.CancelAfter(TimeSpan.FromSeconds(5));
                await proc.Connection.SendJsonAsync(
                    "Target.disposeBrowserContext",
                    JsonSerializer.Serialize(new { browserContextId = session.BrowserContextId }),
                    ct: disposeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing BrowserContext {ContextId} for session {SessionId}", session.BrowserContextId, sessionId);
        }

        await session.DisposeAsync().ConfigureAwait(false);

        if (_eventPublisher is not null)
        {
            var closedDto = new BrowserSessionStateDto(
                SessionId: sessionId,
                Status: "closed",
                Headless: _options.CurrentValue.Headless,
                ProfileMode: _options.CurrentValue.ProfileMode.ToString(),
                Url: null,
                Title: null,
                ScreenshotSeq: 0,
                ConsoleErrorCount: 0,
                Tabs: [],
                RecentActions: [],
                RecentConsole: [],
                UpdatedAtUtc: DateTimeOffset.UtcNow);
            try
            {
                await _eventPublisher.PublishStateUpdatedAsync(sessionId, closedDto, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed publishing closed browser state for session {SessionId}", sessionId);
            }
        }
    }

    public async Task CleanupIdleSessionsAsync(CancellationToken ct = default)
    {
        var timeoutMinutes = Math.Clamp(_options.CurrentValue.IdleTimeoutMinutes, 1, 120);
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(timeoutMinutes);

        foreach (var kvp in _sessions)
        {
            // P1-9: Never reap a session that is currently executing a CDP command or awaiting human approval.
            if (kvp.Value.ExecutionLock.CurrentCount == 0)
            {
                continue;
            }

            if (kvp.Value.LastActivityUtc < cutoff)
            {
                _logger.LogInformation("Closing idle browser session {SessionId} (last active {LastActive})", kvp.Key, kvp.Value.LastActivityUtc);
                await CloseSessionAsync(kvp.Key, "idle_timeout", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(CdpBrowserSessionState? Session, BrowserOperationResult? Error)> AcquireSessionAsync(
        BrowserTrustedCallerContext caller,
        CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            return (null, Fail("browser_disabled", "Web Browser Use 功能当前已关闭，请在「设置 → 浏览器」中启用。"));
        }

        if (_sessions.TryGetValue(caller.SessionId, out var existing) && !existing.IsClosed)
        {
            // P1-1: Keep session WorkMode synchronized with caller so Dev->Daily tightening takes immediate effect.
            existing.SyncWorkMode(caller.WorkMode);
            return (existing, null);
        }

        var maxSessions = Math.Clamp(opts.MaxConcurrentSessions, 1, 8);
        if (_sessions.Count >= maxSessions)
        {
            var oldest = _sessions.Values
                .OrderBy(static s => s.LastActivityUtc)
                .FirstOrDefault(static s => s.ExecutionLock.CurrentCount > 0);
            if (oldest is not null)
            {
                await CloseSessionAsync(oldest.SessionId, "capacity_evicted", ct).ConfigureAwait(false);
            }
            else
            {
                return (null, Fail("browser_capacity_reached", string.Create(CultureInfo.InvariantCulture, $"活跃浏览器会话已达上限 ({maxSessions}) 且均在执行中，请稍后重试或关闭旧会话的浏览器。")));
            }
        }

        CdpManagedBrowserProcess proc;
        try
        {
            proc = await EnsureProcessAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, Fail("browser_launch_failed", ex.Message));
        }

        CdpBrowserSessionState? newState = null;
        var sessionProxy = new BrowserOutboundProxy(
            _policyGuard,
            _portRegistry,
            () => newState?.WorkMode ?? SessionWorkMode.Daily,
            () => newState?.GetApprovedLoopbackPortsSnapshot(),
            _logger);

        string? browserContextId = null;
        try
        {
            var createCtxParams = JsonSerializer.Serialize(new
            {
                disposeOnDetach = true,
                proxyServer = sessionProxy.ProxyServerUrl,
                proxyBypassList = "<-loopback>"
            });

            var ctxRes = await proc.Connection.SendJsonAsync("Target.createBrowserContext", createCtxParams, ct: ct).ConfigureAwait(false);
            browserContextId = ctxRes.GetProperty("browserContextId").GetString()
                ?? throw new InvalidOperationException("Target.createBrowserContext 未返回 browserContextId。");

            // P1-13: Fail-closed if download denial cannot be enforced on this BrowserContext.
            var downloadParams = JsonSerializer.Serialize(new { behavior = "deny", browserContextId });
            await proc.Connection.SendJsonAsync("Browser.setDownloadBehavior", downloadParams, ct: ct).ConfigureAwait(false);

            newState = new CdpBrowserSessionState(caller.SessionId, browserContextId, caller.WorkMode, sessionProxy);
            if (!_sessions.TryAdd(caller.SessionId, newState))
            {
                try
                {
                    await proc.Connection.SendJsonAsync(
                        "Target.disposeBrowserContext",
                        JsonSerializer.Serialize(new { browserContextId }),
                        ct: ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                await newState.DisposeAsync().ConfigureAwait(false);
                if (_sessions.TryGetValue(caller.SessionId, out var winner) && !winner.IsClosed)
                {
                    winner.SyncWorkMode(caller.WorkMode);
                    return (winner, null);
                }

                return (null, Fail("browser_session_race", "浏览器会话状态并发变更，请重试。"));
            }

            return (newState, null);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(browserContextId))
            {
                try
                {
                    await proc.Connection.SendJsonAsync(
                        "Target.disposeBrowserContext",
                        JsonSerializer.Serialize(new { browserContextId }),
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            await sessionProxy.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<CdpManagedBrowserProcess> EnsureProcessAsync(CancellationToken ct)
    {
        if (_sharedProcess is { IsAlive: true } alive)
        {
            return alive;
        }

        await _processGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sharedProcess is { IsAlive: true } aliveAgain)
            {
                return aliveAgain;
            }

            if (_sharedProcess is not null)
            {
                await _sharedProcess.DisposeAsync().ConfigureAwait(false);
                _sharedProcess = null;
                foreach (var sid in _sessions.Keys.ToArray())
                {
                    if (_sessions.TryRemove(sid, out var stale))
                    {
                        stale.MarkClosed();
                        await stale.DisposeAsync().ConfigureAwait(false);
                    }
                }
                _cdpSessionToAppSession.Clear();
            }

            var detected = _locator.Detect(_options.CurrentValue.ExecutablePath);
            if (!detected.Available || string.IsNullOrWhiteSpace(detected.ExecutablePath))
            {
                throw new InvalidOperationException(
                    detected.DiagnosticMessage ?? "未检测到可用的本机 Chromium/Chrome/Edge 浏览器。");
            }

            var proc = await CdpManagedBrowserProcess.LaunchAsync(
                detected.ExecutablePath,
                _runtimeBaseDir,
                _policyGuard,
                _portRegistry,
                _logger,
                ct).ConfigureAwait(false);

            proc.Connection.EventReceived += OnCdpEventReceived;
            _sharedProcess = proc;
            return proc;
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<BrowserTabRecord> EnsureActiveTabAsync(
        CdpManagedBrowserProcess proc,
        CdpBrowserSessionState session,
        CancellationToken ct)
    {
        var active = session.GetActiveTab();
        if (active is not null)
        {
            return active;
        }

        return await CreateAndAttachTabAsync(proc, session, makeActive: true, ct).ConfigureAwait(false);
    }

    private async Task<BrowserTabRecord> CreateAndAttachTabAsync(
        CdpManagedBrowserProcess proc,
        CdpBrowserSessionState session,
        bool makeActive,
        CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        // Always start on about:blank so Fetch.enable and auto-attach are active before any remote URL loads.
        var createParams = JsonSerializer.Serialize(new
        {
            url = "about:blank",
            width = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
            height = Math.Clamp(opts.DefaultViewportHeight, 480, 1440),
            browserContextId = session.BrowserContextId
        });

        var createRes = await proc.Connection.SendJsonAsync("Target.createTarget", createParams, ct: ct).ConfigureAwait(false);
        var targetId = createRes.GetProperty("targetId").GetString()
            ?? throw new InvalidOperationException("Target.createTarget 未返回 targetId。");

        var attachParams = JsonSerializer.Serialize(new { targetId, flatten = true });
        var attachRes = await proc.Connection.SendJsonAsync("Target.attachToTarget", attachParams, ct: ct).ConfigureAwait(false);
        var cdpSessionId = attachRes.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Target.attachToTarget 未返回 sessionId。");

        var tab = new BrowserTabRecord
        {
            TargetId = targetId,
            CdpSessionId = cdpSessionId,
            Url = "about:blank",
            Title = string.Empty
        };

        _cdpSessionToAppSession[cdpSessionId] = session.SessionId;
        session.AddOrUpdateTab(tab, makeActive);

        await InitializeTabDomainsAsync(proc.Connection, cdpSessionId, ct).ConfigureAwait(false);
        return tab;
    }

    private async Task InitializeTabDomainsAsync(CdpConnection connection, string cdpSessionId, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        await connection.SendAsync("Page.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendAsync("Runtime.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendAsync("Network.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);

        var metricsParams = JsonSerializer.Serialize(new
        {
            width = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
            height = Math.Clamp(opts.DefaultViewportHeight, 480, 1440),
            deviceScaleFactor = 1,
            mobile = false
        });
        await connection.SendJsonAsync("Emulation.setDeviceMetricsOverride", metricsParams, cdpSessionId, ct).ConfigureAwait(false);

        var fetchParams = JsonSerializer.Serialize(new
        {
            patterns = new[] { new { urlPattern = "*", resourceType = "Document" } }
        });
        await connection.SendJsonAsync("Fetch.enable", fetchParams, cdpSessionId, ct).ConfigureAwait(false);
    }

    private void OnCdpEventReceived(CdpEventMessage evt)
    {
        if (string.Equals(evt.Method, "Target.targetDestroyed", StringComparison.Ordinal))
        {
            HandleTargetDestroyed(evt.Params);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.SessionId) ||
            !_cdpSessionToAppSession.TryGetValue(evt.SessionId, out var appSessionId) ||
            !_sessions.TryGetValue(appSessionId, out var session))
        {
            return;
        }

        switch (evt.Method)
        {
            case "Fetch.requestPaused":
                _ = HandleFetchRequestPausedAsync(evt.SessionId, session, evt.Params);
                break;

            case "Page.javascriptDialogOpening":
                _ = HandleJavaScriptDialogAsync(evt.SessionId, session, evt.Params);
                break;

            case "Runtime.consoleAPICalled":
                HandleConsoleApiCalled(session, evt.Params);
                break;

            case "Runtime.exceptionThrown":
                HandleRuntimeException(session, evt.Params);
                break;

            case "Network.loadingFailed":
                HandleNetworkLoadingFailed(session, evt.Params);
                break;

            case "Page.frameNavigated":
                HandleFrameNavigated(evt.SessionId, session, evt.Params);
                break;

            case "Inspector.targetCrashed":
                HandleTargetCrashed(evt.SessionId, session);
                break;
        }
    }

    private void HandleTargetDestroyed(JsonElement parameters)
    {
        var targetId = parameters.TryGetProperty("targetId", out var prop) ? prop.GetString() : null;
        if (string.IsNullOrWhiteSpace(targetId)) return;

        foreach (var session in _sessions.Values)
        {
            if (session.RemoveTab(targetId, out var removed) && removed is not null)
            {
                _cdpSessionToAppSession.TryRemove(removed.CdpSessionId, out _);
                break;
            }
        }
    }

    private void HandleTargetCrashed(string cdpSessionId, CdpBrowserSessionState session)
    {
        var tab = session.FindTabByCdpSessionId(cdpSessionId);
        if (tab is null) return;
        session.RemoveTab(tab.TargetId, out _);
        _cdpSessionToAppSession.TryRemove(cdpSessionId, out _);
        session.RecordConsoleEntry("error", $"Browser tab [{tab.TargetId}] crashed and was detached.");
    }

    private async Task HandleFetchRequestPausedAsync(string cdpSessionId, CdpBrowserSessionState session, JsonElement parameters)
    {
        var proc = _sharedProcess;
        if (proc is null || !proc.IsAlive) return;

        var requestId = parameters.TryGetProperty("requestId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId)) return;

        var url = parameters.TryGetProperty("request", out var reqProp) && reqProp.TryGetProperty("url", out var urlProp)
            ? urlProp.GetString()
            : null;

        var allowed = string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);
        if (!allowed && !string.IsNullOrWhiteSpace(url))
        {
            var eval = _policyGuard.ValidateNavigationUrl(url, session.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
            allowed = eval.Allowed;
            if (!allowed)
            {
                session.RecordNetworkError($"Blocked document navigation to '{url}': {eval.Message}");
            }
        }

        try
        {
            if (allowed)
            {
                await proc.Connection.SendJsonAsync(
                    "Fetch.continueRequest",
                    JsonSerializer.Serialize(new { requestId }),
                    cdpSessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await proc.Connection.SendJsonAsync(
                    "Fetch.failRequest",
                    JsonSerializer.Serialize(new { requestId, errorReason = "BlockedByClient" }),
                    cdpSessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fetch response failed for request {RequestId}", requestId);
        }
    }

    private async Task HandleJavaScriptDialogAsync(string cdpSessionId, CdpBrowserSessionState session, JsonElement parameters)
    {
        var proc = _sharedProcess;
        if (proc is null || !proc.IsAlive) return;

        var dialogType = parameters.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "alert";
        var message = parameters.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : string.Empty;
        session.RecordConsoleEntry("warn", $"Auto-dismissed browser {dialogType} dialog: {message}");

        try
        {
            var accept = string.Equals(dialogType, "beforeunload", StringComparison.OrdinalIgnoreCase);
            await proc.Connection.SendJsonAsync(
                "Page.handleJavaScriptDialog",
                JsonSerializer.Serialize(new { accept }),
                cdpSessionId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed handling JS dialog");
        }
    }

    private static void HandleConsoleApiCalled(CdpBrowserSessionState session, JsonElement parameters)
    {
        var level = parameters.TryGetProperty("type", out var t) ? t.GetString() ?? "log" : "log";
        var parts = new List<string>();
        if (parameters.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in args.EnumerateArray())
            {
                if (arg.TryGetProperty("value", out var val))
                {
                    parts.Add(val.ToString());
                }
                else if (arg.TryGetProperty("description", out var desc))
                {
                    parts.Add(desc.GetString() ?? string.Empty);
                }
            }
        }

        var text = string.Join(' ', parts.Where(static p => !string.IsNullOrEmpty(p)));
        if (!string.IsNullOrWhiteSpace(text))
        {
            session.RecordConsoleEntry(level, text);
        }
    }

    private static void HandleRuntimeException(CdpBrowserSessionState session, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("exceptionDetails", out var details)) return;

        var text = details.TryGetProperty("text", out var t) ? t.GetString() ?? "Uncaught exception" : "Uncaught exception";
        if (details.TryGetProperty("exception", out var exc) && exc.TryGetProperty("description", out var desc) && !string.IsNullOrWhiteSpace(desc.GetString()))
        {
            text = desc.GetString()!;
        }

        var url = details.TryGetProperty("url", out var u) ? u.GetString() : null;
        int? line = details.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var ln) ? ln : null;
        session.RecordConsoleEntry("exception", text, url, line);
    }

    private static void HandleNetworkLoadingFailed(CdpBrowserSessionState session, JsonElement parameters)
    {
        var errorText = parameters.TryGetProperty("errorText", out var err) ? err.GetString() : "Request failed";
        var type = parameters.TryGetProperty("type", out var t) ? t.GetString() : null;
        session.RecordNetworkError(string.IsNullOrWhiteSpace(type) ? errorText ?? "Request failed" : $"[{type}] {errorText}");
    }

    private static void HandleFrameNavigated(string cdpSessionId, CdpBrowserSessionState session, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("frame", out var frame)) return;
        if (frame.TryGetProperty("parentId", out var parent) && !string.IsNullOrWhiteSpace(parent.GetString())) return;

        var tab = session.FindTabByCdpSessionId(cdpSessionId);
        if (tab is null) return;

        var url = frame.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            session.UpdateTabMetadata(tab.TargetId, url, title: null, invalidateIsolatedWorld: true);
        }
    }

    private async Task<BrowserApprovalOutcome> RequestLoopbackPortApprovalAsync(
        CdpBrowserSessionState session,
        Uri targetUri,
        int port,
        CancellationToken ct)
    {
        if (_approvalCoordinator is null)
        {
            return new BrowserApprovalOutcome(
                Approved: false,
                Denied: true,
                ExpiredOrCancelled: false,
                GrantedForSession: false,
                ErrorCode: BrowserTargetPolicyGuard.ReasonLoopbackApprovalRequired,
                ErrorMessage: string.Create(CultureInfo.InvariantCulture, $"开发模式访问非白名单本机端口 {port} 需要控制面审批，但当前宿主未配置浏览器审批协调器。"));
        }

        var origin = BrowserTargetPolicyGuard.GetOriginString(targetUri);
        session.Touch();
        var outcome = await _approvalCoordinator.RequestApprovalAsync(
            new BrowserApprovalPrompt(
                SessionId: session.SessionId,
                Tool: "browser_navigate",
                Action: "loopback_port_access",
                TargetUrl: targetUri.AbsoluteUri,
                TargetOrigin: origin,
                TargetDescription: string.Create(CultureInfo.InvariantCulture, $"允许浏览器访问开发模式本机端口 {port} ({origin})"),
                RiskReason: "该本机端口不在默认前端开发白名单中，请确认并非敏感本地管理端口（如数据库或 Docker API）。",
                ExpressionOrPath: targetUri.AbsoluteUri,
                AllowSessionScope: true),
            ct).ConfigureAwait(false);
        session.Touch();
        return outcome;
    }

    private async Task<(BrowserOperationResult? Refusal, bool WaitedForApproval)> EnforceInteractionApprovalAsync(
        BrowserTrustedCallerContext caller,
        CdpBrowserSessionState session,
        BrowserTabRecord tab,
        string action,
        BrowserActRequest request,
        CdpSnapshotElement? targetElement,
        CdpLiveRefProbe? liveProbe,
        CdpActiveElementProbe? activeProbe,
        bool isSensitive,
        CancellationToken ct)
    {
        if (action is "scroll" or "wait" or "hover")
        {
            return (null, false);
        }

        var policy = _options.CurrentValue.InteractionApprovalPolicy;
        var requiresApproval = policy switch
        {
            BrowserInteractionApprovalPolicy.AlwaysAsk => true,
            BrowserInteractionApprovalPolicy.HighRiskOnly => isSensitive,
            BrowserInteractionApprovalPolicy.AutoInSession => isSensitive,
            _ => true
        };

        if (!requiresApproval)
        {
            return (null, false);
        }

        var origin = ExtractOrigin(tab.Url);
        if (!isSensitive &&
            _approvalCoordinator is not null &&
            _approvalCoordinator.IsSessionOriginApproved(caller.SessionId, origin, "interact"))
        {
            return (null, false);
        }

        if (_approvalCoordinator is null)
        {
            return (Fail("approval_unavailable", "该浏览器交互属于高风险操作，需要人工控制面审批，但审批协调器不可用。"), false);
        }

        var elemName = SanitizeOneLine(liveProbe?.Name ?? targetElement?.Name ?? activeProbe?.Name, 80);
        var desc = targetElement is not null
            ? $"{action} [{targetElement.Ref}] {liveProbe?.Role ?? targetElement.Role} \"{elemName}\""
            : $"{action} {SanitizeOneLine(request.Key, 40)} ({elemName})".Trim();
        var riskReason = isSensitive
            ? "检测到敏感提交/支付/删除或表单提交操作，需要人工确认。"
            : "当前浏览器安全策略为 AlwaysAsk，执行页面交互前需要人工确认。";
        var detail = action is "type" or "select_option" && !string.IsNullOrEmpty(request.Text)
            ? $"输入内容: {SanitizeOneLine(request.Text, 120)}"
            : tab.Url;

        session.Touch();
        var outcome = await _approvalCoordinator.RequestApprovalAsync(
            new BrowserApprovalPrompt(
                SessionId: caller.SessionId,
                Tool: "browser_act",
                Action: action,
                TargetUrl: tab.Url,
                TargetOrigin: origin,
                TargetDescription: desc,
                RiskReason: riskReason,
                ExpressionOrPath: detail,
                AllowSessionScope: !isSensitive),
            ct).ConfigureAwait(false);
        session.Touch();

        if (!outcome.Approved)
        {
            return (Fail(outcome.ErrorCode ?? "approval_denied", outcome.ErrorMessage ?? "浏览器交互请求被拒绝或已过期。"), true);
        }

        return (null, true);
    }

    private static async Task<int> EnsureIsolatedWorldContextIdAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        CancellationToken ct)
    {
        if (tab.IsolatedWorldContextId is { } existing && existing > 0)
        {
            return existing;
        }

        var frameTree = await connection.SendAsync("Page.getFrameTree", sessionId: tab.CdpSessionId, ct: ct).ConfigureAwait(false);
        var frameId = frameTree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Page.getFrameTree 未返回主 Frame ID。");

        var worldRes = await connection.SendJsonAsync(
            "Page.createIsolatedWorld",
            JsonSerializer.Serialize(new { frameId, worldName = IsolatedWorldName, grantUniveralAccess = false }),
            tab.CdpSessionId,
            ct).ConfigureAwait(false);

        var ctxId = worldRes.GetProperty("executionContextId").GetInt32();
        tab.IsolatedWorldContextId = ctxId;
        return ctxId;
    }

    private static async Task<string?> EvaluateInIsolatedWorldAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        string expression,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var contextId = await EnsureIsolatedWorldContextIdAsync(connection, tab, ct).ConfigureAwait(false);
            var evalParams = JsonSerializer.Serialize(new
            {
                expression,
                contextId,
                returnByValue = true,
                awaitPromise = false
            });

            try
            {
                var result = await connection.SendJsonAsync("Runtime.evaluate", evalParams, tab.CdpSessionId, ct).ConfigureAwait(false);
                if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                {
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                }

                return null;
            }
            catch (InvalidOperationException ex) when (attempt == 0 && ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
            {
                // Navigation invalidated the old isolated world; recreate once.
                tab.IsolatedWorldContextId = null;
            }
        }

        return null;
    }

    private static async Task<CdpSnapshotPayload> CaptureDomSnapshotCoreAsync(
        CdpConnection connection,
        CdpBrowserSessionState session,
        BrowserTabRecord tab,
        CancellationToken ct)
    {
        var rawJson = await EvaluateInIsolatedWorldAsync(
            connection,
            tab,
            BrowserSnapshotScript.BuildExtractInteractiveSnapshotJs(),
            ct).ConfigureAwait(false);

        CdpSnapshotPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                payload = JsonSerializer.Deserialize(rawJson, CdpJsonContext.Default.CdpSnapshotPayload);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        payload ??= new CdpSnapshotPayload { Url = tab.Url, Title = tab.Title, PageText = string.Empty, Elements = [] };
        session.UpdateTabMetadata(tab.TargetId, payload.Url, payload.Title);
        session.ReplaceTabElementRefs(tab.TargetId, payload.Elements);
        return payload;
    }

    private static async Task<CdpLiveRefProbe?> LocateAndVerifyRefInIsolatedWorldAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        string refId,
        CancellationToken ct)
    {
        var raw = await EvaluateInIsolatedWorldAsync(
            connection,
            tab,
            BrowserSnapshotScript.BuildLocateAndVerifyRefJs(EscapeJsString(refId)),
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize(raw, CdpJsonContext.Default.CdpLiveRefProbe);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<CdpActiveElementProbe?> InspectActiveElementInIsolatedWorldAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        CancellationToken ct)
    {
        var raw = await EvaluateInIsolatedWorldAsync(
            connection,
            tab,
            BrowserSnapshotScript.BuildInspectActiveElementJs(),
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize(raw, CdpJsonContext.Default.CdpActiveElementProbe);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<BrowserScreenshotData?> TryCaptureViewportJpegAsync(
        CdpConnection connection,
        CdpBrowserSessionState session,
        string cdpSessionId,
        CancellationToken ct)
    {
        try
        {
            var captureParams = JsonSerializer.Serialize(new
            {
                format = "jpeg",
                quality = 78,
                fromSurface = true
            });
            var result = await connection.SendJsonAsync("Page.captureScreenshot", captureParams, cdpSessionId, ct).ConfigureAwait(false);
            if (result.TryGetProperty("data", out var dataProp) && dataProp.GetString() is { Length: > 0 } base64)
            {
                var bytes = Convert.FromBase64String(base64);
                return session.RecordScreenshot(bytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Page.captureScreenshot failed for session {SessionId}", session.SessionId);
        }

        return null;
    }

    private static async Task WaitForDocumentSettleAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        CancellationToken ct,
        int maxWaitMs = 4000)
    {
        var deadline = Environment.TickCount64 + maxWaitMs;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var readyState = await EvaluateInIsolatedWorldAsync(connection, tab, "document.readyState", ct).ConfigureAwait(false);
                if (string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(readyState, "interactive", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(180, ct).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
            }

            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    private static Task<JsonElement> DispatchMouseMoveAsync(CdpConnection connection, string cdpSessionId, double x, double y, CancellationToken ct)
        => connection.SendJsonAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mouseMoved", x, y }),
            cdpSessionId,
            ct);

    private static async Task DispatchMouseClickAsync(CdpConnection connection, string cdpSessionId, double x, double y, int clickCount, CancellationToken ct)
    {
        for (var i = 1; i <= clickCount; i++)
        {
            await connection.SendJsonAsync(
                "Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = i }),
                cdpSessionId,
                ct).ConfigureAwait(false);
            await connection.SendJsonAsync(
                "Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = i }),
                cdpSessionId,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task DispatchKeyPressAsync(CdpConnection connection, string cdpSessionId, string key, CancellationToken ct)
    {
        var (code, windowsVirtualKeyCode, text) = MapKey(key);
        await connection.SendJsonAsync(
            "Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "rawKeyDown", key, code, windowsVirtualKeyCode }),
            cdpSessionId,
            ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(text))
        {
            await connection.SendJsonAsync(
                "Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type = "char", key, code, text, windowsVirtualKeyCode }),
                cdpSessionId,
                ct).ConfigureAwait(false);
        }
        await connection.SendJsonAsync(
            "Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "keyUp", key, code, windowsVirtualKeyCode }),
            cdpSessionId,
            ct).ConfigureAwait(false);
    }

    private static bool IsSubmitTriggerKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        return string.Equals(k, "Enter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(k, "Space", StringComparison.OrdinalIgnoreCase) ||
               k == " ";
    }

    private static (string Code, int Vk, string? Text) MapKey(string key) => key switch
    {
        "Enter" => ("Enter", 13, "\r"),
        "Tab" => ("Tab", 9, null),
        "Escape" => ("Escape", 27, null),
        "Backspace" => ("Backspace", 8, null),
        "Delete" => ("Delete", 46, null),
        "ArrowDown" => ("ArrowDown", 40, null),
        "ArrowUp" => ("ArrowUp", 38, null),
        "ArrowLeft" => ("ArrowLeft", 37, null),
        "ArrowRight" => ("ArrowRight", 39, null),
        "Space" or " " => ("Space", 32, " "),
        _ => (key, key.Length == 1 ? char.ToUpperInvariant(key[0]) : 0, key.Length == 1 ? key : null)
    };

    private async ValueTask PublishStateAsync(CdpBrowserSessionState session, bool force, CancellationToken ct)
    {
        if (_eventPublisher is null || !session.ShouldPublishThrottled(force, StatePublishThrottle))
        {
            return;
        }

        try
        {
            await _eventPublisher
                .PublishStateUpdatedAsync(session.SessionId, session.ToDto("running", _options.CurrentValue.ProfileMode.ToString()), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed publishing browser_state_updated for session {SessionId}", session.SessionId);
        }
    }

    private static string ExtractOrigin(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? BrowserTargetPolicyGuard.GetOriginString(parsed)
            : url ?? string.Empty;

    private static string SanitizeOneLine(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= maxLen ? single : single[..maxLen] + "…";
    }

    private static string EscapeJsString(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static BrowserOperationResult Fail(string code, string message)
        => new(Succeeded: false, Output: $"[{code}] {message}", Summary: message, ErrorCode: code);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var sid in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(sid, out var state))
            {
                state.MarkClosed();
                await state.DisposeAsync().ConfigureAwait(false);
            }
        }

        _cdpSessionToAppSession.Clear();

        if (_sharedProcess is not null)
        {
            await _sharedProcess.DisposeAsync().ConfigureAwait(false);
            _sharedProcess = null;
        }

        _processGate.Dispose();
    }
}
