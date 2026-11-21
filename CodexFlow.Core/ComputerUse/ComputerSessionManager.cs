// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.ComputerUse;

/// <summary>
/// Central orchestrator for desktop Computer Use: manages the exclusive desktop lease,
/// window pinning, DPI-aware coordinate resolution, self-UI occlusion masking,
/// host window avoidance readiness barrier, approval coordination, physical override
/// detection, and millisecond-level emergency stop.
/// </summary>
public sealed class ComputerSessionManager : IComputerSessionManager
{
    private const int MaxRecentActions = 30;
    private const int AvoidancePollIntervalMs = 40;
    private const int AvoidanceMaxWaitMs = 600;
    private const int PostActionCaptureDelayMs = 350;
    private const double PhysicalMouseOverrideThresholdPx = 120d;
    private static readonly TimeSpan StatePublishThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ManualTakeoverMaxDuration = TimeSpan.FromMinutes(15);

    private readonly IOptionsMonitor<ComputerOptions> _options;
    private readonly IComputerDriver _driver;
    private readonly ComputerSecurityGuard _securityGuard;
    private readonly ILogger<ComputerSessionManager> _logger;
    private readonly IModelSettingsService? _modelSettings;
    private readonly IComputerApprovalCoordinator? _approvalCoordinator;
    private readonly IComputerStateEventPublisher? _statePublisher;
    private readonly IControlPlaneStateProvider? _controlPlane;

    private readonly object _leaseGate = new();
    private string? _leaseOwnerSessionId;
    private DateTimeOffset _leaseLastActivityUtc;

    private readonly ConcurrentDictionary<string, SessionRuntimeState> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public ComputerSessionManager(
        IOptionsMonitor<ComputerOptions> options,
        IComputerDriver driver,
        ComputerSecurityGuard securityGuard,
        ILogger<ComputerSessionManager> logger,
        IModelSettingsService? modelSettings = null,
        IComputerApprovalCoordinator? approvalCoordinator = null,
        IComputerStateEventPublisher? statePublisher = null,
        IControlPlaneStateProvider? controlPlane = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _securityGuard = securityGuard ?? throw new ArgumentNullException(nameof(securityGuard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelSettings = modelSettings;
        _approvalCoordinator = approvalCoordinator;
        _statePublisher = statePublisher;
        _controlPlane = controlPlane;
    }

    public ComputerDriverStatusDto GetDriverStatus()
        => _driver.GetStatus(_options.CurrentValue.EmergencyStopShortcut);

    public RegisterProtectedWindowsResponse RegisterProtectedWindows(
        int processId,
        IReadOnlyList<string>? windowHandles)
        => _securityGuard.RegisterProtectedWindows(processId, windowHandles);

    public async Task<ComputerOperationResult> GetScreenInfoAsync(
        ComputerTrustedCallerContext caller,
        string? pinWindowHandle = null,
        string? pinProcessName = null,
        bool unpin = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryValidateEnabledAndNotDisposed(out var disabledErr))
        {
            return disabledErr!;
        }

        var wantsPinChange = unpin || !string.IsNullOrWhiteSpace(pinWindowHandle) || !string.IsNullOrWhiteSpace(pinProcessName);
        if (wantsPinChange && !TryAcquireDesktopLease(caller.SessionId, out var busyErr))
        {
            return busyErr!;
        }

        var session = GetOrCreateSession(caller.SessionId);
        var displays = _driver.GetDisplays();
        var windows = _driver.GetVisibleWindows(
            _securityGuard.ProtectedPidsSnapshot,
            _securityGuard.ProtectedHandlesSnapshot);

        if (unpin)
        {
            session.ClearPin();
        }
        else if (!string.IsNullOrWhiteSpace(pinWindowHandle) || !string.IsNullOrWhiteSpace(pinProcessName))
        {
            var matched = MatchPinTarget(windows, pinWindowHandle, pinProcessName);
            if (matched is null)
            {
                return Fail("window_not_found", "未找到匹配指定句柄或进程名的可见桌面窗口。");
            }

            if (_securityGuard.IsProtectedWindow(matched))
            {
                return Fail("self_ui_interaction_forbidden", "安全策略禁止锁定 CodexFlow 自身受保护窗口。");
            }

            if (_securityGuard.IsBlockedProcessName(matched.ProcessName))
            {
                return Fail("blocked_sensitive_application", $"安全策略禁止锁定敏感应用程序（{matched.ProcessName}）。");
            }

            _ = _driver.TryBringWindowToForeground(matched.WindowHandle);
            session.SetPin(matched);
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"平台驱动：{_driver.GetStatus().DriverName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"显示器数量：{displays.Count}");
        foreach (var d in displays)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  - [{d.DisplayId}] {(d.IsPrimary ? "主屏" : "副屏")} Origin=({d.X},{d.Y}) Size={d.Width}x{d.Height} Pixel={d.PixelWidth}x{d.PixelHeight} Scale={d.ScaleFactor:0.##}x");
        }

        sb.AppendLine();
        if (session.PinnedWindowHandle is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"当前锁定窗口：{session.PinnedProcessName} — \"{session.PinnedWindowTitle}\" ({session.PinnedWindowHandle})");
        }
        else
        {
            sb.AppendLine("当前锁定窗口：无（默认全主屏/全桌面视野）");
        }

        sb.AppendLine("可见窗口列表（受保护的 CodexFlow 窗口已标注 [PROTECTED] 且不可操作）：");
        foreach (var w in windows.Take(32))
        {
            var flags = new List<string>();
            if (w.IsForeground) flags.Add("前台");
            if (w.IsMinimized) flags.Add("最小化");
            if (w.IsProtected) flags.Add("PROTECTED");
            else if (_securityGuard.IsBlockedProcessName(w.ProcessName)) flags.Add("BLOCKED");
            var flagSuffix = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  - handle={w.WindowHandle} | proc={w.ProcessName} (pid={w.ProcessId}) | bounds=({w.X},{w.Y},{w.Width}x{w.Height}) | title=\"{w.Title}\"{flagSuffix}");
        }

        session.Touch();
        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
        return new ComputerOperationResult(
            Succeeded: true,
            Output: sb.ToString().TrimEnd(),
            Summary: $"已获取桌面信息（{displays.Count} 屏，{windows.Count} 个可见窗口）",
            Displays: displays,
            Windows: windows);
    }

    public async Task<ComputerOperationResult> CaptureScreenshotAsync(
        ComputerTrustedCallerContext caller,
        bool fullDesktop = false,
        int? displayIndex = null,
        bool? includeGrid = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryValidateEnabledAndNotDisposed(out var disabledErr))
        {
            return disabledErr!;
        }

        if (!IsMultimodalCapable(out var multimodalErr))
        {
            return multimodalErr!;
        }

        if (!TryAcquireDesktopLease(caller.SessionId, out var busyErr))
        {
            return busyErr!;
        }

        var session = GetOrCreateSession(caller.SessionId);
        if (session.EmergencyStopped)
        {
            return Fail("emergency_stopped", "本会话 Computer Use 已被用户紧急停止，需由用户发起新一轮对话后恢复。");
        }

