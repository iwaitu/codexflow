// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution.Approval;
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
    private const long MaxUploadFileSizeBytes = 50L * 1024 * 1024;
    private const long MaxDownloadFileSizeBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan StatePublishThrottle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ManualTakeoverMaxDuration = TimeSpan.FromMinutes(15);

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly IBrowserExecutableLocator _locator;
    private readonly BrowserTargetPolicyGuard _policyGuard;
    private readonly ProtectedLocalPortRegistry _portRegistry;
    private readonly IBrowserApprovalCoordinator? _approvalCoordinator;
    private readonly IBrowserStateEventPublisher? _eventPublisher;
    private readonly IControlPlaneStateProvider? _controlPlane;
    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly string _runtimeBaseDir;
    private readonly string _persistentProfileDir;
    private readonly IDisposable? _optionsChangeSubscription;

    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CdpBrowserSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _cdpSessionToAppSession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _trackedTargets = new(StringComparer.Ordinal);
    private ICdpBrowserRuntime? _sharedRuntime;
    private volatile string? _persistentOwnerSessionId;
    private int _disposed;

    public BrowserSessionManager(
        IOptionsMonitor<BrowserOptions> options,
        IBrowserExecutableLocator locator,
        BrowserTargetPolicyGuard policyGuard,
        ProtectedLocalPortRegistry portRegistry,
        ILogger<BrowserSessionManager> logger,
        IBrowserApprovalCoordinator? approvalCoordinator = null,
        IBrowserStateEventPublisher? eventPublisher = null,
        string? runtimeBaseDir = null,
        IControlPlaneStateProvider? controlPlane = null,
        string? persistentProfileDir = null)
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
        _controlPlane = controlPlane;
        _logger = logger;
        _runtimeBaseDir = string.IsNullOrWhiteSpace(runtimeBaseDir)
            ? Path.Combine(CodexFlowPaths.GlobalDirectory(), "browser-runtime")
            : runtimeBaseDir;
        _persistentProfileDir = string.IsNullOrWhiteSpace(persistentProfileDir)
            ? Path.Combine(CodexFlowPaths.GlobalDirectory(), "browser-profiles", "default")
            : persistentProfileDir;

        CdpManagedBrowserProcess.ReapOrphanedRuntimeDirectories(_runtimeBaseDir, _logger);
        _optionsChangeSubscription = _options.OnChange(OnOptionsChanged);
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

            if (session.TakeoverActive)
            {
                return Fail("takeover_active", "当前浏览器会话正处于人工接管状态，请等待用户点击「完成并交回 Agent」后再执行操作。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
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

                if (session.IsRemote)
                {
                    var remotePort = eval.NormalizedUri.IsDefaultPort
                        ? (string.Equals(eval.NormalizedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                        : eval.NormalizedUri.Port;
                    var remoteIpEval = await _policyGuard.ResolveAndValidateEndpointAsync(
                        eval.NormalizedUri.IdnHost,
                        remotePort,
                        caller.WorkMode,
                        session.GetApprovedLoopbackPortsSnapshot(),
                        ct).ConfigureAwait(false);
                    if (!remoteIpEval.Allowed)
                    {
                        session.RecordAction("browser_navigate", "goto", SanitizeOneLine(url, 200), tab.Url, false, remoteIpEval.Message, sw.ElapsedMilliseconds);
                        await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                        return Fail(remoteIpEval.ReasonCode ?? "policy_denied", remoteIpEval.Message ?? "远端浏览器目标地址被安全策略拦截。");
                    }
                }

                var navRefusal = await EnforcePersistentNavigationApprovalAsync(session, eval.NormalizedUri, ct).ConfigureAwait(false);
                if (navRefusal is not null)
                {
                    session.RecordAction("browser_navigate", "goto", eval.NormalizedUri.AbsoluteUri, tab.Url, false, navRefusal.Output, sw.ElapsedMilliseconds);
                    await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                    return navRefusal;
                }

                var navJson = new JsonObject { ["url"] = eval.NormalizedUri.AbsoluteUri }.ToJsonString();
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
                        if (!historyEval.Allowed || historyEval.NormalizedUri is null)
                        {
                            return Fail(historyEval.ReasonCode ?? "policy_denied", $"历史记录目标 '{entryUrl}' 不符合当前安全策略: {historyEval.Message}");
                        }

                        var navRefusal = await EnforcePersistentNavigationApprovalAsync(session, historyEval.NormalizedUri, ct).ConfigureAwait(false);
                        if (navRefusal is not null)
                        {
                            return navRefusal;
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
            var screenshot = await TryCaptureViewportJpegAsync(
                proc.Connection,
                session,
                tab,
                ct,
                annotate: false,
                elementsForAnnotation: snapshot.Elements,
                cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);

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
        bool annotateScreenshot = false,
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

            if (session.TakeoverActive)
            {
                return Fail("takeover_active", "当前浏览器会话正处于人工接管状态，请等待用户点击「完成并交回 Agent」后再执行操作。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            var captureVisual = includeScreenshot || annotateScreenshot;
            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            BrowserScreenshotData? screenshot = null;
            if (captureVisual)
            {
                screenshot = await TryCaptureViewportJpegAsync(
                    proc.Connection,
                    session,
                    tab,
                    ct,
                    annotate: annotateScreenshot,
                    elementsForAnnotation: snapshot.Elements,
                    cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);
            }

            var console = includeConsole ? session.GetRecentConsoleEntries() : null;
            var netErrors = includeNetworkErrors ? session.GetRecentNetworkErrors() : null;
            var formatted = BrowserSnapshotScript.FormatSafeSnapshotForModel(snapshot, maxChars, console, netErrors);

            var elemCount = snapshot.Elements?.Count ?? 0;
            var summary = string.Create(CultureInfo.InvariantCulture, $"已获取页面快照 ({elemCount} 个可交互元素): {tab.Url}");
            session.Touch();
            session.RecordAction(
                "browser_snapshot",
                annotateScreenshot ? "snapshot+annotate" : captureVisual ? "snapshot+screenshot" : "snapshot",
                null,
                tab.Url,
                true,
                summary,
                sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: captureVisual, ct: ct).ConfigureAwait(false);

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
        if (action is not ("click" or "double_click" or "hover" or "type" or "clear" or "press_key" or "select_option" or "scroll" or "wait" or "upload_file"))
        {
            return Fail("invalid_action", $"不支持的交互动作 '{request.Action}'，允许值: click, double_click, hover, type, clear, press_key, select_option, upload_file, scroll, wait。");
        }

        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        StagedUploadFile? stagedUpload = null;
        var keepStagedForDeferredFormSubmit = false;
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭，请重试。");
            }

            if (session.TakeoverActive)
            {
                return Fail("takeover_active", "当前浏览器会话正处于人工接管状态，请等待用户点击「完成并交回 Agent」后再执行操作。");
            }

            if (session.IsRemote && action == "upload_file")
            {
                return Fail("remote_cdp_upload_unsupported", "当前连接的是远端 CDP 浏览器实例，安全策略禁止向远端浏览器映射本机文件路径上传。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            CdpSnapshotElement? targetElement = null;
            BrowserChildFrameRecord? targetChildFrame = null;
            CdpLiveRefProbe? preActionLiveProbe = null;
            CdpActiveElementProbe? activeProbeForKey = null;
            var targetCdpSessionId = tab.CdpSessionId;
            Func<int?> getTargetWorld = () => tab.IsolatedWorldContextId;
            Action<int?> setTargetWorld = id => tab.IsolatedWorldContextId = id;

            if (action is "click" or "double_click" or "hover" or "type" or "clear" or "select_option" or "upload_file")
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

                if (!string.IsNullOrWhiteSpace(targetElement.FrameCdpSessionId))
                {
                    targetChildFrame = session.GetChildFramesSnapshot(tab.TargetId)
                        .FirstOrDefault(f => string.Equals(f.CdpSessionId, targetElement.FrameCdpSessionId, StringComparison.Ordinal));
                    if (targetChildFrame is null)
                    {
                        return Fail("stale_ref", $"元素 [{refId}] 所在的跨源 iframe 已失效或关闭，请重新调用 browser_snapshot。");
                    }

                    targetCdpSessionId = targetChildFrame.CdpSessionId;
                    getTargetWorld = () => targetChildFrame.IsolatedWorldContextId;
                    setTargetWorld = id => targetChildFrame.IsolatedWorldContextId = id;
                }

                if (action == "upload_file")
                {
                    var fileCheck = await EvaluateInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        BrowserSnapshotScript.BuildVerifyFileInputRefJs(EscapeJsString(refId)),
                        ct).ConfigureAwait(false);
                    if (!string.Equals(fileCheck, "ok", StringComparison.Ordinal))
                    {
                        return Fail("invalid_file_input", $"元素 [{refId}] 不是可用的文件上传控件 <input type=\"file\"> ({fileCheck ?? "unknown"})，请重新调用 browser_snapshot 检查。");
                    }

                    // P0-2 & P1-6: Canonical real-path resolution + immutable private staging BEFORE approval.
                    var (staged, stageErr) = await BrowserWorkspacePathGuard
                        .StageWorkspaceUploadFileAsync(caller.WorkspaceRoot, request.FilePath, session.StagingDirectory, MaxUploadFileSizeBytes, ct)
                        .ConfigureAwait(false);
                    if (stageErr is not null || staged is null)
                    {
                        return stageErr!;
                    }

                    stagedUpload = staged;
                }
                else
                {
                    // P0-2: Always verify live DOM in isolated world before any interaction (prevents TOCTOU & clickjacking).
                    preActionLiveProbe = await LocateAndVerifyRefInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        refId,
                        ct).ConfigureAwait(false);
                    if (preActionLiveProbe is null || !preActionLiveProbe.Ok)
                    {
                        return Fail("stale_ref", $"元素 [{refId}] 已从页面消失或不可见 ({preActionLiveProbe?.Error ?? "not_found"})，请重新调用 browser_snapshot。");
                    }

                    var viewportX = targetElement.FrameOffsetX + preActionLiveProbe.X;
                    var viewportY = targetElement.FrameOffsetY + preActionLiveProbe.Y;
                    var isObscuredInTopDoc = false;
                    if (targetChildFrame is not null && action is "click" or "double_click" or "type" or "clear")
                    {
                        var topHitTag = await EvaluateInIsolatedWorldAsync(
                            proc.Connection,
                            tab,
                            string.Create(CultureInfo.InvariantCulture, $"(() => {{ const h = document.elementFromPoint({viewportX}, {viewportY}); return h ? h.tagName.toLowerCase() : ''; }})()"),
                            ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(topHitTag) && topHitTag is not ("iframe" or "frame"))
                        {
                            isObscuredInTopDoc = true;
                        }
                    }

                    // P1-3: Never allow click on <input type="file"> because it pops OS native dialog and hangs automation.
                    if (action is "click" or "double_click" &&
                        (string.Equals(targetElement.Role, "file_input", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(preActionLiveProbe.Role, "file_input", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Fail(
                            "use_upload_file_action",
                            $"元素 [{refId}] 是文件上传控件，禁止直接点击触发原生文件选择框；请改用 browser_act(action=\"upload_file\", ref=\"{refId}\", file_path=\"工作区内文件路径\")。");
                    }

                    if (action is "click" or "double_click" or "type" or "clear" && (preActionLiveProbe.Obscured || isObscuredInTopDoc))
                    {
                        return Fail("element_obscured", $"元素 [{refId}] 当前被其他浮层遮挡（可能存在弹窗遮罩或点击劫持），已拒绝操作，请先重新快照检查页面。");
                    }

                    // Enforce password/OTP prohibition using BOTH snapshot AND live DOM probe.
                    if (action is "type" or "clear" && (targetElement.IsPassword || preActionLiveProbe.IsPassword))
                    {
                        return Fail(
                            "password_input_forbidden",
                            $"安全策略禁止 AI 直接向密码、支付卡或验证码/OTP 字段 [{targetElement.Ref}] 输入内容。请提示用户自行处理登录/验证或调用 browser_request_takeover 交由人工操作。");
                    }
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
                activeProbeForKey = await InspectActiveElementInIsolatedWorldAsync(
                    proc.Connection,
                    tab.CdpSessionId,
                    () => tab.IsolatedWorldContextId,
                    id => tab.IsolatedWorldContextId = id,
                    ct).ConfigureAwait(false);
                var isSafeNavigationKey = keyTrimmed is "Escape" or "Tab";
                if (!isSafeNavigationKey && activeProbeForKey is { IsPassword: true })
                {
                    return Fail(
                        "password_input_forbidden",
                        "安全策略禁止通过 press_key 向当前聚焦的密码/OTP/支付敏感字段输入字符。请提示用户自行输入或请求人工接管。");
                }

                if (!isSafeNavigationKey && activeProbeForKey is { InIframe: true })
                {
                    BrowserChildFrameRecord? focusedChild = null;
                    CdpActiveElementProbe? childProbe = null;
                    foreach (var cf in session.GetChildFramesSnapshot(tab.TargetId))
                    {
                        var hasFocus = await EvaluateInIsolatedWorldAsync(
                            proc.Connection,
                            cf.CdpSessionId,
                            () => cf.IsolatedWorldContextId,
                            id => cf.IsolatedWorldContextId = id,
                            "String(Boolean(document.hasFocus()))",
                            ct).ConfigureAwait(false);
                        if (string.Equals(hasFocus, "true", StringComparison.Ordinal))
                        {
                            focusedChild = cf;
                            childProbe = await InspectActiveElementInIsolatedWorldAsync(
                                proc.Connection,
                                cf.CdpSessionId,
                                () => cf.IsolatedWorldContextId,
                                id => cf.IsolatedWorldContextId = id,
                                ct).ConfigureAwait(false);
                            break;
                        }
                    }

                    if (focusedChild is null || childProbe is null || childProbe.InIframe || !childProbe.IsEditable)
                    {
                        return Fail(
                            "iframe_input_forbidden",
                            "当前焦点位于跨源或不受管 iframe 内部，无法校验目标输入框是否为密码/敏感字段，已拒绝 press_key 输入。");
                    }

                    if (childProbe.IsPassword)
                    {
                        return Fail(
                            "password_input_forbidden",
                            "安全策略禁止通过 press_key 向 iframe 内聚焦的密码/OTP/支付敏感字段输入字符。请提示用户自行输入或请求人工接管。");
                    }

                    targetChildFrame = focusedChild;
                    targetCdpSessionId = focusedChild.CdpSessionId;
                    getTargetWorld = () => focusedChild.IsolatedWorldContextId;
                    setTargetWorld = id => focusedChild.IsolatedWorldContextId = id;
                    activeProbeForKey = childProbe;
                }
            }

            var liveUrlBeforeApproval = !string.IsNullOrWhiteSpace(targetChildFrame?.Url)
                ? targetChildFrame.Url
                : !string.IsNullOrWhiteSpace(targetElement?.FrameOrigin)
                    ? targetElement.FrameOrigin!
                    : !string.IsNullOrWhiteSpace(preActionLiveProbe?.PageUrl)
                        ? preActionLiveProbe.PageUrl!
                        : !string.IsNullOrWhiteSpace(activeProbeForKey?.PageUrl)
                            ? activeProbeForKey.PageUrl!
                            : tab.Url;

            var topPageOriginBeforeApproval = ExtractOrigin(tab.Url);
            var isCrossOriginChildFrame = targetElement is not null &&
                !string.IsNullOrWhiteSpace(targetElement.FrameOrigin) &&
                !string.Equals(targetElement.FrameOrigin, topPageOriginBeforeApproval, StringComparison.OrdinalIgnoreCase);

            var isPersistentRemoteWrite =
                session.ProfileMode == BrowserProfileMode.Persistent &&
                !IsLoopbackUrl(liveUrlBeforeApproval) &&
                action is not ("scroll" or "wait" or "hover") &&
                !(action == "press_key" && request.Key?.Trim() is "Escape" or "Tab");

            var isSensitive =
                action == "upload_file" ||
                isCrossOriginChildFrame ||
                isPersistentRemoteWrite ||
                targetElement?.IsSensitiveSubmit == true ||
                preActionLiveProbe?.IsSensitiveSubmit == true ||
                (action == "press_key" &&
                 IsSubmitTriggerKey(request.Key) &&
                 activeProbeForKey?.IsSensitiveSubmitContext == true);

            // Approval gate for sensitive interactions, upload_file, persistent remote writes, cross-origin iframe writes, or AlwaysAsk policy.
            var (approvalRefusal, waitedForApproval) = await EnforceInteractionApprovalAsync(
                caller,
                session,
                tab,
                liveUrlBeforeApproval,
                action,
                request,
                targetElement,
                preActionLiveProbe,
                activeProbeForKey,
                isSensitive,
                isPersistentRemoteWrite,
                stagedUpload,
                ct).ConfigureAwait(false);
            if (approvalRefusal is not null)
            {
                session.RecordAction("browser_act", action, SanitizeOneLine(request.Ref ?? request.Key, 80), liveUrlBeforeApproval, false, approvalRefusal.Output, sw.ElapsedMilliseconds);
                await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                return approvalRefusal;
            }

            // P1-5: Re-verify live URL origin and live DOM target after human approval to eliminate TOCTOU.
            if (waitedForApproval)
            {
                var priorOrigin = ExtractOrigin(liveUrlBeforeApproval);
                var currentLiveUrl = await EvaluateInIsolatedWorldAsync(
                    proc.Connection,
                    targetCdpSessionId,
                    getTargetWorld,
                    setTargetWorld,
                    "String(window.location.href || '')",
                    ct).ConfigureAwait(false) ?? liveUrlBeforeApproval;
                var currentOrigin = ExtractOrigin(currentLiveUrl);
                if (!string.Equals(currentOrigin, priorOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("stale_ref", "等待审批期间页面已跳转至其他站点，已取消本次交互以防止越权操作，请重新快照。");
                }

                if (action == "upload_file")
                {
                    var fileRecheck = await EvaluateInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        BrowserSnapshotScript.BuildVerifyFileInputRefJs(EscapeJsString(targetElement!.Ref)),
                        ct).ConfigureAwait(false);
                    if (!string.Equals(fileRecheck, "ok", StringComparison.Ordinal))
                    {
                        return Fail("stale_ref", $"等待审批期间文件上传控件 [{targetElement.Ref}] 状态已失效 ({fileRecheck ?? "unknown"})，已中止上传。");
                    }
                }
                else if (targetElement is not null)
                {
                    var postApprovalProbe = await LocateAndVerifyRefInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        targetElement.Ref,
                        ct).ConfigureAwait(false);
                    if (postApprovalProbe is null || !postApprovalProbe.Ok || postApprovalProbe.Obscured ||
                        (action is "type" or "clear" && postApprovalProbe.IsPassword) ||
                        !string.Equals(postApprovalProbe.Role, preActionLiveProbe?.Role, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(ExtractOrigin(postApprovalProbe.PageUrl ?? currentLiveUrl), priorOrigin, StringComparison.OrdinalIgnoreCase))
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
                    var x = (targetElement?.FrameOffsetX ?? 0) + preActionLiveProbe!.X;
                    var y = (targetElement?.FrameOffsetY ?? 0) + preActionLiveProbe.Y;
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

                    var x = (targetElement?.FrameOffsetX ?? 0) + preActionLiveProbe!.X;
                    var y = (targetElement?.FrameOffsetY ?? 0) + preActionLiveProbe.Y;
                    await DispatchMouseClickAsync(proc.Connection, tab.CdpSessionId, x, y, 1, ct).ConfigureAwait(false);

                    var clearRes = await EvaluateInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
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
                    var focusedBeforeInsert = await InspectActiveElementInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        ct).ConfigureAwait(false);
                    if (focusedBeforeInsert is { IsPassword: true } or { InIframe: true } || focusedBeforeInsert is not { IsEditable: true })
                    {
                        return Fail("password_input_forbidden", "点击后实际获得焦点的元素不是安全的非密码可编辑输入框，已中止输入。");
                    }

                    await DispatchKeyPressAsync(proc.Connection, tab.CdpSessionId, "Backspace", ct).ConfigureAwait(false);

                    if (action == "type" && !string.IsNullOrEmpty(request.Text))
                    {
                        var insertJson = new JsonObject { ["text"] = request.Text }.ToJsonString();
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
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
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
                case "upload_file":
                {
                    await SetFileInputFilesInIsolatedWorldAsync(
                        proc.Connection,
                        targetCdpSessionId,
                        getTargetWorld,
                        setTargetWorld,
                        targetElement!.Ref,
                        stagedUpload!.StagedFilePath,
                        ct).ConfigureAwait(false);
                    keepStagedForDeferredFormSubmit = true;
                    break;
                }
            }

            await WaitForDocumentSettleAsync(proc.Connection, tab, ct, maxWaitMs: 2500).ConfigureAwait(false);
            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            var screenshot = await TryCaptureViewportJpegAsync(
                proc.Connection,
                session,
                tab,
                ct,
                annotate: false,
                elementsForAnnotation: snapshot.Elements,
                cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);

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
            if (stagedUpload is not null && !keepStagedForDeferredFormSubmit)
            {
                BrowserWorkspacePathGuard.TryDeleteDirectory(stagedUpload.TempSubDir);
            }

            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> EvaluateAsync(
        BrowserTrustedCallerContext caller,
        string expression,
        bool awaitPromise = false,
        int maxResultChars = 8000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.PlanModeActive)
        {
            return Fail("plan_mode_active", "当前处于只读 Plan Mode，禁止执行 browser_evaluate 页面脚本求值。");
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Fail("invalid_expression", "动作 'browser_evaluate' 必须提供非空的 expression JavaScript 表达式。");
        }

        var trimmedExpr = expression.Trim();
        if (trimmedExpr.Length > 16_384)
        {
            return Fail("invalid_expression", "JavaScript 表达式长度超过 16384 字符上限。");
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

            if (session.TakeoverActive)
            {
                return Fail("takeover_active", "当前浏览器会话正处于人工接管状态，请等待用户点击「完成并交回 Agent」后再执行操作。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);

            var liveUrlBeforeApproval = await EvaluateInIsolatedWorldAsync(
                proc.Connection,
                tab,
                "String(window.location.href || '')",
                ct).ConfigureAwait(false) ?? tab.Url;
            var originBeforeApproval = ExtractOrigin(liveUrlBeforeApproval);

            if (_approvalCoordinator is null)
            {
                return Fail("approval_unavailable", "browser_evaluate 属于页面主上下文高风险操作，必须单次人工审批，但当前宿主未配置审批协调器。");
            }

            session.Touch();
            var approval = await _approvalCoordinator.RequestApprovalAsync(
                new BrowserApprovalPrompt(
                    SessionId: caller.SessionId,
                    Tool: "browser_evaluate",
                    Action: "evaluate",
                    TargetUrl: liveUrlBeforeApproval,
                    TargetOrigin: originBeforeApproval,
                    TargetDescription: $"在页面主上下文中执行 JavaScript 表达式 ({originBeforeApproval})",
                    RiskReason: "该表达式直接在当前页面主上下文运行并可访问同源 DOM 与前端状态（非沙箱环境），必须单次人工确认（allow_once）。",
                    ExpressionOrPath: trimmedExpr,
                    AllowSessionScope: false),
                ct).ConfigureAwait(false);
            session.Touch();

            if (!approval.Approved)
            {
                var denyReason = approval.ErrorMessage ?? "browser_evaluate 执行请求未获人工批准。";
                session.RecordAction("browser_evaluate", "evaluate", SanitizeOneLine(trimmedExpr, 80), liveUrlBeforeApproval, false, denyReason, sw.ElapsedMilliseconds);
                await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                return Fail(approval.ErrorCode ?? "approval_denied", denyReason);
            }

            var liveUrlAfterApproval = await EvaluateInIsolatedWorldAsync(
                proc.Connection,
                tab,
                "String(window.location.href || '')",
                ct).ConfigureAwait(false) ?? liveUrlBeforeApproval;
            var originAfterApproval = ExtractOrigin(liveUrlAfterApproval);
            if (!string.Equals(originAfterApproval, originBeforeApproval, StringComparison.OrdinalIgnoreCase))
            {
                session.RecordAction("browser_evaluate", "evaluate", SanitizeOneLine(trimmedExpr, 80), liveUrlAfterApproval, false, "页面 Origin 在审批期间发生变化", sw.ElapsedMilliseconds);
                await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                return Fail("stale_origin", "等待审批期间页面已跳转至其他站点，已取消执行 browser_evaluate。");
            }

            using var evalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            evalCts.CancelAfter(TimeSpan.FromSeconds(10));

            var evalParams = new JsonObject
            {
                ["expression"] = trimmedExpr,
                ["returnByValue"] = false,
                ["awaitPromise"] = awaitPromise,
                ["timeout"] = 8000
            }.ToJsonString();
            var evalRes = await proc.Connection.SendJsonAsync("Runtime.evaluate", evalParams, tab.CdpSessionId, evalCts.Token).ConfigureAwait(false);

            if (evalRes.TryGetProperty("exceptionDetails", out var excDetails))
            {
                var excText = excDetails.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "Uncaught exception" : "Uncaught exception";
                if (excDetails.TryGetProperty("exception", out var excObj) &&
                    excObj.TryGetProperty("description", out var descProp) &&
                    !string.IsNullOrWhiteSpace(descProp.GetString()))
                {
                    excText = descProp.GetString()!;
                }

                var cleanErr = BrowserSnapshotScript.StripForgedVisualMarkers(excText);
                session.RecordAction("browser_evaluate", "evaluate", SanitizeOneLine(trimmedExpr, 80), tab.Url, false, cleanErr, sw.ElapsedMilliseconds);
                await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
                return new BrowserOperationResult(
                    Succeeded: false,
                    Output: $"[evaluate_exception] 页面脚本执行异常:\n{UntrustedText.Wrap("browser_eval_error", cleanErr)}",
                    Summary: SanitizeOneLine(cleanErr, 120),
                    Url: tab.Url,
                    Title: tab.Title,
                    ErrorCode: "evaluate_exception");
            }

            string rawOutput;
            if (evalRes.TryGetProperty("result", out var resProp))
            {
                var objectId = resProp.TryGetProperty("objectId", out var oidProp) ? oidProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(objectId))
                {
                    try
                    {
                        var callParams = new JsonObject
                        {
                            ["objectId"] = objectId,
                            ["functionDeclaration"] = BrowserSnapshotScript.BuildSafeSerializeObjectFunctionJs(),
                            ["returnByValue"] = true,
                            ["awaitPromise"] = false
                        }.ToJsonString();
                        var callRes = await proc.Connection.SendJsonAsync("Runtime.callFunctionOn", callParams, tab.CdpSessionId, evalCts.Token).ConfigureAwait(false);
                        rawOutput = callRes.TryGetProperty("result", out var cr) && cr.TryGetProperty("value", out var cv)
                            ? (cv.ValueKind == JsonValueKind.String ? cv.GetString() ?? "null" : cv.ToString())
                            : "[object]";
                    }
                    finally
                    {
                        try
                        {
                            await proc.Connection.SendJsonAsync(
                                "Runtime.releaseObject",
                                new JsonObject { ["objectId"] = objectId }.ToJsonString(),
                                tab.CdpSessionId,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                else if (resProp.TryGetProperty("value", out var valProp))
                {
                    rawOutput = valProp.ValueKind == JsonValueKind.String
                        ? valProp.GetString() ?? string.Empty
                        : valProp.GetRawText();
                }
                else if (resProp.TryGetProperty("unserializableValue", out var unsProp))
                {
                    rawOutput = unsProp.GetString() ?? "[unserializable]";
                }
                else
                {
                    var resType = resProp.TryGetProperty("type", out var rt) ? rt.GetString() : "undefined";
                    rawOutput = string.Equals(resType, "undefined", StringComparison.Ordinal) ? "[undefined]" : $"[{resType}]";
                }
            }
            else
            {
                rawOutput = "[undefined]";
            }

            var cleaned = BrowserSnapshotScript.StripForgedVisualMarkers(rawOutput);
            var limit = Math.Clamp(maxResultChars, 200, 24000);
            if (cleaned.Length > limit)
            {
                cleaned = string.Concat(cleaned.AsSpan(0, limit), "\n... (求值结果已截断)");
            }

            var summary = $"已完成页面 JS 求值 -> {tab.Url}";
            session.Touch();
            session.RecordAction("browser_evaluate", "evaluate", SanitizeOneLine(trimmedExpr, 80), tab.Url, true, summary, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);

            return new BrowserOperationResult(
                Succeeded: true,
                Output: $"{summary}\n\n{UntrustedText.Wrap("browser_eval_result", cleaned)}",
                Summary: summary,
                Url: tab.Url,
                Title: tab.Title);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "browser_evaluate failed for session {SessionId}", caller.SessionId);
            session.RecordAction("browser_evaluate", "evaluate", SanitizeOneLine(trimmedExpr, 80), session.GetActiveTab()?.Url, false, ex.Message, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
            return Fail("browser_error", $"页面 JS 求值失败: {ex.Message}");
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

            if (session.TakeoverActive && normalized is not ("list" or "close_browser"))
            {
                return Fail("takeover_active", "当前浏览器会话正处于人工接管状态，请等待用户点击「完成并交回 Agent」后再执行操作。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);

            switch (normalized)
            {
                case "list":
                {
                    var state = session.ToDto("running");
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
                        if (!eval.Allowed && eval.RequiresLoopbackPortApproval && eval.NormalizedUri is not null)
                        {
                            var approvalResult = await RequestLoopbackPortApprovalAsync(session, eval.NormalizedUri, eval.TargetPort, ct).ConfigureAwait(false);
                            if (!approvalResult.Approved)
                            {
                                return Fail(approvalResult.ErrorCode ?? BrowserTargetPolicyGuard.ReasonLoopbackApprovalRequired, approvalResult.ErrorMessage ?? "访问非白名单本机端口未获批准。");
                            }

                            if (approvalResult.GrantedForSession)
                            {
                                session.ApproveLoopbackPortForSession(eval.TargetPort);
                            }

                            eval = _policyGuard.ValidateNavigationUrl(url, caller.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
                        }

                        if (!eval.Allowed || eval.NormalizedUri is null)
                        {
                            return Fail(eval.ReasonCode ?? "policy_denied", eval.Message ?? "新标签页 URL 被安全策略拦截。");
                        }

                        if (session.IsRemote)
                        {
                            var remotePort = eval.NormalizedUri.IsDefaultPort
                                ? (string.Equals(eval.NormalizedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                                : eval.NormalizedUri.Port;
                            var remoteIpEval = await _policyGuard.ResolveAndValidateEndpointAsync(
                                eval.NormalizedUri.IdnHost,
                                remotePort,
                                caller.WorkMode,
                                session.GetApprovedLoopbackPortsSnapshot(),
                                ct).ConfigureAwait(false);
                            if (!remoteIpEval.Allowed)
                            {
                                return Fail(remoteIpEval.ReasonCode ?? "policy_denied", remoteIpEval.Message ?? "远端浏览器新标签页地址被安全策略拦截。");
                            }
                        }

                        var navRefusal = await EnforcePersistentNavigationApprovalAsync(session, eval.NormalizedUri, ct).ConfigureAwait(false);
                        if (navRefusal is not null)
                        {
                            return navRefusal;
                        }

                        targetUri = eval.NormalizedUri;
                    }

                    // P1-12: Always create on about:blank so Fetch.enable and domains are armed BEFORE any navigation.
                    var tab = await CreateAndAttachTabAsync(proc, session, makeActive: true, ct).ConfigureAwait(false);
                    if (targetUri is not null)
                    {
                        var navJson = new JsonObject { ["url"] = targetUri.AbsoluteUri }.ToJsonString();
                        await proc.Connection.SendJsonAsync("Page.navigate", navJson, tab.CdpSessionId, ct).ConfigureAwait(false);
                        await WaitForDocumentSettleAsync(proc.Connection, tab, ct).ConfigureAwait(false);
                        var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
                        _ = await TryCaptureViewportJpegAsync(
                            proc.Connection,
                            session,
                            tab,
                            ct,
                            annotate: false,
                            elementsForAnnotation: snapshot.Elements,
                            cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);
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

                    await proc.Connection.SendJsonAsync("Target.activateTarget", new JsonObject { ["targetId"] = targetTab.TargetId }.ToJsonString(), ct: ct).ConfigureAwait(false);
                    _ = await TryCaptureViewportJpegAsync(proc.Connection, session, targetTab, ct).ConfigureAwait(false);
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
                    foreach (var cf in removed.ChildFrames)
                    {
                        _cdpSessionToAppSession.TryRemove(cf.CdpSessionId, out _);
                        _trackedTargets.TryRemove(cf.TargetId, out _);
                    }
                    try
                    {
                        await proc.Connection.SendJsonAsync("Target.closeTarget", new JsonObject { ["targetId"] = removed.TargetId }.ToJsonString(), ct: ct).ConfigureAwait(false);
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

    public async Task<BrowserOperationResult> RequestTakeoverAsync(
        BrowserTrustedCallerContext caller,
        string reason,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.PlanModeActive)
        {
            return Fail("plan_mode_active", "当前处于只读 Plan Mode，禁止发起浏览器人工接管。");
        }

        if (_controlPlane is { IsControlPlaneAvailable: false })
        {
            return Fail("control_plane_unavailable", "当前处于未启用控制面的独立模式，无法在桌面端交互完成人工接管。");
        }

        var (session, err) = await AcquireSessionAsync(caller, ct).ConfigureAwait(false);
        if (err is not null || session is null)
        {
            return err!;
        }

        if (session.IsRemote)
        {
            return Fail(
                "remote_cdp_no_takeover",
                "当前连接的是远端 CDP 浏览器实例，不支持唤出本机桌面窗口进行人工接管。");
        }

        if (session.Headless)
        {
            return Fail(
                "headless_mode_no_takeover",
                "当前受管浏览器运行在「无头模式」，不支持唤出窗口人工接管。请让用户在「设置 → 浏览器」中将运行模式切换为「桌面有头（支持人工接管）」并关闭当前浏览器会话后重试。");
        }

        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "需要人工完成登录、验证码或确认操作" : reason.Trim();
        Task<string?> completionTask;
        ICdpBrowserRuntime? shownProc = null;
        BrowserTabRecord? shownTab = null;

        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭。");
            }

            if (session.TakeoverActive)
            {
                return Fail("takeover_already_active", "当前浏览器会话已处于人工接管状态，请先完成当前人工接管。");
            }

            session.Touch();
            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);
            shownProc = proc;
            shownTab = tab;

            completionTask = session.BeginTakeover(effectiveReason, manual: false);
            try
            {
                await SetWindowVisibleAsync(proc.Connection, tab, visible: true, ct, throwOnShowFailure: true).ConfigureAwait(false);
            }
            catch (Exception showEx) when (showEx is not OperationCanceledException)
            {
                session.CancelTakeover("window_show_failed");
                return Fail("takeover_window_failed", $"无法将受管浏览器窗口唤出到前台：{showEx.Message}");
            }

            session.RecordAction("browser_request_takeover", "start", SanitizeOneLine(effectiveReason, 120), tab.Url, true, "已唤出桌面浏览器窗口等待人工接管", 0);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            // Release ExecutionLock while waiting for human takeover so CompleteTakeoverAsync/CloseSessionAsync never deadlock.
            session.ExecutionLock.Release();
        }

        var clampedTimeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 870));
        using var pauseGuard = AmbientTurnTimeoutController.Current?.PauseTimers();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(clampedTimeout);

        string? userNote;
        try
        {
            userNote = await completionTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.CancelTakeover("turn_cancelled");
            await TryHideSessionWindowAsync(session, shownProc, shownTab).ConfigureAwait(false);
            await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
            return Fail("takeover_cancelled", "人工接管已随回合取消而终止。");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            session.CancelTakeover("takeover_timeout");
            await TryHideSessionWindowAsync(session, shownProc, shownTab).ConfigureAwait(false);
            await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
            return Fail("takeover_timeout", string.Create(CultureInfo.InvariantCulture, $"等待人工接管完成超时（{(int)clampedTimeout.TotalSeconds} 秒）。"));
        }
        catch (Exception ex)
        {
            session.CancelTakeover("takeover_aborted");
            await TryHideSessionWindowAsync(session, shownProc, shownTab).ConfigureAwait(false);
            await PublishStateAsync(session, force: true, ct: CancellationToken.None).ConfigureAwait(false);
            var abortedByExit = ex is InvalidOperationException && ex.Message.Contains("browser_process_exited", StringComparison.Ordinal);
            return Fail(abortedByExit ? "browser_exited" : "takeover_aborted", $"人工接管已中断（{(abortedByExit ? "浏览器进程已关闭" : ex.Message)}）。");
        }

        // Human returned control: re-acquire ExecutionLock and capture fresh post-takeover snapshot & screenshot.
        await session.ExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "人工接管结束后浏览器会话已关闭。");
            }

            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);
            await SetWindowVisibleAsync(proc.Connection, tab, visible: false, ct).ConfigureAwait(false);
            await WaitForDocumentSettleAsync(proc.Connection, tab, ct, maxWaitMs: 2500).ConfigureAwait(false);
            var snapshot = await CaptureDomSnapshotCoreAsync(proc.Connection, session, tab, ct).ConfigureAwait(false);
            var screenshot = await TryCaptureViewportJpegAsync(
                proc.Connection,
                session,
                tab,
                ct,
                annotate: false,
                elementsForAnnotation: snapshot.Elements,
                cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);
            session.Touch();
            var summary = string.IsNullOrWhiteSpace(userNote)
                ? $"人工接管已完成并交回控制权 -> {tab.Url}"
                : $"人工接管已完成（备注: {SanitizeOneLine(userNote, 80)}）-> {tab.Url}";
            session.RecordAction("browser_request_takeover", "complete", null, tab.Url, true, summary, 0);
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
            _logger.LogWarning(ex, "Post-takeover snapshot failed for session {SessionId}", caller.SessionId);
            return Fail("takeover_post_snapshot_failed", $"人工接管已结束，但采集恢复后页面状态失败：{ex.Message}");
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> StartTakeoverAsync(
        string sessionId,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session) ||
            session.IsClosed)
        {
            return Fail("session_not_found", "当前会话尚未启动受管浏览器。");
        }

        if (session.IsRemote)
        {
            return Fail(
                "remote_cdp_no_takeover",
                "当前连接的是远端 CDP 浏览器实例，不支持唤出本机桌面窗口进行人工接管。");
        }

        if (session.Headless)
        {
            return Fail(
                "headless_mode_no_takeover",
                "当前浏览器运行在「无头模式」，不支持唤出窗口人工接管。请在「设置 → 浏览器」中切换到「桌面有头（支持人工接管）」后重新启动浏览器。");
        }

        if (!await session.ExecutionLock.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
        {
            return Fail("browser_busy", "当前浏览器正在执行操作或等待审批，请稍后再试。");
        }

        try
        {
            if (session.IsClosed)
            {
                return Fail("session_closed", "当前浏览器会话已关闭。");
            }

            if (session.TakeoverActive)
            {
                return Fail("takeover_already_active", "当前浏览器会话已处于人工接管状态。");
            }

            var proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            var tab = await EnsureActiveTabAsync(proc, session, ct).ConfigureAwait(false);
            var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "用户主动人工接管" : reason.Trim();
            _ = session.BeginTakeover(effectiveReason, manual: true);
            try
            {
                await SetWindowVisibleAsync(proc.Connection, tab, visible: true, ct, throwOnShowFailure: true).ConfigureAwait(false);
            }
            catch (Exception showEx) when (showEx is not OperationCanceledException)
            {
                session.CancelTakeover("window_show_failed");
                return Fail("takeover_window_failed", $"无法将受管浏览器窗口唤出到前台：{showEx.Message}");
            }

            session.RecordAction("browser_takeover", "start", SanitizeOneLine(effectiveReason, 120), tab.Url, true, "已将受管浏览器窗口移至前台供人工接管", 0);
            await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);

            return new BrowserOperationResult(
                Succeeded: true,
                Output: "已唤出桌面浏览器窗口进入人工接管状态。",
                Summary: "已进入人工接管",
                Url: tab.Url,
                Title: tab.Title);
        }
        finally
        {
            session.ExecutionLock.Release();
        }
    }

    public async Task<BrowserOperationResult> CompleteTakeoverAsync(
        string sessionId,
        string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session) ||
            session.IsClosed)
        {
            return Fail("session_not_found", "当前会话没有活跃的受管浏览器。");
        }

        if (!session.TakeoverActive)
        {
            return new BrowserOperationResult(true, "当前会话未处于人工接管状态。", Summary: "无需结束接管");
        }

        // Hide window FIRST before waking any blocked tool waiter so ExecutionLock contention never leaves the window visible.
        if (_sharedRuntime is { IsAlive: true } proc && session.GetActiveTab() is { } activeTab)
        {
            await SetWindowVisibleAsync(proc.Connection, activeTab, visible: false, ct).ConfigureAwait(false);
        }

        var wasManual = session.IsManualTakeover;
        _ = session.CompleteTakeover(note);

        // If this was a manual takeover (no RequestTakeoverAsync waiter taking a screenshot), capture refreshed screenshot here.
        if (wasManual && await session.ExecutionLock.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
        {
            try
            {
                if (!session.IsClosed && _sharedRuntime is { IsAlive: true } liveProc && session.GetActiveTab() is { } tab)
                {
                    var snapshot = await CaptureDomSnapshotCoreAsync(liveProc.Connection, session, tab, ct).ConfigureAwait(false);
                    _ = await TryCaptureViewportJpegAsync(
                        liveProc.Connection,
                        session,
                        tab,
                        ct,
                        annotate: false,
                        elementsForAnnotation: snapshot.Elements,
                        cachedSecretRects: snapshot.SecretRects).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Best-effort post-takeover snapshot refresh failed for session {SessionId}", sessionId);
            }
            finally
            {
                session.ExecutionLock.Release();
            }
        }

        await PublishStateAsync(session, force: true, ct: ct).ConfigureAwait(false);
        return new BrowserOperationResult(
            Succeeded: true,
            Output: "已结束人工接管并将浏览器控制权交回 Agent。",
            Summary: "已交回控制权");
    }

    public void CancelTakeover(string sessionId, string reason)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session))
        {
            session.CancelTakeover(reason);
            _ = TryHideSessionWindowAsync(session, _sharedRuntime, session.GetActiveTab());
        }
    }

    public BrowserSessionStateDto GetSessionState(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) && !session.IsClosed)
        {
            return session.ToDto("running");
        }

        var currentOpts = _options.CurrentValue;
        var isRemoteConfigured = !string.IsNullOrWhiteSpace(currentOpts.RemoteCdpWsUrl);
        return new BrowserSessionStateDto(
            SessionId: sessionId ?? string.Empty,
            Status: "idle",
            Headless: isRemoteConfigured || currentOpts.Headless,
            ProfileMode: isRemoteConfigured ? nameof(BrowserProfileMode.Ephemeral) : currentOpts.ProfileMode.ToString(),
            Url: null,
            Title: null,
            ScreenshotSeq: 0,
            ConsoleErrorCount: 0,
            Tabs: [],
            RecentActions: [],
            RecentConsole: [],
            UpdatedAtUtc: null,
            IsRemote: isRemoteConfigured);
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
        session.AbortTakeover(reason);

        if (string.Equals(_persistentOwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _persistentOwnerSessionId, null, sessionId);
        }

        foreach (var kvp in _cdpSessionToAppSession)
        {
            if (string.Equals(kvp.Value, sessionId, StringComparison.Ordinal))
            {
                _cdpSessionToAppSession.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _trackedTargets)
        {
            if (string.Equals(kvp.Value, sessionId, StringComparison.Ordinal))
            {
                _trackedTargets.TryRemove(kvp.Key, out _);
            }
        }

        try
        {
            var proc = _sharedRuntime;
            if (proc is not null && proc.IsAlive)
            {
                using var disposeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                disposeCts.CancelAfter(TimeSpan.FromSeconds(5));
                if (session.UsesDefaultContext)
                {
                    string? keepaliveTargetId = null;
                    try
                    {
                        var createKeepalive = await proc.Connection.SendJsonAsync(
                            "Target.createTarget",
                            "{\"url\":\"about:blank\"}",
                            ct: disposeCts.Token).ConfigureAwait(false);
                        keepaliveTargetId = createKeepalive.TryGetProperty("targetId", out var kId) ? kId.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(keepaliveTargetId))
                        {
                            _trackedTargets[keepaliveTargetId] = "__keepalive__";
                            if (!session.Headless)
                            {
                                await HideTargetWindowByIdAsync(proc.Connection, keepaliveTargetId, disposeCts.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }

                    var closedIds = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        var targetsRes = await proc.Connection.SendAsync("Target.getTargets", ct: disposeCts.Token).ConfigureAwait(false);
                        if (targetsRes.TryGetProperty("targetInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
                        {
                            var activeEphemeralCtxIds = _sessions.Values
                                .Where(static s => !s.UsesDefaultContext && !string.IsNullOrWhiteSpace(s.BrowserContextId))
                                .Select(static s => s.BrowserContextId)
                                .ToHashSet(StringComparer.Ordinal);

                            foreach (var info in infos.EnumerateArray())
                            {
                                var tType = info.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                                var tId = info.TryGetProperty("targetId", out var idp) ? idp.GetString() : null;
                                var ctxId = info.TryGetProperty("browserContextId", out var cp) ? cp.GetString() : null;
                                if (!string.Equals(tType, "page", StringComparison.OrdinalIgnoreCase) ||
                                    string.IsNullOrWhiteSpace(tId) ||
                                    string.Equals(tId, keepaliveTargetId, StringComparison.Ordinal) ||
                                    (!string.IsNullOrWhiteSpace(ctxId) && activeEphemeralCtxIds.Contains(ctxId)))
                                {
                                    continue;
                                }

                                closedIds.Add(tId);
                                try
                                {
                                    await proc.Connection.SendJsonAsync(
                                        "Target.closeTarget",
                                        new JsonObject { ["targetId"] = tId }.ToJsonString(),
                                        ct: disposeCts.Token).ConfigureAwait(false);
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }

                    foreach (var tab in session.GetTabsSnapshot())
                    {
                        if (closedIds.Contains(tab.TargetId) || string.Equals(tab.TargetId, keepaliveTargetId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        try
                        {
                            await proc.Connection.SendJsonAsync(
                                "Target.closeTarget",
                                new JsonObject { ["targetId"] = tab.TargetId }.ToJsonString(),
                                ct: disposeCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                else
                {
                    await proc.Connection.SendJsonAsync(
                        "Target.disposeBrowserContext",
                        new JsonObject { ["browserContextId"] = session.BrowserContextId }.ToJsonString(),
                        ct: disposeCts.Token).ConfigureAwait(false);
                }
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
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                IsRemote: session.IsRemote);
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
        var now = DateTimeOffset.UtcNow;
        var timeoutMinutes = Math.Clamp(_options.CurrentValue.IdleTimeoutMinutes, 1, 120);
        var cutoff = now - TimeSpan.FromMinutes(timeoutMinutes);

        foreach (var kvp in _sessions)
        {
            var state = kvp.Value;
            if (state.TakeoverActive)
            {
                // Auto-expire manual takeover if human exceeded 15m hard ceiling so persistent profiles never stay wedged forever.
                if (state.IsManualTakeover &&
                    state.TakeoverStartedAtUtc is { } startedAt &&
                    (now - startedAt) > ManualTakeoverMaxDuration)
                {
                    _logger.LogInformation("Auto-completing expired manual takeover for session {SessionId}", kvp.Key);
                    await CompleteTakeoverAsync(kvp.Key, "人工接管超过 15 分钟自动结束", ct).ConfigureAwait(false);
                }

                continue;
            }

            // P1-9: Never reap a session that is currently executing a CDP command or awaiting human approval.
            if (state.ExecutionLock.CurrentCount == 0)
            {
                continue;
            }

            if (state.LastActivityUtc < cutoff)
            {
                _logger.LogInformation("Closing idle browser session {SessionId} (last active {LastActive})", kvp.Key, state.LastActivityUtc);
                await CloseSessionAsync(kvp.Key, "idle_timeout", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(CdpBrowserSessionState? Session, BrowserOperationResult? Error)> AcquireSessionAsync(
        BrowserTrustedCallerContext caller,
        CancellationToken ct)
    {
        try
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

            // P1-7 / P2-2: If runtime target (local/remote, executable, headless, profile mode) changed since launch,
            // drain idle sessions; if active busy sessions remain, fail-closed with browser_restart_required.
            var isRemoteDesired = !string.IsNullOrWhiteSpace(opts.RemoteCdpWsUrl);
            if (isRemoteDesired && opts.ProfileMode == BrowserProfileMode.Persistent)
            {
                return (null, Fail(
                    "remote_cdp_persistent_unsupported",
                    "远端 CDP 浏览器模式仅支持会话隔离上下文（Ephemeral），不支持开启本地持久化 Profile，请在「设置 → 浏览器」中将登录态策略切换为「会话隔离临时 Profile」。"));
            }

            var desiredRuntimeKey = ComputeDesiredRuntimeKey(opts);
            var activeProc = _sharedRuntime;
            if (activeProc is { IsAlive: true } &&
                !string.Equals(activeProc.RuntimeKey, desiredRuntimeKey, StringComparison.Ordinal))
            {
                foreach (var sid in _sessions.Keys.ToArray())
                {
                    if (_sessions.TryGetValue(sid, out var staleCandidate) &&
                        !staleCandidate.TakeoverActive &&
                        staleCandidate.ExecutionLock.CurrentCount > 0)
                    {
                        await CloseSessionAsync(sid, "browser_mode_changed", ct).ConfigureAwait(false);
                    }
                }

                if (!_sessions.IsEmpty)
                {
                    return (null, Fail(
                        "browser_restart_required",
                        "浏览器运行模式或远端 CDP 配置已变更，但仍有其他会话正在使用旧配置浏览器。请先关闭当前活跃会话的浏览器以使新配置生效。"));
                }
            }

            var isPersistent = !isRemoteDesired && opts.ProfileMode == BrowserProfileMode.Persistent;
            if (isPersistent)
            {
                // Single-active session exclusivity for persistent profile (never hijack active or non-idle owner).
                var existingOwnerId = _persistentOwnerSessionId;
                if (!string.IsNullOrWhiteSpace(existingOwnerId) &&
                    !string.Equals(existingOwnerId, caller.SessionId, StringComparison.Ordinal) &&
                    _sessions.TryGetValue(existingOwnerId, out var ownerSession) &&
                    !ownerSession.IsClosed)
                {
                    var idleThreshold = TimeSpan.FromMinutes(Math.Clamp(opts.IdleTimeoutMinutes, 1, 120));
                    var isOwnerIdle = (DateTimeOffset.UtcNow - ownerSession.LastActivityUtc) >= idleThreshold;
                    if (!isOwnerIdle || ownerSession.TakeoverActive || !await ownerSession.ExecutionLock.WaitAsync(0, ct).ConfigureAwait(false))
                    {
                        return (null, Fail(
                            "persistent_profile_busy",
                            $"持久化浏览器 Profile 当前正被会话 '{existingOwnerId}' 占用，请先完成或关闭该会话的浏览器后再试。"));
                    }

                    ownerSession.ExecutionLock.Release();
                    await CloseSessionAsync(existingOwnerId, "persistent_profile_idle_reclaimed", ct).ConfigureAwait(false);
                }
            }

            var maxSessions = isPersistent ? 1 : Math.Clamp(opts.MaxConcurrentSessions, 1, 8);
            if (_sessions.Count >= maxSessions)
            {
                var oldest = _sessions.Values
                    .OrderBy(static s => s.LastActivityUtc)
                    .FirstOrDefault(static s => s.ExecutionLock.CurrentCount > 0 && !s.TakeoverActive);
                if (oldest is not null)
                {
                    await CloseSessionAsync(oldest.SessionId, "capacity_evicted", ct).ConfigureAwait(false);
                }
                else
                {
                    return (null, Fail("browser_capacity_reached", string.Create(CultureInfo.InvariantCulture, $"活跃浏览器会话已达上限 ({maxSessions}) 且均在执行中，请稍后重试或关闭旧会话的浏览器。")));
                }
            }

            ICdpBrowserRuntime proc;
            try
            {
                proc = await EnsureRuntimeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, Fail("browser_launch_failed", ex.Message));
            }

            // Re-check runtime key after acquiring/launching under lock.
            if (!string.Equals(proc.RuntimeKey, ComputeDesiredRuntimeKey(_options.CurrentValue), StringComparison.Ordinal))
            {
                return (null, Fail(
                    "browser_restart_required",
                    "浏览器运行模式或远端 CDP 配置正在切换中，请关闭旧会话浏览器后重试。"));
            }

            var sessionStagingDir = Path.Combine(_runtimeBaseDir, "staging", $"{SanitizeFileSegment(caller.SessionId)}-{Guid.NewGuid().ToString("N")[..8]}");
            var usePersistentDefaultContext = !proc.IsRemote && proc.ProfileMode == BrowserProfileMode.Persistent;
            if (usePersistentDefaultContext)
            {
                var priorOwner = Interlocked.CompareExchange(ref _persistentOwnerSessionId, caller.SessionId, null);
                if (priorOwner is not null && !string.Equals(priorOwner, caller.SessionId, StringComparison.Ordinal))
                {
                    return (null, Fail("persistent_profile_busy", "持久化浏览器 Profile 当前正被另一个会话并发占用，请稍后重试。"));
                }

                try
                {
                    await ConfigureDownloadBehaviorAsync(proc.Connection, browserContextId: null, opts, caller.WorkspaceRoot, isRemote: false, ct).ConfigureAwait(false);

                    var persistentState = new CdpBrowserSessionState(
                        caller.SessionId,
                        browserContextId: string.Empty,
                        caller.WorkMode,
                        outboundProxy: null,
                        headless: proc.Headless,
                        profileMode: BrowserProfileMode.Persistent,
                        usesDefaultContext: true,
                        workspaceRoot: caller.WorkspaceRoot,
                        stagingDirectory: sessionStagingDir,
                        isRemote: false);

                    if (!_sessions.TryAdd(caller.SessionId, persistentState))
                    {
                        Interlocked.CompareExchange(ref _persistentOwnerSessionId, null, caller.SessionId);
                        await persistentState.DisposeAsync().ConfigureAwait(false);
                        if (_sessions.TryGetValue(caller.SessionId, out var winner) && !winner.IsClosed)
                        {
                            winner.SyncWorkMode(caller.WorkMode);
                            return (winner, null);
                        }

                        return (null, Fail("browser_session_race", "浏览器会话状态并发变更，请重试。"));
                    }

                    return (persistentState, null);
                }
                catch
                {
                    Interlocked.CompareExchange(ref _persistentOwnerSessionId, null, caller.SessionId);
                    throw;
                }
            }

            CdpBrowserSessionState? newState = null;
            var sessionProxy = proc.IsRemote
                ? null
                : new BrowserOutboundProxy(
                    _policyGuard,
                    _portRegistry,
                    () => newState?.WorkMode ?? SessionWorkMode.Daily,
                    () => newState?.GetApprovedLoopbackPortsSnapshot(),
                    _logger);

            string? browserContextId = null;
            try
            {
                var createCtxParams = sessionProxy is null
                    ? new JsonObject { ["disposeOnDetach"] = true }.ToJsonString()
                    : new JsonObject
                    {
                        ["disposeOnDetach"] = true,
                        ["proxyServer"] = sessionProxy.ProxyServerUrl,
                        ["proxyBypassList"] = "<-loopback>"
                    }.ToJsonString();

                var ctxRes = await proc.Connection.SendJsonAsync("Target.createBrowserContext", createCtxParams, ct: ct).ConfigureAwait(false);
                browserContextId = ctxRes.GetProperty("browserContextId").GetString()
                    ?? throw new InvalidOperationException("Target.createBrowserContext 未返回 browserContextId。");

                // P1-13 & P2-2: Fail-closed if download policy cannot be enforced; remote mode forces deny.
                await ConfigureDownloadBehaviorAsync(proc.Connection, browserContextId, opts, caller.WorkspaceRoot, proc.IsRemote, ct).ConfigureAwait(false);

                newState = new CdpBrowserSessionState(
                    caller.SessionId,
                    browserContextId,
                    caller.WorkMode,
                    sessionProxy,
                    headless: proc.Headless,
                    profileMode: BrowserProfileMode.Ephemeral,
                    usesDefaultContext: false,
                    workspaceRoot: caller.WorkspaceRoot,
                    stagingDirectory: sessionStagingDir,
                    isRemote: proc.IsRemote);
                if (!_sessions.TryAdd(caller.SessionId, newState))
                {
                    try
                    {
                        await proc.Connection.SendJsonAsync(
                            "Target.disposeBrowserContext",
                            new JsonObject { ["browserContextId"] = browserContextId }.ToJsonString(),
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
                            new JsonObject { ["browserContextId"] = browserContextId }.ToJsonString(),
                            ct: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                if (sessionProxy is not null)
                {
                    await sessionProxy.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AcquireSessionAsync failed for session {SessionId}", caller.SessionId);
            return (null, Fail("browser_init_failed", $"初始化受管浏览器会话失败: {ex.Message}"));
        }
    }

    private string ComputeDesiredRuntimeKey(BrowserOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.RemoteCdpWsUrl))
        {
            var check = BrowserRemoteEndpointGuard.ValidateRemoteCdpWsUrlSyntax(opts.RemoteCdpWsUrl);
            var normalized = check.Allowed && check.NormalizedUri is not null
                ? check.NormalizedUri.AbsoluteUri
                : opts.RemoteCdpWsUrl.Trim();
            return $"remote:{normalized}";
        }

        var detected = _locator.Detect(opts.ExecutablePath);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"local:{opts.Headless}:{opts.ProfileMode}:{detected.ExecutablePath ?? string.Empty}");
    }

    private async Task<ICdpBrowserRuntime> EnsureRuntimeAsync(CancellationToken ct)
    {
        var desiredOpts = _options.CurrentValue;
        var desiredKey = ComputeDesiredRuntimeKey(desiredOpts);
        var current = _sharedRuntime;
        if (current is { IsAlive: true } &&
            (!_sessions.IsEmpty || string.Equals(current.RuntimeKey, desiredKey, StringComparison.Ordinal)))
        {
            return current;
        }

        await _processGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            desiredOpts = _options.CurrentValue;
            desiredKey = ComputeDesiredRuntimeKey(desiredOpts);
            var currentLocked = _sharedRuntime;
            if (currentLocked is { IsAlive: true } &&
                (!_sessions.IsEmpty || string.Equals(currentLocked.RuntimeKey, desiredKey, StringComparison.Ordinal)))
            {
                return currentLocked;
            }

            if (_sharedRuntime is not null)
            {
                await _sharedRuntime.DisposeAsync().ConfigureAwait(false);
                _sharedRuntime = null;
                _persistentOwnerSessionId = null;
                foreach (var sid in _sessions.Keys.ToArray())
                {
                    if (_sessions.TryRemove(sid, out var stale))
                    {
                        stale.MarkClosed();
                        await stale.DisposeAsync().ConfigureAwait(false);
                    }
                }
                _cdpSessionToAppSession.Clear();
                _trackedTargets.Clear();
            }

            ICdpBrowserRuntime runtime;
            if (!string.IsNullOrWhiteSpace(desiredOpts.RemoteCdpWsUrl))
            {
                runtime = await CdpRemoteBrowserRuntime.ConnectAsync(
                    desiredOpts.RemoteCdpWsUrl,
                    _portRegistry,
                    _logger,
                    ct).ConfigureAwait(false);
            }
            else
            {
                var detected = _locator.Detect(desiredOpts.ExecutablePath);
                if (!detected.Available || string.IsNullOrWhiteSpace(detected.ExecutablePath))
                {
                    throw new InvalidOperationException(
                        detected.DiagnosticMessage ?? "未检测到可用的本机 Chromium/Chrome/Edge 浏览器。");
                }

                Func<SessionWorkMode> workModeProvider = desiredOpts.ProfileMode == BrowserProfileMode.Persistent
                    ? () =>
                    {
                        var ownerId = _persistentOwnerSessionId;
                        return !string.IsNullOrWhiteSpace(ownerId) && _sessions.TryGetValue(ownerId, out var s)
                            ? s.WorkMode
                            : SessionWorkMode.Daily;
                    }
                    : static () => SessionWorkMode.Daily;

                Func<IReadOnlySet<int>?> approvedPortsProvider = desiredOpts.ProfileMode == BrowserProfileMode.Persistent
                    ? () =>
                    {
                        var ownerId = _persistentOwnerSessionId;
                        return !string.IsNullOrWhiteSpace(ownerId) && _sessions.TryGetValue(ownerId, out var s)
                            ? s.GetApprovedLoopbackPortsSnapshot()
                            : null;
                    }
                    : static () => null;

                runtime = await CdpManagedBrowserProcess.LaunchAsync(
                    detected.ExecutablePath,
                    _runtimeBaseDir,
                    _policyGuard,
                    _portRegistry,
                    _logger,
                    ct,
                    headless: desiredOpts.Headless,
                    profileMode: desiredOpts.ProfileMode,
                    persistentProfileDir: _persistentProfileDir,
                    processProxyWorkModeProvider: workModeProvider,
                    processProxyApprovedPortsProvider: approvedPortsProvider).ConfigureAwait(false);
            }

            runtime.Connection.EventReceived += OnCdpEventReceived;
            runtime.Terminated += () => OnManagedRuntimeTerminated(runtime);
            try
            {
                await runtime.Connection.SendJsonAsync("Target.setDiscoverTargets", "{\"discover\":true}", ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Target.setDiscoverTargets failed during startup.");
            }

            _sharedRuntime = runtime;
            return runtime;
        }
        finally
        {
            _processGate.Release();
        }
    }

    private void OnManagedRuntimeTerminated(ICdpBrowserRuntime exitedRuntime)
    {
        if (!ReferenceEquals(_sharedRuntime, exitedRuntime))
        {
            return;
        }

        _persistentOwnerSessionId = null;
        foreach (var state in _sessions.Values)
        {
            state.AbortTakeover("browser_process_exited");
        }
    }

    private async Task<BrowserTabRecord> EnsureActiveTabAsync(
        ICdpBrowserRuntime proc,
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
        ICdpBrowserRuntime proc,
        CdpBrowserSessionState session,
        bool makeActive,
        CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        // Always start on about:blank so Fetch.enable and auto-attach are active before any remote URL loads.
        var createParams = session.UsesDefaultContext
            ? new JsonObject
            {
                ["url"] = "about:blank",
                ["width"] = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
                ["height"] = Math.Clamp(opts.DefaultViewportHeight, 480, 1440)
            }.ToJsonString()
            : new JsonObject
            {
                ["url"] = "about:blank",
                ["width"] = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
                ["height"] = Math.Clamp(opts.DefaultViewportHeight, 480, 1440),
                ["browserContextId"] = session.BrowserContextId
            }.ToJsonString();

        var createRes = await proc.Connection.SendJsonAsync("Target.createTarget", createParams, ct: ct).ConfigureAwait(false);
        var targetId = createRes.GetProperty("targetId").GetString()
            ?? throw new InvalidOperationException("Target.createTarget 未返回 targetId。");

        _trackedTargets[targetId] = session.SessionId;
        var attachParams = new JsonObject { ["targetId"] = targetId, ["flatten"] = true }.ToJsonString();
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

        await InitializeTabDomainsAsync(proc.Connection, cdpSessionId, session.IsRemote, ct).ConfigureAwait(false);
        if (!session.IsRemote && !session.Headless && !session.TakeoverActive)
        {
            await SetWindowVisibleAsync(proc.Connection, tab, visible: false, ct).ConfigureAwait(false);
        }

        return tab;
    }

    private async Task InitializeTabDomainsAsync(CdpConnection connection, string cdpSessionId, bool isRemote, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        await connection.SendAsync("Page.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendAsync("Runtime.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendAsync("Network.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendJsonAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        try
        {
            await connection.SendJsonAsync(
                "Target.setAutoAttach",
                "{\"autoAttach\":true,\"waitForDebuggerOnStart\":false,\"flatten\":true}",
                sessionId: cdpSessionId,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target.setAutoAttach failed for CDP session {CdpSessionId}", cdpSessionId);
        }

        var metricsParams = new JsonObject
        {
            ["width"] = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
            ["height"] = Math.Clamp(opts.DefaultViewportHeight, 480, 1440),
            ["deviceScaleFactor"] = 1,
            ["mobile"] = false
        }.ToJsonString();
        await connection.SendJsonAsync("Emulation.setDeviceMetricsOverride", metricsParams, cdpSessionId, ct).ConfigureAwait(false);

        var fetchParams = isRemote
            ? new JsonObject { ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }) }.ToJsonString()
            : new JsonObject
            {
                ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*", ["resourceType"] = "Document" })
            }.ToJsonString();
        await connection.SendJsonAsync("Fetch.enable", fetchParams, cdpSessionId, ct).ConfigureAwait(false);
    }

    private void OnCdpEventReceived(CdpEventMessage evt)
    {
        if (string.Equals(evt.Method, "Target.targetCreated", StringComparison.Ordinal))
        {
            _ = HandleTargetCreatedAsync(evt.Params);
            return;
        }

        if (string.Equals(evt.Method, "Target.targetDestroyed", StringComparison.Ordinal))
        {
            HandleTargetDestroyed(evt.Params);
            return;
        }

        if (string.Equals(evt.Method, "Target.attachedToTarget", StringComparison.Ordinal))
        {
            _ = HandleTargetAttachedAsync(evt.SessionId, evt.Params);
            return;
        }

        if (string.Equals(evt.Method, "Target.detachedFromTarget", StringComparison.Ordinal))
        {
            HandleTargetDetached(evt.Params);
            return;
        }

        if (string.Equals(evt.Method, "Browser.downloadProgress", StringComparison.Ordinal))
        {
            _ = HandleDownloadProgressAsync(evt.Params);
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

            case "Page.fileChooserOpened":
                session.RecordConsoleEntry("warn", "Intercepted OS file chooser dialog in automation mode; use browser_act(action=\"upload_file\") or human takeover.");
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

    private async Task HandleTargetAttachedAsync(string? parentCdpSessionId, JsonElement parameters)
    {
        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive || !parameters.TryGetProperty("targetInfo", out var info))
        {
            return;
        }

        var childCdpSessionId = parameters.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;
        var targetType = info.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
        var targetId = info.TryGetProperty("targetId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(childCdpSessionId) ||
            string.IsNullOrWhiteSpace(targetId) ||
            !string.Equals(targetType, "iframe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CdpBrowserSessionState? owner = null;
        BrowserTabRecord? parentTab = null;

        if (!string.IsNullOrWhiteSpace(parentCdpSessionId) &&
            _cdpSessionToAppSession.TryGetValue(parentCdpSessionId, out var appSessionId) &&
            _sessions.TryGetValue(appSessionId, out var candidateOwner) &&
            !candidateOwner.IsClosed)
        {
            owner = candidateOwner;
            parentTab = candidateOwner.FindTabByCdpSessionId(parentCdpSessionId);
            if (parentTab is null)
            {
                foreach (var tabCandidate in candidateOwner.GetTabsSnapshot())
                {
                    if (tabCandidate.ChildFrames.Any(c => string.Equals(c.CdpSessionId, parentCdpSessionId, StringComparison.Ordinal)))
                    {
                        parentTab = tabCandidate;
                        break;
                    }
                }
            }

            parentTab ??= candidateOwner.GetActiveTab();
        }

        if (owner is null)
        {
            var ctxId = info.TryGetProperty("browserContextId", out var ctxProp) ? ctxProp.GetString() : null;
            owner = !string.IsNullOrWhiteSpace(ctxId)
                ? _sessions.Values.FirstOrDefault(s => !s.IsClosed && !s.UsesDefaultContext && string.Equals(s.BrowserContextId, ctxId, StringComparison.Ordinal))
                : null;
            owner ??= _sessions.Values.FirstOrDefault(static s => !s.IsClosed && s.UsesDefaultContext);
            parentTab = owner?.GetActiveTab();
        }

        if (owner is null || parentTab is null)
        {
            return;
        }

        var childUrl = info.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;
        _trackedTargets[targetId] = owner.SessionId;
        _cdpSessionToAppSession[childCdpSessionId] = owner.SessionId;
        owner.AddOrUpdateChildFrame(
            parentTab.TargetId,
            new BrowserChildFrameRecord
            {
                TargetId = targetId,
                CdpSessionId = childCdpSessionId,
                Url = childUrl
            });

        try
        {
            await InitializeChildFrameDomainsAsync(proc.Connection, childCdpSessionId, owner.IsRemote, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed initializing OOPIF child frame {ChildSessionId}", childCdpSessionId);
        }
    }

    private void HandleTargetDetached(JsonElement parameters)
    {
        var detachedId = parameters.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(detachedId))
        {
            return;
        }

        if (_cdpSessionToAppSession.TryRemove(detachedId, out var appSessionId) &&
            _sessions.TryGetValue(appSessionId, out var session))
        {
            session.RemoveChildFrameByCdpSessionId(detachedId);
        }
    }

    private static async Task InitializeChildFrameDomainsAsync(CdpConnection connection, string cdpSessionId, bool isRemote, CancellationToken ct)
    {
        await connection.SendAsync("Page.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        await connection.SendAsync("Runtime.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        try
        {
            await connection.SendJsonAsync(
                "Target.setAutoAttach",
                "{\"autoAttach\":true,\"waitForDebuggerOnStart\":false,\"flatten\":true}",
                sessionId: cdpSessionId,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var fetchParams = isRemote
            ? new JsonObject { ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }) }.ToJsonString()
            : new JsonObject
            {
                ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*", ["resourceType"] = "Document" })
            }.ToJsonString();
        await connection.SendJsonAsync("Fetch.enable", fetchParams, cdpSessionId, ct).ConfigureAwait(false);
    }

    private async Task HandleTargetCreatedAsync(JsonElement parameters)
    {
        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive || !parameters.TryGetProperty("targetInfo", out var info))
        {
            return;
        }

        var targetType = info.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
        var targetId = info.TryGetProperty("targetId", out var idProp) ? idProp.GetString() : null;
        if (!string.Equals(targetType, "page", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(targetId) ||
            !_trackedTargets.TryAdd(targetId, "__pending__"))
        {
            return;
        }

        var ctxId = info.TryGetProperty("browserContextId", out var ctxProp) ? ctxProp.GetString() : null;
        var owner = !string.IsNullOrWhiteSpace(ctxId)
            ? _sessions.Values.FirstOrDefault(s => !s.IsClosed && !s.UsesDefaultContext && string.Equals(s.BrowserContextId, ctxId, StringComparison.Ordinal))
            : null;
        owner ??= _sessions.Values.FirstOrDefault(static s => !s.IsClosed && s.UsesDefaultContext);

        if (owner is null || owner.HasTab(targetId))
        {
            _trackedTargets.TryRemove(targetId, out _);
            return;
        }

        try
        {
            var attachRes = await proc.Connection.SendJsonAsync(
                "Target.attachToTarget",
                new JsonObject { ["targetId"] = targetId, ["flatten"] = true }.ToJsonString(),
                ct: CancellationToken.None).ConfigureAwait(false);
            var cdpSessionId = attachRes.TryGetProperty("sessionId", out var sProp) ? sProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(cdpSessionId))
            {
                _trackedTargets.TryRemove(targetId, out _);
                return;
            }

            var url = info.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "about:blank" : "about:blank";
            var title = info.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty;
            var tab = new BrowserTabRecord
            {
                TargetId = targetId,
                CdpSessionId = cdpSessionId,
                Url = url,
                Title = title
            };

            _trackedTargets[targetId] = owner.SessionId;
            _cdpSessionToAppSession[cdpSessionId] = owner.SessionId;
            owner.AddOrUpdateTab(tab, makeActive: false);
            await InitializeTabDomainsAsync(proc.Connection, cdpSessionId, owner.IsRemote, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _trackedTargets.TryRemove(targetId, out _);
            _logger.LogDebug(ex, "Failed auto-attaching popup/created target {TargetId}", targetId);
        }
    }

    private async Task HandleDownloadProgressAsync(JsonElement parameters)
    {
        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive)
        {
            return;
        }

        var guid = parameters.TryGetProperty("guid", out var guidProp) ? guidProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(guid))
        {
            return;
        }

        var totalBytes = parameters.TryGetProperty("totalBytes", out var tb) && tb.TryGetInt64(out var t) ? t : 0L;
        var receivedBytes = parameters.TryGetProperty("receivedBytes", out var rb) && rb.TryGetInt64(out var r) ? r : 0L;
        if (totalBytes > MaxDownloadFileSizeBytes || receivedBytes > MaxDownloadFileSizeBytes)
        {
            try
            {
                await proc.Connection.SendJsonAsync(
                    "Browser.cancelDownload",
                    new JsonObject { ["guid"] = guid }.ToJsonString(),
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed cancelling oversized browser download {Guid}", guid);
            }
        }
    }

    private void HandleTargetDestroyed(JsonElement parameters)
    {
        var targetId = parameters.TryGetProperty("targetId", out var prop) ? prop.GetString() : null;
        if (string.IsNullOrWhiteSpace(targetId)) return;

        _trackedTargets.TryRemove(targetId, out _);
        foreach (var session in _sessions.Values)
        {
            session.RemoveChildFrameByTargetId(targetId);
            if (session.RemoveTab(targetId, out var removed) && removed is not null)
            {
                _cdpSessionToAppSession.TryRemove(removed.CdpSessionId, out _);
                foreach (var cf in removed.ChildFrames)
                {
                    _cdpSessionToAppSession.TryRemove(cf.CdpSessionId, out _);
                    _trackedTargets.TryRemove(cf.TargetId, out _);
                }
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
        foreach (var cf in tab.ChildFrames)
        {
            _cdpSessionToAppSession.TryRemove(cf.CdpSessionId, out _);
            _trackedTargets.TryRemove(cf.TargetId, out _);
        }
        session.RecordConsoleEntry("error", $"Browser tab [{tab.TargetId}] crashed and was detached.");
    }

    private async Task HandleFetchRequestPausedAsync(string cdpSessionId, CdpBrowserSessionState session, JsonElement parameters)
    {
        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive) return;

        var requestId = parameters.TryGetProperty("requestId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId)) return;

        var url = parameters.TryGetProperty("request", out var reqProp) && reqProp.TryGetProperty("url", out var urlProp)
            ? urlProp.GetString()
            : null;
        var resourceType = parameters.TryGetProperty("resourceType", out var rtProp) ? rtProp.GetString() : null;

        var allowed = string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);
        if (!allowed && !string.IsNullOrWhiteSpace(url))
        {
            if (session.IsRemote)
            {
                var isSubResource = !string.Equals(resourceType, "Document", StringComparison.OrdinalIgnoreCase);
                if (isSubResource &&
                    (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)))
                {
                    allowed = true;
                }
                else
                {
                    var urlForCheck = url;
                    if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                    {
                        urlForCheck = string.Concat("http://", url.AsSpan(5));
                    }
                    else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                    {
                        urlForCheck = string.Concat("https://", url.AsSpan(6));
                    }

                    var approved = session.GetApprovedLoopbackPortsSnapshot();
                    var navEval = _policyGuard.ValidateNavigationUrl(urlForCheck, session.WorkMode, approved);
                    if (!navEval.Allowed || navEval.NormalizedUri is null)
                    {
                        allowed = false;
                        session.RecordNetworkError($"Blocked remote request to '{url}': {navEval.Message}");
                    }
                    else
                    {
                        var port = navEval.NormalizedUri.IsDefaultPort
                            ? (string.Equals(navEval.NormalizedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                            : navEval.NormalizedUri.Port;
                        var ipEval = await _policyGuard.ResolveAndValidateEndpointAsync(
                            navEval.NormalizedUri.IdnHost,
                            port,
                            session.WorkMode,
                            approved,
                            CancellationToken.None).ConfigureAwait(false);
                        allowed = ipEval.Allowed;
                        if (!allowed)
                        {
                            session.RecordNetworkError($"Blocked remote endpoint '{url}': {ipEval.Message}");
                        }
                    }
                }
            }
            else
            {
                var eval = _policyGuard.ValidateNavigationUrl(url, session.WorkMode, session.GetApprovedLoopbackPortsSnapshot());
                allowed = eval.Allowed;
                if (!allowed)
                {
                    session.RecordNetworkError($"Blocked document navigation to '{url}': {eval.Message}");
                }
            }
        }

        try
        {
            if (allowed)
            {
                await proc.Connection.SendJsonAsync(
                    "Fetch.continueRequest",
                    new JsonObject { ["requestId"] = requestId }.ToJsonString(),
                    cdpSessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await proc.Connection.SendJsonAsync(
                    "Fetch.failRequest",
                    new JsonObject { ["requestId"] = requestId, ["errorReason"] = "BlockedByClient" }.ToJsonString(),
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
        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive) return;

        var dialogType = parameters.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "alert";
        var message = parameters.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : string.Empty;
        session.RecordConsoleEntry("warn", $"Auto-dismissed browser {dialogType} dialog: {message}");

        try
        {
            var accept = string.Equals(dialogType, "beforeunload", StringComparison.OrdinalIgnoreCase);
            await proc.Connection.SendJsonAsync(
                "Page.handleJavaScriptDialog",
                new JsonObject { ["accept"] = accept }.ToJsonString(),
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

        var url = frame.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        var tab = session.FindTabByCdpSessionId(cdpSessionId);
        if (tab is not null)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                session.UpdateTabMetadata(tab.TargetId, url, title: null, invalidateIsolatedWorld: true);
            }

            return;
        }

        if (session.FindChildFrameByCdpSessionId(cdpSessionId) is not null)
        {
            session.UpdateChildFrameUrl(cdpSessionId, url);
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

    private async Task<BrowserOperationResult?> EnforcePersistentNavigationApprovalAsync(
        CdpBrowserSessionState session,
        Uri targetUri,
        CancellationToken ct)
    {
        if (session.ProfileMode != BrowserProfileMode.Persistent ||
            string.Equals(targetUri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase) ||
            NetworkAddressClassifier.IsLoopbackHostname(targetUri.Host))
        {
            return null;
        }

        var origin = BrowserTargetPolicyGuard.GetOriginString(targetUri);
        if (_approvalCoordinator is not null &&
            _approvalCoordinator.IsSessionOriginApproved(session.SessionId, origin, "persistent_nav"))
        {
            return null;
        }

        if (_approvalCoordinator is null)
        {
            return Fail("approval_unavailable", "持久化登录态模式下访问外部站点需要控制面确认，但审批协调器不可用。");
        }

        session.Touch();
        var outcome = await _approvalCoordinator.RequestApprovalAsync(
            new BrowserApprovalPrompt(
                SessionId: session.SessionId,
                Tool: "browser_navigate",
                Action: "persistent_origin_navigation",
                TargetUrl: targetUri.AbsoluteUri,
                TargetOrigin: origin,
                TargetDescription: $"允许持久化登录态浏览器导航至外部站点 {origin}",
                RiskReason: "当前处于持久化登录态 Profile，访问外部站点会携带已保存登录 Cookie/状态，请确认站点归属与导航安全性。",
                ExpressionOrPath: targetUri.AbsoluteUri,
                AllowSessionScope: true),
            ct).ConfigureAwait(false);
        session.Touch();

        if (!outcome.Approved)
        {
            return Fail(outcome.ErrorCode ?? "approval_denied", outcome.ErrorMessage ?? "持久化登录态外部导航请求被拒绝或已过期。");
        }

        return null;
    }

    private async Task<(BrowserOperationResult? Refusal, bool WaitedForApproval)> EnforceInteractionApprovalAsync(
        BrowserTrustedCallerContext caller,
        CdpBrowserSessionState session,
        BrowserTabRecord tab,
        string liveUrl,
        string action,
        BrowserActRequest request,
        CdpSnapshotElement? targetElement,
        CdpLiveRefProbe? liveProbe,
        CdpActiveElementProbe? activeProbe,
        bool isSensitive,
        bool isPersistentRemoteWrite,
        StagedUploadFile? stagedUpload,
        CancellationToken ct)
    {
        if (action is "scroll" or "wait" or "hover")
        {
            return (null, false);
        }

        var policy = _options.CurrentValue.InteractionApprovalPolicy;
        var requiresApproval = isSensitive || policy switch
        {
            BrowserInteractionApprovalPolicy.AlwaysAsk => true,
            BrowserInteractionApprovalPolicy.HighRiskOnly => false,
            BrowserInteractionApprovalPolicy.AutoInSession => false,
            _ => true
        };

        if (!requiresApproval)
        {
            return (null, false);
        }

        var effectiveUrl = string.IsNullOrWhiteSpace(liveUrl) ? tab.Url : liveUrl;
        var origin = ExtractOrigin(effectiveUrl);
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
        var desc = action == "upload_file" && targetElement is not null
            ? $"向 [{targetElement.Ref}] 上传本地工作区文件 {Path.GetFileName(stagedUpload?.CanonicalSourcePath)}"
            : targetElement is not null
                ? $"{action} [{targetElement.Ref}] {liveProbe?.Role ?? targetElement.Role} \"{elemName}\""
                : $"{action} {SanitizeOneLine(request.Key, 40)} ({elemName})".Trim();

        var riskReason = action == "upload_file"
            ? "上传本地工作区文件到目标网站属于数据外发高风险操作，必须单次人工确认（已计算摘要并锁定不可变副本）。"
            : isPersistentRemoteWrite
                ? "当前处于持久化登录态 Profile，向非本地网站执行写入/提交操作需要单次人工确认。"
                : isSensitive
                    ? "检测到敏感提交/支付/删除或表单提交操作，需要人工确认。"
                    : "当前浏览器安全策略为 AlwaysAsk，执行页面交互前需要人工确认。";

        var detail = action == "upload_file" && stagedUpload is not null
            ? string.Create(CultureInfo.InvariantCulture, $"file={stagedUpload.CanonicalSourcePath} ({stagedUpload.SizeBytes} bytes, sha256={stagedUpload.Sha256Hex})")
            : action is "type" or "select_option" && !string.IsNullOrEmpty(request.Text)
                ? $"输入内容: {SanitizeOneLine(request.Text, 120)}"
                : effectiveUrl;

        session.Touch();
        var outcome = await _approvalCoordinator.RequestApprovalAsync(
            new BrowserApprovalPrompt(
                SessionId: caller.SessionId,
                Tool: "browser_act",
                Action: action,
                TargetUrl: effectiveUrl,
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

    private void OnOptionsChanged(BrowserOptions updated)
    {
        if (updated.AllowControlledDownloads)
        {
            return;
        }

        var proc = _sharedRuntime;
        if (proc is null || !proc.IsAlive)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await proc.Connection.SendJsonAsync(
                    "Browser.setDownloadBehavior",
                    "{\"behavior\":\"deny\"}",
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            foreach (var session in _sessions.Values)
            {
                if (session.IsClosed || session.UsesDefaultContext || string.IsNullOrWhiteSpace(session.BrowserContextId))
                {
                    continue;
                }

                try
                {
                    await proc.Connection.SendJsonAsync(
                        "Browser.setDownloadBehavior",
                        new JsonObject { ["behavior"] = "deny", ["browserContextId"] = session.BrowserContextId }.ToJsonString(),
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        });
    }

    private static async Task ConfigureDownloadBehaviorAsync(
        CdpConnection connection,
        string? browserContextId,
        BrowserOptions opts,
        string? workspaceRoot,
        bool isRemote,
        CancellationToken ct)
    {
        if (!isRemote &&
            opts.AllowControlledDownloads &&
            BrowserWorkspacePathGuard.TryPrepareControlledDownloadDirectory(workspaceRoot, out var downloadDir) &&
            !string.IsNullOrWhiteSpace(downloadDir))
        {
            var allowJson = string.IsNullOrWhiteSpace(browserContextId)
                ? new JsonObject { ["behavior"] = "allowAndName", ["downloadPath"] = downloadDir, ["eventsEnabled"] = true }.ToJsonString()
                : new JsonObject
                {
                    ["behavior"] = "allowAndName",
                    ["downloadPath"] = downloadDir,
                    ["eventsEnabled"] = true,
                    ["browserContextId"] = browserContextId
                }.ToJsonString();
            await connection.SendJsonAsync("Browser.setDownloadBehavior", allowJson, ct: ct).ConfigureAwait(false);
            return;
        }

        var denyJson = string.IsNullOrWhiteSpace(browserContextId)
            ? new JsonObject { ["behavior"] = "deny" }.ToJsonString()
            : new JsonObject { ["behavior"] = "deny", ["browserContextId"] = browserContextId }.ToJsonString();
        await connection.SendJsonAsync("Browser.setDownloadBehavior", denyJson, ct: ct).ConfigureAwait(false);
    }

    private static async Task SetFileInputFilesInIsolatedWorldAsync(
        CdpConnection connection,
        string cdpSessionId,
        Func<int?> getWorldId,
        Action<int?> setWorldId,
        string refId,
        string stagedFilePath,
        CancellationToken ct)
    {
        await connection.SendAsync("DOM.enable", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        string? objectId = null;
        try
        {
            var contextId = await EnsureIsolatedWorldContextIdAsync(connection, cdpSessionId, getWorldId, setWorldId, ct).ConfigureAwait(false);
            var evalParams = new JsonObject
            {
                ["expression"] = BrowserSnapshotScript.BuildResolveRefElementJs(EscapeJsString(refId)),
                ["contextId"] = contextId,
                ["returnByValue"] = false,
                ["awaitPromise"] = false
            }.ToJsonString();

            var evalRes = await connection.SendJsonAsync("Runtime.evaluate", evalParams, cdpSessionId, ct).ConfigureAwait(false);
            objectId = evalRes.TryGetProperty("result", out var r) && r.TryGetProperty("objectId", out var oid)
                ? oid.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(objectId))
            {
                throw new InvalidOperationException($"无法解析文件上传控件 [{refId}] 的 DOM 对象引用。");
            }

            var descRes = await connection.SendJsonAsync(
                "DOM.describeNode",
                new JsonObject { ["objectId"] = objectId }.ToJsonString(),
                cdpSessionId,
                ct).ConfigureAwait(false);
            var backendNodeId = descRes.GetProperty("node").GetProperty("backendNodeId").GetInt32();
            if (backendNodeId <= 0)
            {
                throw new InvalidOperationException($"DOM.describeNode 未返回有效 backendNodeId ({backendNodeId})。");
            }

            // DOM.setFileInputFiles natively fires input + change events on the input element.
            var setFilesParams = new JsonObject
            {
                ["backendNodeId"] = backendNodeId,
                ["files"] = new JsonArray(stagedFilePath)
            }.ToJsonString();
            await connection.SendJsonAsync("DOM.setFileInputFiles", setFilesParams, cdpSessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                try
                {
                    await connection.SendJsonAsync(
                        "Runtime.releaseObject",
                        new JsonObject { ["objectId"] = objectId }.ToJsonString(),
                        cdpSessionId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                await connection.SendAsync("DOM.disable", sessionId: cdpSessionId, ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task SetWindowVisibleAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        bool visible,
        CancellationToken ct,
        bool throwOnShowFailure = false)
    {
        try
        {
            var winRes = await connection.SendJsonAsync(
                "Browser.getWindowForTarget",
                new JsonObject { ["targetId"] = tab.TargetId }.ToJsonString(),
                ct: ct).ConfigureAwait(false);
            if (!winRes.TryGetProperty("windowId", out var widProp))
            {
                if (visible && throwOnShowFailure)
                {
                    throw new InvalidOperationException("Browser.getWindowForTarget 未返回 windowId。");
                }

                return;
            }

            var windowId = widProp.GetInt32();
            if (visible)
            {
                // Step 1: restore normal state if user previously minimized/maximized/fullscreened the window.
                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject { ["windowId"] = windowId, ["bounds"] = new JsonObject { ["windowState"] = "normal" } }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);
                // Step 2: place window cleanly on primary desktop bounds.
                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject
                    {
                        ["windowId"] = windowId,
                        ["bounds"] = new JsonObject { ["left"] = 80, ["top"] = 80, ["width"] = 1280, ["height"] = 820 }
                    }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);

                try { await connection.SendAsync("Emulation.clearDeviceMetricsOverride", sessionId: tab.CdpSessionId, ct: ct).ConfigureAwait(false); } catch (Exception) { }
                try { await connection.SendJsonAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":false}", tab.CdpSessionId, ct).ConfigureAwait(false); } catch (Exception) { }
                await connection.SendAsync("Page.bringToFront", sessionId: tab.CdpSessionId, ct: ct).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    var opts = _options.CurrentValue;
                    await connection.SendJsonAsync(
                        "Emulation.setDeviceMetricsOverride",
                        new JsonObject
                        {
                            ["width"] = Math.Clamp(opts.DefaultViewportWidth, 640, 1920),
                            ["height"] = Math.Clamp(opts.DefaultViewportHeight, 480, 1440),
                            ["deviceScaleFactor"] = 1,
                            ["mobile"] = false
                        }.ToJsonString(),
                        tab.CdpSessionId,
                        ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                try { await connection.SendJsonAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}", tab.CdpSessionId, ct).ConfigureAwait(false); } catch (Exception) { }

                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject { ["windowId"] = windowId, ["bounds"] = new JsonObject { ["windowState"] = "normal" } }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        await connection.SendJsonAsync(
                            "Browser.setWindowBounds",
                            new JsonObject
                            {
                                ["windowId"] = windowId,
                                ["bounds"] = new JsonObject { ["left"] = -32000, ["top"] = -32000, ["width"] = 1280, ["height"] = 800 }
                            }.ToJsonString(),
                            ct: ct).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception)
                    {
                    }
                }

                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject { ["windowId"] = windowId, ["bounds"] = new JsonObject { ["windowState"] = "minimized" } }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(visible && throwOnShowFailure))
        {
            _logger.LogDebug(ex, "Browser.setWindowBounds(visible={Visible}) failed for target {TargetId}", visible, tab.TargetId);
        }
    }

    private async Task HideTargetWindowByIdAsync(CdpConnection connection, string targetId, CancellationToken ct)
    {
        try
        {
            var winRes = await connection.SendJsonAsync(
                "Browser.getWindowForTarget",
                new JsonObject { ["targetId"] = targetId }.ToJsonString(),
                ct: ct).ConfigureAwait(false);
            if (!winRes.TryGetProperty("windowId", out var widProp))
            {
                return;
            }

            var windowId = widProp.GetInt32();
            if (OperatingSystem.IsWindows())
            {
                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject
                    {
                        ["windowId"] = windowId,
                        ["bounds"] = new JsonObject { ["left"] = -32000, ["top"] = -32000, ["width"] = 1280, ["height"] = 800 }
                    }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);
            }
            else
            {
                await connection.SendJsonAsync(
                    "Browser.setWindowBounds",
                    new JsonObject { ["windowId"] = windowId, ["bounds"] = new JsonObject { ["windowState"] = "minimized" } }.ToJsonString(),
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HideTargetWindowByIdAsync failed for target {TargetId}", targetId);
        }
    }

    private async Task TryHideSessionWindowAsync(CdpBrowserSessionState session, ICdpBrowserRuntime? proc, BrowserTabRecord? tab)
    {
        if (session.IsRemote || session.Headless || proc is null || !proc.IsAlive || tab is null)
        {
            return;
        }

        try
        {
            await SetWindowVisibleAsync(proc.Connection, tab, visible: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static string SanitizeFileSegment(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        var result = sb.ToString();
        return result.Length > 48 ? result[..48] : result;
    }

    private static bool IsLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return NetworkAddressClassifier.IsLoopbackHostname(uri.Host);
    }

    private static Task<int> EnsureIsolatedWorldContextIdAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        CancellationToken ct)
        => EnsureIsolatedWorldContextIdAsync(
            connection,
            tab.CdpSessionId,
            () => tab.IsolatedWorldContextId,
            id => tab.IsolatedWorldContextId = id,
            ct);

    private static async Task<int> EnsureIsolatedWorldContextIdAsync(
        CdpConnection connection,
        string cdpSessionId,
        Func<int?> getWorldId,
        Action<int?> setWorldId,
        CancellationToken ct)
    {
        if (getWorldId() is { } existing && existing > 0)
        {
            return existing;
        }

        var frameTree = await connection.SendAsync("Page.getFrameTree", sessionId: cdpSessionId, ct: ct).ConfigureAwait(false);
        var frameId = frameTree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Page.getFrameTree 未返回主 Frame ID。");

        var worldRes = await connection.SendJsonAsync(
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = frameId, ["worldName"] = IsolatedWorldName, ["grantUniveralAccess"] = false }.ToJsonString(),
            cdpSessionId,
            ct).ConfigureAwait(false);

        var ctxId = worldRes.GetProperty("executionContextId").GetInt32();
        setWorldId(ctxId);
        return ctxId;
    }

    private static Task<string?> EvaluateInIsolatedWorldAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        string expression,
        CancellationToken ct,
        bool awaitPromise = false)
        => EvaluateInIsolatedWorldAsync(
            connection,
            tab.CdpSessionId,
            () => tab.IsolatedWorldContextId,
            id => tab.IsolatedWorldContextId = id,
            expression,
            ct,
            awaitPromise);

    private static async Task<string?> EvaluateInIsolatedWorldAsync(
        CdpConnection connection,
        string cdpSessionId,
        Func<int?> getWorldId,
        Action<int?> setWorldId,
        string expression,
        CancellationToken ct,
        bool awaitPromise = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var contextId = await EnsureIsolatedWorldContextIdAsync(connection, cdpSessionId, getWorldId, setWorldId, ct).ConfigureAwait(false);
            var evalParams = new JsonObject
            {
                ["expression"] = expression,
                ["contextId"] = contextId,
                ["returnByValue"] = true,
                ["awaitPromise"] = awaitPromise
            }.ToJsonString();

            try
            {
                var result = await connection.SendJsonAsync("Runtime.evaluate", evalParams, cdpSessionId, ct).ConfigureAwait(false);
                if (result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                {
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                }

                return null;
            }
            catch (InvalidOperationException ex) when (attempt == 0 && ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
            {
                // Navigation invalidated the old isolated world; recreate once.
                setWorldId(null);
            }
        }

        return null;
    }

    private async Task<CdpSnapshotPayload> CaptureDomSnapshotCoreAsync(
        CdpConnection connection,
        CdpBrowserSessionState session,
        BrowserTabRecord tab,
        CancellationToken ct)
    {
        var rawJson = await EvaluateInIsolatedWorldAsync(
            connection,
            tab,
            BrowserSnapshotScript.BuildExtractInteractiveSnapshotJs(startIndex: 0),
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

        var mergedElements = new List<CdpSnapshotElement>(payload.Elements ?? []);
        var mergedSecretRects = new List<CdpBoundingBox>(payload.SecretRects ?? []);
        var textParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(payload.PageText))
        {
            textParts.Add(payload.PageText);
        }

        var childFrames = session.GetChildFramesSnapshot(tab.TargetId);
        if (childFrames.Count > 0)
        {
            var crossSlots = payload.CrossOriginFrames ?? [];
            var usedSlotIndices = new HashSet<int>();
            foreach (var child in childFrames.Take(8))
            {
                if (mergedElements.Count >= 180)
                {
                    break;
                }

                try
                {
                    var childRaw = await EvaluateInIsolatedWorldAsync(
                        connection,
                        child.CdpSessionId,
                        () => child.IsolatedWorldContextId,
                        id => child.IsolatedWorldContextId = id,
                        BrowserSnapshotScript.BuildExtractInteractiveSnapshotJs(startIndex: mergedElements.Count),
                        ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(childRaw))
                    {
                        continue;
                    }

                    var childPayload = JsonSerializer.Deserialize(childRaw, CdpJsonContext.Default.CdpSnapshotPayload);
                    if (childPayload is null)
                    {
                        continue;
                    }

                    var childUrl = !string.IsNullOrWhiteSpace(childPayload.Url) ? childPayload.Url! : child.Url;
                    if (!string.IsNullOrWhiteSpace(childUrl))
                    {
                        child.Url = childUrl;
                    }

                    var childOrigin = ExtractOrigin(childUrl);
                    var frameTag = string.IsNullOrWhiteSpace(childOrigin)
                        ? "cross-origin iframe"
                        : $"iframe:{SanitizeOneLine(childOrigin, 60)}";

                    if (!string.IsNullOrWhiteSpace(childPayload.PageText))
                    {
                        textParts.Add($"[{frameTag}]\n{childPayload.PageText}");
                    }

                    var slotIdx = MatchCrossOriginFrameSlot(crossSlots, usedSlotIndices, childUrl, childOrigin);
                    if (slotIdx >= 0)
                    {
                        usedSlotIndices.Add(slotIdx);
                        var slot = crossSlots[slotIdx];
                        if (slot.Width > 1 && slot.Height > 1)
                        {
                            foreach (var el in childPayload.Elements ?? [])
                            {
                                if (mergedElements.Count >= 180)
                                {
                                    break;
                                }

                                el.FrameCdpSessionId = child.CdpSessionId;
                                el.FrameOrigin = childOrigin;
                                el.FrameLabel = frameTag;
                                el.FrameOffsetX = slot.X;
                                el.FrameOffsetY = slot.Y;
                                el.X += slot.X;
                                el.Y += slot.Y;
                                mergedElements.Add(el);
                            }

                            foreach (var sr in childPayload.SecretRects ?? [])
                            {
                                mergedSecretRects.Add(new CdpBoundingBox
                                {
                                    X = sr.X + slot.X,
                                    Y = sr.Y + slot.Y,
                                    Width = sr.Width,
                                    Height = sr.Height
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping unresponsive OOPIF child frame {ChildSessionId}", child.CdpSessionId);
                }
            }
        }

        var combinedPayload = new CdpSnapshotPayload
        {
            Url = payload.Url,
            Title = payload.Title,
            PageText = string.Join("\n\n", textParts),
            Elements = mergedElements,
            SecretRects = mergedSecretRects,
            CrossOriginFrames = payload.CrossOriginFrames
        };

        session.UpdateTabMetadata(tab.TargetId, combinedPayload.Url, combinedPayload.Title);
        session.ReplaceTabElementRefs(tab.TargetId, mergedElements);
        session.ReplaceTabSecretRects(tab.TargetId, mergedSecretRects);
        return combinedPayload;
    }

    private static int MatchCrossOriginFrameSlot(
        List<CdpCrossOriginFrameSlot> slots,
        HashSet<int> usedIndices,
        string childUrl,
        string childOrigin)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            if (usedIndices.Contains(i)) continue;
            if (string.Equals(slots[i].Url, childUrl, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(slots[i].Src, childUrl, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        if (!string.IsNullOrWhiteSpace(childOrigin))
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (usedIndices.Contains(i)) continue;
                var slotOrigin = ExtractOrigin(!string.IsNullOrWhiteSpace(slots[i].Url) ? slots[i].Url : slots[i].Src);
                if (string.Equals(slotOrigin, childOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (slots.Count == 1 && usedIndices.Count == 0)
        {
            return 0;
        }

        return -1;
    }

    private static async Task<CdpLiveRefProbe?> LocateAndVerifyRefInIsolatedWorldAsync(
        CdpConnection connection,
        string cdpSessionId,
        Func<int?> getWorldId,
        Action<int?> setWorldId,
        string refId,
        CancellationToken ct)
    {
        var raw = await EvaluateInIsolatedWorldAsync(
            connection,
            cdpSessionId,
            getWorldId,
            setWorldId,
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
        string cdpSessionId,
        Func<int?> getWorldId,
        Action<int?> setWorldId,
        CancellationToken ct)
    {
        var raw = await EvaluateInIsolatedWorldAsync(
            connection,
            cdpSessionId,
            getWorldId,
            setWorldId,
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
        BrowserTabRecord tab,
        CancellationToken ct,
        bool annotate = false,
        IReadOnlyList<CdpSnapshotElement>? elementsForAnnotation = null,
        IReadOnlyList<CdpBoundingBox>? cachedSecretRects = null)
    {
        var overlayInstalled = false;
        try
        {
            var secretRects = cachedSecretRects is not null
                ? new List<CdpBoundingBox>(cachedSecretRects)
                : await CollectLiveMainSecretRectsAsync(connection, tab, ct).ConfigureAwait(false);

            var annotations = new List<CdpAnnotationItem>();
            if (annotate && elementsForAnnotation is { Count: > 0 })
            {
                foreach (var el in elementsForAnnotation.Take(150))
                {
                    if (el.Width <= 1 || el.Height <= 1)
                    {
                        continue;
                    }

                    annotations.Add(new CdpAnnotationItem
                    {
                        Ref = el.Ref,
                        Role = el.Role,
                        IsSensitive = el.IsPassword || el.IsSensitiveSubmit,
                        X = Math.Max(0, el.X - el.Width / 2.0),
                        Y = Math.Max(0, el.Y - el.Height / 2.0),
                        Width = el.Width,
                        Height = el.Height
                    });
                }
            }

            if (secretRects.Count > 0 || annotations.Count > 0)
            {
                var specJson = JsonSerializer.Serialize(
                    new CdpVisualOverlaySpec
                    {
                        SecretRects = secretRects,
                        Annotations = annotations
                    },
                    CdpJsonContext.Default.CdpVisualOverlaySpec);
                try
                {
                    _ = await EvaluateInIsolatedWorldAsync(
                        connection,
                        tab,
                        BrowserSnapshotScript.BuildInstallVisualOverlayJs(specJson),
                        ct,
                        awaitPromise: true).ConfigureAwait(false);
                    overlayInstalled = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Installing visual overlay before screenshot failed; proceeding cleanly.");
                }
            }

            var captureParams = new JsonObject
            {
                ["format"] = "jpeg",
                ["quality"] = 78,
                ["fromSurface"] = true
            }.ToJsonString();
            var result = await connection.SendJsonAsync("Page.captureScreenshot", captureParams, tab.CdpSessionId, ct).ConfigureAwait(false);
            if (result.TryGetProperty("data", out var dataProp) && dataProp.GetString() is { Length: > 0 } base64)
            {
                var bytes = Convert.FromBase64String(base64);
                return session.RecordScreenshot(bytes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Page.captureScreenshot failed for session {SessionId}", session.SessionId);
        }
        finally
        {
            if (overlayInstalled)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await EvaluateInIsolatedWorldAsync(
                        connection,
                        tab,
                        BrowserSnapshotScript.BuildRemoveVisualOverlayJs(),
                        cleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        return null;
    }

    private static async Task<List<CdpBoundingBox>> CollectLiveMainSecretRectsAsync(
        CdpConnection connection,
        BrowserTabRecord tab,
        CancellationToken ct)
    {
        try
        {
            var raw = await EvaluateInIsolatedWorldAsync(
                connection,
                tab,
                BrowserSnapshotScript.BuildExtractSecretRectsJs(),
                ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return JsonSerializer.Deserialize(raw, CdpJsonContext.Default.ListCdpBoundingBox) ?? [];
            }
        }
        catch (Exception)
        {
        }

        return [];
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
            new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }.ToJsonString(),
            cdpSessionId,
            ct);

    private static async Task DispatchMouseClickAsync(CdpConnection connection, string cdpSessionId, double x, double y, int clickCount, CancellationToken ct)
    {
        for (var i = 1; i <= clickCount; i++)
        {
            await connection.SendJsonAsync(
                "Input.dispatchMouseEvent",
                new JsonObject { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = i }.ToJsonString(),
                cdpSessionId,
                ct).ConfigureAwait(false);
            await connection.SendJsonAsync(
                "Input.dispatchMouseEvent",
                new JsonObject { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = i }.ToJsonString(),
                cdpSessionId,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task DispatchKeyPressAsync(CdpConnection connection, string cdpSessionId, string key, CancellationToken ct)
    {
        var (code, windowsVirtualKeyCode, text) = MapKey(key);
        await connection.SendJsonAsync(
            "Input.dispatchKeyEvent",
            new JsonObject
            {
                ["type"] = "rawKeyDown",
                ["key"] = key,
                ["code"] = code,
                ["windowsVirtualKeyCode"] = windowsVirtualKeyCode
            }.ToJsonString(),
            cdpSessionId,
            ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(text))
        {
            await connection.SendJsonAsync(
                "Input.dispatchKeyEvent",
                new JsonObject
                {
                    ["type"] = "char",
                    ["key"] = key,
                    ["code"] = code,
                    ["text"] = text,
                    ["windowsVirtualKeyCode"] = windowsVirtualKeyCode
                }.ToJsonString(),
                cdpSessionId,
                ct).ConfigureAwait(false);
        }
        await connection.SendJsonAsync(
            "Input.dispatchKeyEvent",
            new JsonObject
            {
                ["type"] = "keyUp",
                ["key"] = key,
                ["code"] = code,
                ["windowsVirtualKeyCode"] = windowsVirtualKeyCode
            }.ToJsonString(),
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
                .PublishStateUpdatedAsync(session.SessionId, session.ToDto("running"), ct)
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

        _optionsChangeSubscription?.Dispose();

        foreach (var sid in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(sid, out var state))
            {
                state.MarkClosed();
                await state.DisposeAsync().ConfigureAwait(false);
            }
        }

        _cdpSessionToAppSession.Clear();
        _trackedTargets.Clear();

        if (_sharedRuntime is not null)
        {
            await _sharedRuntime.DisposeAsync().ConfigureAwait(false);
            _sharedRuntime = null;
        }

        _processGate.Dispose();
    }
}