        if (_options.CurrentValue.AutoMinimizeHostWindow && fullDesktop)
        {
            await RequestHostWindowAvoidanceAsync(session, ct).ConfigureAwait(false);
        }

        try
        {
            var capture = CaptureAndProcessFrame(
                session,
                fullDesktop,
                displayIndex,
                includeGrid ?? _options.CurrentValue.EnableGridOverlay,
                markerImgPoint: null,
                dragEndImgPoint: null);

            session.UpdateCapture(capture.Screenshot, capture.Viewport);
            session.RecordAction(
                tool: "computer_screenshot",
                action: "screenshot",
                targetProcess: session.PinnedProcessName,
                targetTitle: session.PinnedWindowTitle,
                x: null,
                y: null,
                succeeded: true,
                summary: $"捕获桌面截图 {capture.Screenshot.Width}x{capture.Screenshot.Height}",
                durationMs: capture.ElapsedMs);

            await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
            return new ComputerOperationResult(
                Succeeded: true,
                Output: FormatCaptureDescription(capture.Viewport, session),
                Summary: $"已捕获桌面截图 ({capture.Screenshot.Width}x{capture.Screenshot.Height})",
                Screenshot: capture.Screenshot,
                Viewport: capture.Viewport);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Computer Use screenshot failed for session {SessionId}", caller.SessionId);
            var code = ex is UnauthorizedAccessException ? "permission_denied" : "capture_failed";
            return Fail(code, ex.Message);
        }
        finally
        {
            if (session.HostWindowAvoidanceRequested)
            {
                session.HostWindowAvoidanceRequested = false;
                await PublishStateAsync(session, force: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<ComputerOperationResult> ActAsync(
        ComputerTrustedCallerContext caller,
        ComputerActRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidateEnabledAndNotDisposed(out var disabledErr))
        {
            return disabledErr!;
        }

        if (caller.PlanModeActive)
        {
            return Fail("plan_mode_active", "当前处于只读 Plan Mode，禁止执行 computer_act 键鼠交互。");
        }

        if (!IsMultimodalCapable(out var multimodalErr))
        {
            return multimodalErr!;
        }

        if (!TryAcquireDesktopLease(caller.SessionId, out var busyErr))
        {
            return busyErr!;
        }

        var session = GetOrCreateSession(caller.SessionId);
        if (session.EmergencyStopped)
        {
            return Fail("emergency_stopped", "本会话 Computer Use 已被用户紧急停止，需由用户发起新一轮对话后恢复。");
        }

        if (session.TakeoverPending)
        {
            return Fail("takeover_in_progress", "当前正等待人工接管操作完成，请等待用户在桌面端点击「完成并交回 Agent」。");
        }

        if (_securityGuard.HasAnyPendingApprovalFreeze(caller.SessionId))
        {
            return Fail("pending_approval_freeze", "安全策略冻结：当前会话仍有待处理的控制面审批请求，处理完成后才可执行桌面操作。");
        }

        var action = request.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(action))
        {
            return Fail("invalid_action", "参数 action 不能为空。");
        }

        if (action == "wait")
        {
            var waitMs = Math.Clamp(request.DurationMs ?? 1000, 100, 15_000);
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.EnterActionScope());
            try
            {
                await Task.Delay(waitMs, waitCts.Token).ConfigureAwait(false);
                var frame = CaptureAndProcessFrame(session, false, null, _options.CurrentValue.EnableGridOverlay, null, null);
                session.UpdateCapture(frame.Screenshot, frame.Viewport);
                await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
                return new ComputerOperationResult(
                    Succeeded: true,
                    Output: $"已完成等待 {waitMs}ms。\n{FormatCaptureDescription(frame.Viewport, session)}",
                    Summary: $"等待 {waitMs}ms",
                    Screenshot: frame.Screenshot,
                    Viewport: frame.Viewport);
            }
            catch (OperationCanceledException) when (session.EmergencyStopped)
            {
                return Fail("emergency_stopped", "等待被用户紧急停止。");
            }
            finally
            {
                session.ExitActionScope();
            }
        }

        // Physical mouse override check: if human moved mouse > 120px shortly after agent's last move, yield control.
        if (session.TryDetectPhysicalMouseOverride(_driver, PhysicalMouseOverrideThresholdPx, out var overrideDistance))
        {
            session.ClearLastAgentCursor();
            return Fail(
                "physical_mouse_override_detected",
                $"检测到用户刚刚手动大幅移动了物理鼠标（位移 {overrideDistance:0}px），已自动让出桌面控制权。请先通过 computer_screenshot 重新确认屏幕状态后再继续。");
        }

        var isCoordinateAction = action is "click" or "double_click" or "right_click" or "middle_click" or "move" or "drag" or "scroll";
        var isKeyboardAction = action is "type_text" or "key_combo";
        if (!isCoordinateAction && !isKeyboardAction)
        {
            return Fail("unsupported_action", $"不支持的动作 '{request.Action}'。支持：click, double_click, right_click, middle_click, move, drag, scroll, type_text, key_combo, wait。");
        }

        ScreenPoint? targetScreen = null;
        ScreenPoint? dragEndScreen = null;
        ComputerCaptureViewport? viewport = session.LastViewport;

        if (isCoordinateAction)
        {
            if (request.X is null || request.Y is null)
            {
                return Fail("missing_coordinates", $"动作 '{action}' 必须提供 x 和 y 坐标。");
            }

            if (viewport is null)
            {
                // Auto-bootstrap viewport on first coordinate action without requiring a separate screenshot call.
                var bootstrap = CaptureAndProcessFrame(session, false, null, _options.CurrentValue.EnableGridOverlay, null, null);
                session.UpdateCapture(bootstrap.Screenshot, bootstrap.Viewport);
                viewport = bootstrap.Viewport;
            }

            var overrideCoord = ParseCoordinateSpaceOverride(request.CoordinateSpace);
            targetScreen = CoordinateMapper.ToScreenCoordinate(viewport, request.X.Value, request.Y.Value, overrideCoord);

            if (action == "drag")
            {
                if (request.EndX is null || request.EndY is null)
                {
                    return Fail("missing_drag_end", "动作 'drag' 必须同时提供 end_x 与 end_y。");
                }

                dragEndScreen = CoordinateMapper.ToScreenCoordinate(viewport, request.EndX.Value, request.EndY.Value, overrideCoord);
            }
        }

        IReadOnlyList<string>? parsedKeys = null;
        if (action == "key_combo")
        {
            if (string.IsNullOrWhiteSpace(request.Keys))
            {
                return Fail("missing_keys", "动作 'key_combo' 必须提供 keys 参数（如 \"Ctrl+C\" 或 \"Enter\"）。");
            }

            parsedKeys = ParseKeyComboTokens(request.Keys);
            if (_securityGuard.IsDangerousKeyCombo(parsedKeys, out var keyReason))
            {
                return Fail("dangerous_key_combo_blocked", keyReason ?? "安全策略拦截高危系统快捷键。");
            }
        }
        else if (action == "type_text" && string.IsNullOrEmpty(request.Text))
        {
            return Fail("missing_text", "动作 'type_text' 必须提供 text 参数。");
        }

        // Pre-check target window safety before requesting approval.
        ComputerPointSafetyResult preSafety;
        if (targetScreen is { } pt)
        {
            preSafety = _securityGuard.EvaluatePointTarget(pt.X, pt.Y);
            if (!preSafety.Allowed && !preSafety.HitProtectedSelfUi)
            {
                return Fail(preSafety.ErrorCode!, preSafety.ErrorMessage!);
            }

            if (dragEndScreen is { } endPt)
            {
                var endSafety = _securityGuard.EvaluatePointTarget(endPt.X, endPt.Y);
                if (!endSafety.Allowed && !endSafety.HitProtectedSelfUi)
                {
                    return Fail(endSafety.ErrorCode!, endSafety.ErrorMessage!);
                }
            }
        }
        else
        {
            preSafety = _securityGuard.EvaluateForegroundTarget();
            if (!preSafety.Allowed && !preSafety.HitProtectedSelfUi)
            {
                return Fail(preSafety.ErrorCode!, preSafety.ErrorMessage!);
            }
        }

        // If host window sits over the target right now, minimize/avoid it BEFORE preview/execution OR proceed to approval if approval is needed.
        var targetWindowSnapshot = preSafety.HitProtectedSelfUi ? null : preSafety.Window;
        var (needsApproval, allowSessionScope) = EvaluateApprovalRequirement(caller.SessionId, action, targetWindowSnapshot?.ProcessName);
        if (needsApproval)
        {
            if (_controlPlane is { IsControlPlaneAvailable: false } || _approvalCoordinator is null)
            {
                return Fail("control_plane_unavailable", "当前宿主未启用控制面审批（ControlPlane），无法授权桌面交互动作。");
            }

            string? previewBase64 = null;
            if (targetScreen is { } previewPt && viewport is not null && session.LatestBgraFrame is { } latestBgra)
            {
                var imgPt = CoordinateMapper.ToImageCoordinate(viewport, previewPt.X, previewPt.Y);
                var (cropBgra, cw, ch) = ComputerVisualProcessor.CropPreviewRegion(
                    latestBgra.Bgra,
                    latestBgra.Width,
                    latestBgra.Height,
                    imgPt.X,
                    imgPt.Y);
                previewBase64 = Convert.ToBase64String(ComputerVisualProcessor.EncodePng(cropBgra, cw, ch));
            }

            var targetProc = targetWindowSnapshot?.ProcessName ?? session.PinnedProcessName ?? "desktop";
            var targetTitle = targetWindowSnapshot?.Title ?? session.PinnedWindowTitle ?? "Desktop";
            var targetHandle = targetWindowSnapshot?.WindowHandle ?? session.PinnedWindowHandle ?? string.Empty;
            var actionSummary = BuildActionSummary(action, request, targetScreen, dragEndScreen);

            var approvalOutcome = await _approvalCoordinator.RequestApprovalAsync(
                new ComputerApprovalPrompt(
                    SessionId: caller.SessionId,
                    Tool: "computer_act",
                    Action: action,
                    TargetProcessName: targetProc,
                    TargetWindowTitle: targetTitle,
                    TargetWindowHandle: targetHandle,
                    ActionSummary: actionSummary,
                    RiskReason: "桌面级键鼠操作会直接作用于宿主机当前桌面会话，请确认目标窗口与动作位置准确。",
                    TargetX: targetScreen?.X,
                    TargetY: targetScreen?.Y,
                    AllowSessionScope: allowSessionScope,
                    PreviewImageBase64: previewBase64),
                ct).ConfigureAwait(false);

            if (!approvalOutcome.Approved)
            {
                return Fail(
                    approvalOutcome.ErrorCode ?? "approval_denied",
                    approvalOutcome.ErrorMessage ?? "用户拒绝了桌面交互操作。");
            }
        }

        // Window Avoidance Readiness Barrier: after approval (and/or when host window covers target), request minimization & wait up to 600ms.
        if (_options.CurrentValue.AutoMinimizeHostWindow)
        {
            var barrierOk = await WaitForHostWindowAvoidanceBarrierAsync(
                session,
                targetScreen,
                dragEndScreen,
                ct).ConfigureAwait(false);
            if (!barrierOk)
            {
                return Fail(
                    "self_ui_interaction_forbidden",
                    "安全策略拒绝：目标坐标仍被 CodexFlow 窗口覆盖（避让超时）。请手动移开或最小化 CodexFlow 窗口后再重试。");
            }
        }
        else if (preSafety.HitProtectedSelfUi)
        {
            return Fail("self_ui_interaction_forbidden", "安全策略严禁向 CodexFlow 自身窗口注入键鼠事件。");
        }

        // Post-approval / post-avoidance TOCTOU window verification.
        var liveSafety = targetScreen is { } finalPt
            ? _securityGuard.EvaluatePointTarget(finalPt.X, finalPt.Y)
            : _securityGuard.EvaluateForegroundTarget();
        if (!liveSafety.Allowed)
        {
            return Fail(liveSafety.ErrorCode!, liveSafety.ErrorMessage!);
        }

        if (targetWindowSnapshot is not null &&
            !_securityGuard.IsWindowStillExpected(targetWindowSnapshot, liveSafety.Window, out var toctouReason))
        {
            return Fail("toctou_window_drift", toctouReason!);
        }

        // Execute the physical OS input while physical Esc x2 interrupt monitor is active.
        var sw = Stopwatch.StartNew();
        using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.EnterActionScope());
        IDisposable? interruptMonitor = null;
        var physicalAborted = false;
        string? abortReason = null;

        try
        {
            interruptMonitor = _driver.StartPhysicalInterruptMonitor(reason =>
            {
                physicalAborted = true;
                abortReason = reason;
                _ = EmergencyStopAsync(caller.SessionId, reason, CancellationToken.None);
            });

            if (session.EmergencyStopped || actionCts.Token.IsCancellationRequested)
            {
                return Fail("emergency_stopped", "操作已在执行前被用户紧急停止。");
            }

            switch (action)
            {
                case "click":
                    _driver.PerformMouseClick(targetScreen!.Value.X, targetScreen.Value.Y, request.Button ?? "left", 1);
                    break;
                case "double_click":
                    _driver.PerformMouseClick(targetScreen!.Value.X, targetScreen.Value.Y, request.Button ?? "left", 2);
                    break;
                case "right_click":
                    _driver.PerformMouseClick(targetScreen!.Value.X, targetScreen.Value.Y, "right", 1);
                    break;
                case "middle_click":
                    _driver.PerformMouseClick(targetScreen!.Value.X, targetScreen.Value.Y, "middle", 1);
                    break;
                case "move":
                    _driver.PerformMouseMove(targetScreen!.Value.X, targetScreen.Value.Y);
                    break;
                case "drag":
                    _driver.PerformMouseDrag(targetScreen!.Value.X, targetScreen.Value.Y, dragEndScreen!.Value.X, dragEndScreen.Value.Y, request.Button ?? "left");
                    break;
                case "scroll":
                    _driver.PerformMouseScroll(targetScreen!.Value.X, targetScreen.Value.Y, request.DeltaX ?? 0, request.DeltaY ?? 480);
                    break;
                case "type_text":
                    _driver.PerformTypeText(request.Text!);
                    break;
                case "key_combo":
                    _driver.PerformKeyCombo(parsedKeys!);
                    break;
            }

            if (_driver.TryGetCursorPosition(out var afterX, out var afterY))
            {
                session.RecordAgentCursor(afterX, afterY);
            }
            else if (dragEndScreen is { } endCoords)
            {
                session.RecordAgentCursor(endCoords.X, endCoords.Y);
            }
            else if (targetScreen is { } ptCoords)
            {
                session.RecordAgentCursor(ptCoords.X, ptCoords.Y);
            }

            await Task.Delay(PostActionCaptureDelayMs, actionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (physicalAborted || session.EmergencyStopped)
        {
            return Fail("emergency_stopped", $"桌面操作被紧急中断（{abortReason ?? "user_stop"}），所有输入流与修饰键已释放。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Computer Use action {Action} failed for session {SessionId}", action, caller.SessionId);
            return Fail("computer_action_failed", ex.Message);
        }
        finally
        {
            interruptMonitor?.Dispose();
            session.ExitActionScope();
        }

        sw.Stop();

        // Post-action echo screenshot with red target reticle / drag trajectory.
        try
        {
            ImagePoint? markerImg = null;
            ImagePoint? dragEndImg = null;
            if (targetScreen is { } mPt && viewport is not null)
            {
                markerImg = CoordinateMapper.ToImageCoordinate(viewport, mPt.X, mPt.Y);
                if (dragEndScreen is { } dPt)
                {
                    dragEndImg = CoordinateMapper.ToImageCoordinate(viewport, dPt.X, dPt.Y);
                }
            }

            var postCapture = CaptureAndProcessFrame(
                session,
                fullDesktop: false,
                displayIndex: null,
                includeGrid: _options.CurrentValue.EnableGridOverlay,
                markerImgPoint: markerImg,
                dragEndImgPoint: dragEndImg);

            session.UpdateCapture(postCapture.Screenshot, postCapture.Viewport);
            var summary = BuildActionSummary(action, request, targetScreen, dragEndScreen);
            session.RecordAction(
                tool: "computer_act",
                action: action,
                targetProcess: liveSafety.Window?.ProcessName ?? session.PinnedProcessName,
                targetTitle: liveSafety.Window?.Title ?? session.PinnedWindowTitle,
                x: targetScreen?.X,
                y: targetScreen?.Y,
                succeeded: true,
                summary: summary,
                durationMs: sw.ElapsedMilliseconds);

            await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
            return new ComputerOperationResult(
                Succeeded: true,
                Output: $"已执行 {summary}。\n{FormatCaptureDescription(postCapture.Viewport, session)}",
                Summary: summary,
                Screenshot: postCapture.Screenshot,
                Viewport: postCapture.Viewport);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var summary = BuildActionSummary(action, request, targetScreen, dragEndScreen);
            session.RecordAction("computer_act", action, liveSafety.Window?.ProcessName, liveSafety.Window?.Title, targetScreen?.X, targetScreen?.Y, true, summary, sw.ElapsedMilliseconds);
            await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
            return new ComputerOperationResult(
                Succeeded: true,
                Output: $"已执行 {summary}（动作后回显截屏跳过：{ex.Message}）。",
                Summary: summary);
        }
    }

    public async Task<ComputerOperationResult> RequestTakeoverAsync(
        ComputerTrustedCallerContext caller,
        string reason,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryValidateEnabledAndNotDisposed(out var disabledErr))
        {
            return disabledErr!;
        }

        if (caller.PlanModeActive)
        {
            return Fail("plan_mode_active", "只读 Plan Mode 下禁止发起人工接管请求。");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Fail("missing_reason", "发起人工接管必须提供具体的操作指引原因。");
        }

        if (!TryAcquireDesktopLease(caller.SessionId, out var busyErr))
        {
            return busyErr!;
        }

        var session = GetOrCreateSession(caller.SessionId);
        var clampedSeconds = Math.Clamp(timeoutSeconds, 30, (int)ManualTakeoverMaxDuration.TotalSeconds);
        var tcs = session.BeginTakeover(reason.Trim());
        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);

        using var pauseGuard = AmbientTurnTimeoutController.Current?.PauseTimers();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.EnterActionScope());
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedSeconds));

        try
        {
            var note = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            session.EndTakeover();

            ComputerScreenshotData? shot = null;
            ComputerCaptureViewport? vp = null;
            if (IsMultimodalCapable(out _))
            {
                try
                {
                    var capture = CaptureAndProcessFrame(session, false, null, _options.CurrentValue.EnableGridOverlay, null, null);
                    session.UpdateCapture(capture.Screenshot, capture.Viewport);
                    shot = capture.Screenshot;
                    vp = capture.Viewport;
                }
                catch (Exception)
                {
                }
            }

            await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
            var noteSuffix = string.IsNullOrWhiteSpace(note) ? string.Empty : $"（用户备注：{note}）";
            return new ComputerOperationResult(
                Succeeded: true,
                Output: $"用户已完成桌面人工接管并交回控制权{noteSuffix}。\n{(vp is not null ? FormatCaptureDescription(vp, session) : string.Empty)}".Trim(),
                Summary: "人工接管已完成",
                Screenshot: shot,
                Viewport: vp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || session.EmergencyStopped)
        {
            session.CancelTakeover("Turn cancelled or emergency stopped.");
            await PublishStateAsync(session, force: true, CancellationToken.None).ConfigureAwait(false);
            return Fail("takeover_cancelled", "人工接管随对话取消或紧急停止而终止。");
        }
        catch (OperationCanceledException)
        {
            session.CancelTakeover("人工接管等待超时。");
            await PublishStateAsync(session, force: true, CancellationToken.None).ConfigureAwait(false);
            return Fail("takeover_timeout", $"等待人工接管超时（已等待 {clampedSeconds} 秒）。");
        }
        finally
        {
            session.ExitActionScope();
        }
    }

    public async Task<ComputerOperationResult> StartTakeoverAsync(
        string sessionId,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = GetOrCreateSession(sessionId);
        session.BeginTakeover(string.IsNullOrWhiteSpace(reason) ? "用户主动开启桌面人工接管" : reason.Trim());
        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
        return new ComputerOperationResult(true, "已进入桌面人工接管模式。", "已暂停 Agent 操控等待人工操作");
    }

    public async Task<ComputerOperationResult> CompleteTakeoverAsync(
        string sessionId,
        string? note = null,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.CompleteTakeover(note))
        {
            return Fail("takeover_not_active", "当前会话没有正在进行中的桌面人工接管。");
        }

        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
        return new ComputerOperationResult(true, "人工接管已完成，控制权已交回 Agent。", "人工接管完成");
    }

    public void CancelTakeover(string sessionId, string reason)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.CancelTakeover(reason);
        }
    }

    public void ResetEmergencyStopForNewTurn(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.EmergencyStopped = false;
            session.ClearLastAgentCursor();
        }
    }

    public async Task<ComputerEmergencyStopResponse> EmergencyStopAsync(
        string? sessionId = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        var cancelledCount = 0;
        var targets = string.IsNullOrWhiteSpace(sessionId)
            ? _sessions.Values.ToArray()
            : (_sessions.TryGetValue(sessionId.Trim(), out var single) ? [single] : Array.Empty<SessionRuntimeState>());

        foreach (var s in targets)
        {
            s.TriggerEmergencyStop();
            _approvalCoordinator?.CancelSessionApprovals(s.SessionId, reason ?? "Emergency stop triggered.");
            ReleaseDesktopLease(s.SessionId);
            cancelledCount++;
            await PublishStateAsync(s, force: true, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            ReleaseDesktopLease(null);
        }

        return new ComputerEmergencyStopResponse(
            Stopped: true,
            CancelledSessions: cancelledCount,
            Message: cancelledCount > 0
                ? $"已紧急停止 {cancelledCount} 个活跃桌面操控会话并释放全部输入控制。"
                : "当前无活跃桌面操控会话，紧急熔断保护已就绪。");
    }

    public ComputerSessionStateDto GetSessionState(string sessionId)
    {
        var enabled = _options.CurrentValue.Enabled;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return new ComputerSessionStateDto(
                SessionId: sessionId ?? string.Empty,
                Status: enabled ? "idle" : "disabled",
                Active: false,
                Enabled: enabled,
                PinnedWindowHandle: null,
                PinnedProcessName: null,
                PinnedWindowTitle: null,
                ScreenshotSeq: 0,
                ImageWidth: null,
                ImageHeight: null,
                CoordinateMode: ResolveEffectiveMode().ToString(),
                LastActionSummary: null,
                RecentActions: []);
        }

        return session.ToDto(enabled, ResolveEffectiveMode(), IsLeaseHeldBy(sessionId));
    }

    public string? GetSessionGenerationId(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.GenerationId : null;

    public bool TryGetLatestScreenshot(string sessionId, out ComputerScreenshotData? screenshot)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            _sessions.TryGetValue(sessionId, out var session) &&
            session.LatestScreenshot is { } latest)
        {
            screenshot = latest;
            return true;
        }

        screenshot = null;
        return false;
    }

    public async Task CloseSessionAsync(string sessionId, string reason = "closed", CancellationToken ct = default)
    {
        _ = reason;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _approvalCoordinator?.CancelSessionApprovals(sessionId, "Computer session closed.");
        _approvalCoordinator?.ClearSessionAuthorizations(sessionId);
        ReleaseDesktopLease(sessionId);

        if (_sessions.TryRemove(sessionId, out var removed))
        {
            removed.TriggerEmergencyStop();
            await PublishStateAsync(
                removed.ToClosedDto(_options.CurrentValue.Enabled, ResolveEffectiveMode()),
                ct).ConfigureAwait(false);
            removed.Dispose();
        }
    }

    public async Task CleanupIdleSessionsAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(_options.CurrentValue.IdleTimeoutMinutes, 1, 120));
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in _sessions.Values.ToArray())
        {
            if (entry.TakeoverPending)
            {
                continue;
            }

            if (now - entry.LastActivityUtc > timeout)
            {
                await CloseSessionAsync(entry.SessionId, "idle_timeout", ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var s in _sessions.Values)
        {
            s.TriggerEmergencyStop();
            s.Dispose();
        }

        _sessions.Clear();
        ReleaseDesktopLease(null);
        return ValueTask.CompletedTask;
    }

    private (ComputerScreenshotData Screenshot, ComputerCaptureViewport Viewport, long ElapsedMs) CaptureAndProcessFrame(
        SessionRuntimeState session,
        bool fullDesktop,
        int? displayIndex,
        bool includeGrid,
        ImagePoint? markerImgPoint,
        ImagePoint? dragEndImgPoint)
    {
        var sw = Stopwatch.StartNew();
        var displays = _driver.GetDisplays();
        var (capX, capY, capW, capH) = ResolveCaptureRegion(session, displays, fullDesktop, displayIndex);

        var raw = _driver.CaptureRegion(capX, capY, capW, capH);
        var effectiveMode = ResolveEffectiveMode();
        var (imgW, imgH) = CoordinateMapper.ComputeScaledSize(raw.Width, raw.Height, _options.CurrentValue.MaxImageLongEdge);
        var bgra = ComputerVisualProcessor.ResizeBilinearBgra32(raw.BgraBytes.Span, raw.Width, raw.Height, imgW, imgH);

        var viewport = new ComputerCaptureViewport(
            CaptureOriginX: capX,
            CaptureOriginY: capY,
            CaptureWidth: capW,
            CaptureHeight: capH,
            ImageWidth: imgW,
            ImageHeight: imgH,
            VirtualLeft: raw.VirtualLeft,
            VirtualTop: raw.VirtualTop,
            VirtualWidth: raw.VirtualWidth,
            VirtualHeight: raw.VirtualHeight,
            EffectiveMode: effectiveMode);

        // Mask any protected CodexFlow windows overlapping the captured region to prevent self-approval visual feedback loops.
        var windows = _driver.GetVisibleWindows(
            _securityGuard.ProtectedPidsSnapshot,
            _securityGuard.ProtectedHandlesSnapshot);
        var protectedRects = new List<ComputerProtectedRect>();
        foreach (var win in windows)
        {
            if (win.IsMinimized || !_securityGuard.IsProtectedWindow(win))
            {
                continue;
            }

            if (TryProjectScreenRectToImage(viewport, win.X, win.Y, win.Width, win.Height, out var rx, out var ry, out var rw, out var rh))
            {
                protectedRects.Add(new ComputerProtectedRect(rx, ry, rw, rh));
            }
        }

        if (protectedRects.Count > 0)
        {
            ComputerVisualProcessor.ApplyProtectedRectMasks(bgra, imgW, imgH, protectedRects);
        }

        if (includeGrid)
        {
            ComputerVisualProcessor.DrawCoordinateGrid(bgra, imgW, imgH, effectiveMode);
        }

        if (_driver.TryGetCursorPosition(out var curX, out var curY))
        {
            if (curX >= capX && curX < capX + capW && curY >= capY && curY < capY + capH)
            {
                var curImg = CoordinateMapper.ToImageCoordinate(viewport, curX, curY);
                ComputerVisualProcessor.DrawCursorCrosshair(bgra, imgW, imgH, curImg.X, curImg.Y);
            }
        }

        if (markerImgPoint is { } mp)
        {
            ComputerVisualProcessor.DrawActionMarker(bgra, imgW, imgH, mp.X, mp.Y, dragEndImgPoint?.X, dragEndImgPoint?.Y);
        }

        session.LatestBgraFrame = (bgra, imgW, imgH);
        var encoded = _driver.EncodeScreenshot(bgra, imgW, imgH, out var mimeType);
        var seq = session.NextScreenshotSeq();
        var screenshot = new ComputerScreenshotData(seq, mimeType, encoded, imgW, imgH, DateTimeOffset.UtcNow);
        sw.Stop();
        return (screenshot, viewport, sw.ElapsedMilliseconds);
    }

    private (int X, int Y, int Width, int Height) ResolveCaptureRegion(
        SessionRuntimeState session,
        IReadOnlyList<ComputerDisplayInfoDto> displays,
        bool fullDesktop,
        int? displayIndex)
    {
        if (!fullDesktop && displayIndex is null && !string.IsNullOrWhiteSpace(session.PinnedWindowHandle))
        {
            var windows = _driver.GetVisibleWindows(
                _securityGuard.ProtectedPidsSnapshot,
                _securityGuard.ProtectedHandlesSnapshot);
            var pinned = windows.FirstOrDefault(w =>
                string.Equals(w.WindowHandle, session.PinnedWindowHandle, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null && !pinned.IsMinimized && pinned.Width >= 40 && pinned.Height >= 40)
            {
                return (pinned.X, pinned.Y, pinned.Width, pinned.Height);
            }
        }

        if (displays.Count == 0)
        {
            return (0, 0, 1280, 800);
        }

        if (fullDesktop)
        {
            var minX = displays.Min(static d => d.X);
            var minY = displays.Min(static d => d.Y);
            var maxX = displays.Max(static d => d.X + d.Width);
            var maxY = displays.Max(static d => d.Y + d.Height);
            return (minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        if (displayIndex is { } idx && idx >= 0 && idx < displays.Count)
        {
            var d = displays[idx];
            return (d.X, d.Y, d.Width, d.Height);
        }

        var primary = displays.FirstOrDefault(static d => d.IsPrimary) ?? displays[0];
        return (primary.X, primary.Y, primary.Width, primary.Height);
    }

    private static bool TryProjectScreenRectToImage(
        ComputerCaptureViewport viewport,
        int sx,
        int sy,
        int sw,
        int sh,
        out int ix,
        out int iy,
        out int iw,
        out int ih)
    {
        var capRight = viewport.CaptureOriginX + viewport.CaptureWidth;
        var capBottom = viewport.CaptureOriginY + viewport.CaptureHeight;
        var interLeft = Math.Max(viewport.CaptureOriginX, sx);
        var interTop = Math.Max(viewport.CaptureOriginY, sy);
        var interRight = Math.Min(capRight, sx + sw);
        var interBottom = Math.Min(capBottom, sy + sh);

        if (interRight <= interLeft || interBottom <= interTop)
        {
            ix = iy = iw = ih = 0;
            return false;
        }

        var tl = CoordinateMapper.ToImageCoordinate(viewport, interLeft, interTop);
        var br = CoordinateMapper.ToImageCoordinate(viewport, interRight - 1, interBottom - 1);
        ix = tl.X;
        iy = tl.Y;
        iw = Math.Max(1, br.X - tl.X + 1);
        ih = Math.Max(1, br.Y - tl.Y + 1);
        return true;
    }

    private async Task RequestHostWindowAvoidanceAsync(SessionRuntimeState session, CancellationToken ct)
    {
        session.HostWindowAvoidanceRequested = true;
        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);
        await Task.Delay(180, ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitForHostWindowAvoidanceBarrierAsync(
        SessionRuntimeState session,
        ScreenPoint? targetScreen,
        ScreenPoint? dragEndScreen,
        CancellationToken ct)
    {
        bool IsObstructedBySelf()
        {
            if (targetScreen is { } pt && _securityGuard.EvaluatePointTarget(pt.X, pt.Y).HitProtectedSelfUi)
            {
                return true;
            }

            if (dragEndScreen is { } endPt && _securityGuard.EvaluatePointTarget(endPt.X, endPt.Y).HitProtectedSelfUi)
            {
                return true;
            }

            if (targetScreen is null && _securityGuard.EvaluateForegroundTarget().HitProtectedSelfUi)
            {
                return true;
            }

            return false;
        }

        if (!IsObstructedBySelf())
        {
            return true;
        }

        session.HostWindowAvoidanceRequested = true;
        await PublishStateAsync(session, force: true, ct).ConfigureAwait(false);

        try
        {
            var elapsed = 0;
            while (elapsed < AvoidanceMaxWaitMs)
            {
                await Task.Delay(AvoidancePollIntervalMs, ct).ConfigureAwait(false);
                elapsed += AvoidancePollIntervalMs;
                if (!IsObstructedBySelf())
                {
                    return true;
                }
            }

            return !IsObstructedBySelf();
        }
        finally
        {
            session.HostWindowAvoidanceRequested = false;
            await PublishStateAsync(session, force: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private (bool NeedsApproval, bool AllowSessionScope) EvaluateApprovalRequirement(
        string sessionId,
        string action,
        string? targetProcessName)
    {
        var policy = _options.CurrentValue.InteractionApprovalPolicy;
        if (policy == ComputerInteractionApprovalPolicy.AlwaysAsk)
        {
            return (true, false);
        }

        if (policy == ComputerInteractionApprovalPolicy.AppScopedInSession)
        {
            if (!string.IsNullOrWhiteSpace(targetProcessName) &&
                _approvalCoordinator?.IsSessionProcessApproved(sessionId, targetProcessName) == true)
            {
                return (false, true);
            }

            return (action is not "move", true);
        }

        // HighRiskOnly: require approval only for text/combo/drag; allow session scoping per target app.
        if (action is "key_combo" or "drag" or "type_text")
        {
            if (!string.IsNullOrWhiteSpace(targetProcessName) &&
                _approvalCoordinator?.IsSessionProcessApproved(sessionId, targetProcessName) == true)
            {
                return (false, true);
            }

            return (true, true);
        }

        return (false, true);
    }

    private bool TryValidateEnabledAndNotDisposed(out ComputerOperationResult? error)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            error = Fail("computer_manager_disposed", "桌面 Computer Use 服务已停止。");
            return false;
        }

        if (!_options.CurrentValue.Enabled)
        {
            error = Fail("computer_use_disabled", "桌面 Computer Use 功能已在设置中被禁用（ComputerUse:Enabled=false）。");
            return false;
        }

        var status = _driver.GetStatus(_options.CurrentValue.EmergencyStopShortcut);
        if (!status.Available)
        {
            var msg = status.DiagnosticMessage ?? "当前系统桌面驱动未就绪。";
            if (!string.IsNullOrWhiteSpace(status.PermissionGuide))
            {
                msg += $" {status.PermissionGuide}";
            }

            error = Fail("computer_driver_unavailable", msg);
            return false;
        }

        error = null;
        return true;
    }

    private bool IsMultimodalCapable(out ComputerOperationResult? error)
    {
        var enabled = AmbientTurnVisualAttachmentContext.Current?.IsMultimodalEnabled
                      ?? (_modelSettings is null || _modelSettings.Read().SupportsMultimodal);
        if (!enabled)
        {
            error = Fail(
                "multimodal_required_for_computer_use",
                "当前模型未启用多模态视觉能力（SupportsMultimodal=false）。一期纯视觉 Computer Use 已被安全门禁禁用，请先在设置中切换并校验支持视觉的多模态模型。");
            return false;
        }

        error = null;
        return true;
    }

    private bool TryAcquireDesktopLease(string sessionId, out ComputerOperationResult? error)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(_options.CurrentValue.IdleTimeoutMinutes, 1, 120));
        var now = DateTimeOffset.UtcNow;

        lock (_leaseGate)
        {
            if (_leaseOwnerSessionId is null ||
                string.Equals(_leaseOwnerSessionId, sessionId, StringComparison.Ordinal) ||
                now - _leaseLastActivityUtc > timeout)
            {
                _leaseOwnerSessionId = sessionId;
                _leaseLastActivityUtc = now;
                error = null;
                return true;
            }

            error = Fail(
                "desktop_busy_by_other_session",
                $"物理桌面排他租约正由另一个会话（{_leaseOwnerSessionId}）持有，请等待其操作完成或关闭后再发起桌面自动化。");
            return false;
        }
    }

    private void ReleaseDesktopLease(string? sessionId)
    {
        lock (_leaseGate)
        {
            if (sessionId is null || string.Equals(_leaseOwnerSessionId, sessionId, StringComparison.Ordinal))
            {
                _leaseOwnerSessionId = null;
            }
        }
    }

    private bool IsLeaseHeldBy(string sessionId)
    {
        lock (_leaseGate)
        {
            return string.Equals(_leaseOwnerSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    private ComputerCoordinateMode ResolveEffectiveMode()
    {
        var modelName = _modelSettings?.Read().Model;
        return ComputerOptions.ResolveEffectiveCoordinateMode(_options.CurrentValue.CoordinateMode, modelName);
    }

    private SessionRuntimeState GetOrCreateSession(string sessionId)
        => _sessions.GetOrAdd(sessionId, static id => new SessionRuntimeState(id));

    private async Task PublishStateAsync(SessionRuntimeState session, bool force, CancellationToken ct)
    {
        if (_statePublisher is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now - session.LastPublishedUtc < StatePublishThrottle)
        {
            return;
        }

        session.LastPublishedUtc = now;
        var dto = session.ToDto(_options.CurrentValue.Enabled, ResolveEffectiveMode(), IsLeaseHeldBy(session.SessionId));
        await PublishStateAsync(dto, ct).ConfigureAwait(false);
    }

    private async Task PublishStateAsync(ComputerSessionStateDto dto, CancellationToken ct)
    {
        if (_statePublisher is null)
        {
            return;
        }

        try
        {
            await _statePublisher.PublishStateUpdatedAsync(dto.SessionId, dto, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed publishing computer state SSE event for session {SessionId}", dto.SessionId);
        }
    }

    private static ComputerWindowInfoDto? MatchPinTarget(
        IReadOnlyList<ComputerWindowInfoDto> windows,
        string? pinWindowHandle,
        string? pinProcessName)
    {
        if (!string.IsNullOrWhiteSpace(pinWindowHandle))
        {
            return windows.FirstOrDefault(w =>
                string.Equals(w.WindowHandle, pinWindowHandle.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(pinProcessName))
        {
            var norm = ComputerPermissionClamp.NormalizeSingleProcessName(pinProcessName);
            return windows.FirstOrDefault(w =>
                string.Equals(ComputerPermissionClamp.NormalizeSingleProcessName(w.ProcessName), norm, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Contains(norm, StringComparison.OrdinalIgnoreCase) ||
                w.Title.Contains(pinProcessName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static ComputerCoordinateMode? ParseCoordinateSpaceOverride(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "pixels" or "screenshot_pixels" or "pixel" => ComputerCoordinateMode.ScreenshotPixels,
            "normalized" or "normalized_1000" or "1000" => ComputerCoordinateMode.Normalized1000,
            _ => null
        };
    }

    private static string[] ParseKeyComboTokens(string raw)
        => raw.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildActionSummary(
        string action,
        ComputerActRequest req,
        ScreenPoint? pt,
        ScreenPoint? dragEnd)
        => action switch
        {
            "click" => $"左键单击 ({pt?.X},{pt?.Y})",
            "double_click" => $"左键双击 ({pt?.X},{pt?.Y})",
            "right_click" => $"右键单击 ({pt?.X},{pt?.Y})",
            "middle_click" => $"中键单击 ({pt?.X},{pt?.Y})",
            "move" => $"移动鼠标至 ({pt?.X},{pt?.Y})",
            "drag" => $"拖拽 ({pt?.X},{pt?.Y}) -> ({dragEnd?.X},{dragEnd?.Y})",
            "scroll" => $"在 ({pt?.X},{pt?.Y}) 滚轮 (dx={req.DeltaX ?? 0}, dy={req.DeltaY ?? 480})",
            "type_text" => $"输入文本（{req.Text?.Length ?? 0} 字符）",
            "key_combo" => $"发送快捷键 {req.Keys}",
            _ => action
        };

    private static string FormatCaptureDescription(ComputerCaptureViewport vp, SessionRuntimeState session)
    {
        var coordHint = vp.EffectiveMode == ComputerCoordinateMode.Normalized1000
            ? "坐标体系：[0, 1000] 千分比归一化坐标（左上角 0,0 -> 右下角 1000,1000，已与截图网格刻度对齐）"
            : $"坐标体系：截图图像像素坐标（x: 0..{vp.ImageWidth - 1}, y: 0..{vp.ImageHeight - 1}，已与截图网格刻度对齐）";
        var scopeHint = session.PinnedWindowHandle is not null
            ? $"视野范围：锁定窗口 {session.PinnedProcessName} \"{session.PinnedWindowTitle}\" ({session.PinnedWindowHandle})"
            : $"视野范围：桌面区域 ({vp.CaptureOriginX},{vp.CaptureOriginY} {vp.CaptureWidth}x{vp.CaptureHeight})";
        return $"{scopeHint}\n图像尺寸：{vp.ImageWidth}x{vp.ImageHeight}\n{coordHint}";
    }

    private static ComputerOperationResult Fail(string code, string message)
        => new(Succeeded: false, Output: $"[{code}] {message}", Summary: message, ErrorCode: code);

    private sealed class SessionRuntimeState(string sessionId) : IDisposable
    {
        private readonly object _actionGate = new();
        private readonly Queue<ComputerActionRecordDto> _recentActions = new();
        private long _screenshotSeq;
        private long _actionSeq;
        private CancellationTokenSource? _activeScopeCts;
        private TaskCompletionSource<string?>? _takeoverTcs;
        private int? _lastAgentCursorX;
        private int? _lastAgentCursorY;
        private DateTimeOffset _lastAgentCursorAtUtc;

        public string SessionId { get; } = sessionId;
        public string GenerationId { get; } = Guid.NewGuid().ToString("N")[..8];
        public string? PinnedWindowHandle { get; private set; }
        public string? PinnedProcessName { get; private set; }
        public string? PinnedWindowTitle { get; private set; }
        public ComputerScreenshotData? LatestScreenshot { get; private set; }
        public ComputerCaptureViewport? LastViewport { get; private set; }
        public (byte[] Bgra, int Width, int Height)? LatestBgraFrame { get; set; }
        public bool TakeoverPending { get; private set; }
        public string? TakeoverReason { get; private set; }
        public DateTimeOffset? TakeoverStartedAtUtc { get; private set; }
        public bool EmergencyStopped { get; set; }
        public bool HostWindowAvoidanceRequested { get; set; }
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastPublishedUtc { get; set; } = DateTimeOffset.MinValue;

        public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

        public long NextScreenshotSeq() => Interlocked.Increment(ref _screenshotSeq);

        public void SetPin(ComputerWindowInfoDto window)
        {
            PinnedWindowHandle = window.WindowHandle;
            PinnedProcessName = window.ProcessName;
            PinnedWindowTitle = window.Title;
            Touch();
        }

        public void ClearPin()
        {
            PinnedWindowHandle = null;
            PinnedProcessName = null;
            PinnedWindowTitle = null;
            Touch();
        }

        public void UpdateCapture(ComputerScreenshotData screenshot, ComputerCaptureViewport viewport)
        {
            LatestScreenshot = screenshot;
            LastViewport = viewport;
            Touch();
        }

        public void RecordAgentCursor(int x, int y)
        {
            _lastAgentCursorX = x;
            _lastAgentCursorY = y;
            _lastAgentCursorAtUtc = DateTimeOffset.UtcNow;
        }

        public void ClearLastAgentCursor()
        {
            _lastAgentCursorX = null;
            _lastAgentCursorY = null;
        }

        public bool TryDetectPhysicalMouseOverride(IComputerDriver driver, double thresholdPx, out double distance)
        {
            distance = 0;
            if (_lastAgentCursorX is not { } lx || _lastAgentCursorY is not { } ly)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - _lastAgentCursorAtUtc > TimeSpan.FromSeconds(15))
            {
                return false;
            }

            if (!driver.TryGetCursorPosition(out var cx, out var cy))
            {
                return false;
            }

            var dx = cx - lx;
            var dy = cy - ly;
            distance = Math.Sqrt(dx * dx + dy * dy);
            return distance > thresholdPx;
        }

        public CancellationToken EnterActionScope()
        {
            lock (_actionGate)
            {
                _activeScopeCts?.Dispose();
                _activeScopeCts = new CancellationTokenSource();
                return _activeScopeCts.Token;
            }
        }

        public void ExitActionScope()
        {
            lock (_actionGate)
            {
                _activeScopeCts?.Dispose();
                _activeScopeCts = null;
            }
        }

        public void TriggerEmergencyStop()
        {
            EmergencyStopped = true;
            CancellationTokenSource? cts;
            lock (_actionGate)
            {
                cts = _activeScopeCts;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            CancelTakeover("Emergency stop triggered.");
        }

        public TaskCompletionSource<string?> BeginTakeover(string reason)
        {
            lock (_actionGate)
            {
                _takeoverTcs?.TrySetCanceled();
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _takeoverTcs = tcs;
                TakeoverPending = true;
                TakeoverReason = reason;
                TakeoverStartedAtUtc = DateTimeOffset.UtcNow;
                Touch();
                return tcs;
            }
        }

        public bool CompleteTakeover(string? note)
        {
            lock (_actionGate)
            {
                if (!TakeoverPending || _takeoverTcs is null)
                {
                    return false;
                }

                var applied = _takeoverTcs.TrySetResult(note);
                EndTakeover();
                return applied;
            }
        }

        public void EndTakeover()
        {
            lock (_actionGate)
            {
                TakeoverPending = false;
                TakeoverReason = null;
                TakeoverStartedAtUtc = null;
                _takeoverTcs = null;
                Touch();
            }
        }

        public void CancelTakeover(string reason)
        {
            lock (_actionGate)
            {
                _ = reason;
                _takeoverTcs?.TrySetCanceled();
                TakeoverPending = false;
                TakeoverReason = null;
                TakeoverStartedAtUtc = null;
                _takeoverTcs = null;
            }
        }

        public void RecordAction(
            string tool,
            string action,
            string? targetProcess,
            string? targetTitle,
            int? x,
            int? y,
            bool succeeded,
            string? summary,
            long durationMs)
        {
            var seq = Interlocked.Increment(ref _actionSeq);
            var record = new ComputerActionRecordDto(
                seq,
                tool,
                action,
                targetProcess,
                targetTitle,
                x,
                y,
                succeeded,
                summary,
                durationMs,
                DateTimeOffset.UtcNow);
            lock (_actionGate)
            {
                _recentActions.Enqueue(record);
                while (_recentActions.Count > MaxRecentActions)
                {
                    _recentActions.Dequeue();
                }

                Touch();
            }
        }

        public ComputerSessionStateDto ToDto(bool enabled, ComputerCoordinateMode effectiveMode, bool hasLease)
        {
            ComputerActionRecordDto[] actions;
            lock (_actionGate)
            {
                actions = _recentActions.ToArray();
            }

            var status = !enabled
                ? "disabled"
                : EmergencyStopped
                    ? "emergency_stopped"
                    : TakeoverPending
                        ? "takeover"
                        : hasLease ? "active" : "idle";

            return new ComputerSessionStateDto(
                SessionId: SessionId,
                Status: status,
                Active: hasLease && !EmergencyStopped,
                Enabled: enabled,
                PinnedWindowHandle: PinnedWindowHandle,
                PinnedProcessName: PinnedProcessName,
                PinnedWindowTitle: PinnedWindowTitle,
                ScreenshotSeq: LatestScreenshot?.ScreenshotSeq ?? 0,
                ImageWidth: LatestScreenshot?.Width,
                ImageHeight: LatestScreenshot?.Height,
                CoordinateMode: effectiveMode.ToString(),
                LastActionSummary: actions.Length > 0 ? actions[^1].Summary : null,
                RecentActions: actions,
                TakeoverPending: TakeoverPending,
                TakeoverReason: TakeoverReason,
                TakeoverStartedAtUtc: TakeoverStartedAtUtc,
                EmergencyStopped: EmergencyStopped,
                HostWindowAvoidanceRequested: HostWindowAvoidanceRequested,
                UpdatedAtUtc: LastActivityUtc);
        }

        public ComputerSessionStateDto ToClosedDto(bool enabled, ComputerCoordinateMode effectiveMode)
            => new(
                SessionId: SessionId,
                Status: "closed",
                Active: false,
                Enabled: enabled,
                PinnedWindowHandle: null,
                PinnedProcessName: null,
                PinnedWindowTitle: null,
                ScreenshotSeq: LatestScreenshot?.ScreenshotSeq ?? 0,
                ImageWidth: LatestScreenshot?.Width,
                ImageHeight: LatestScreenshot?.Height,
                CoordinateMode: effectiveMode.ToString(),
                LastActionSummary: "桌面操控会话已关闭",
                RecentActions: [],
                TakeoverPending: false,
                TakeoverReason: null,
                TakeoverStartedAtUtc: null,
                EmergencyStopped: false,
                HostWindowAvoidanceRequested: false,
                UpdatedAtUtc: DateTimeOffset.UtcNow);

        public void Dispose()
        {
            lock (_actionGate)
            {
                _activeScopeCts?.Dispose();
                _activeScopeCts = null;
                _takeoverTcs?.TrySetCanceled();
                _takeoverTcs = null;
            }
        }
    }
}
