// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.ComputerUse.Windows;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Orchestration.ComputerUse;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.ComputerUse;

public sealed class ComputerUseTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedModelSettings(bool multimodal, string model = "gpt-5") : IModelSettingsService
    {
        public ModelSettingsView Read()
            => new("https://api.example/v1/{1}", "ChatCompletions", model, 128000, null, null, true, "1234", multimodal);

        public ModelSettingsView Update(ModelSettingsUpdate update) => Read();
    }

    private sealed class FixedControlPlane(bool available = true, string tokenDeliveryMode = "Pipe") : IControlPlaneStateProvider
    {
        public bool IsControlPlaneAvailable => available;
        public bool SelfApprovalProtection => true;
        public string TokenDeliveryMode => tokenDeliveryMode;
    }

    private sealed class TestStatePublisher(Action<ComputerSessionStateDto> publish) : IComputerStateEventPublisher
    {
        public ValueTask PublishStateUpdatedAsync(
            string sessionId, ComputerSessionStateDto state, CancellationToken ct = default)
        {
            publish(state);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubFreezeProbe(bool frozen) : IComputerSessionApprovalFreezeProbe
    {
        public bool Frozen { get; set; } = frozen;
        public bool HasAnyPendingApproval(string sessionId) => Frozen;
    }

    private class FakeComputerDriver : IComputerDriver
    {
        public List<ComputerDisplayInfoDto> Displays { get; } =
        [
            new("DISPLAY-1", true, 0, 0, 1920, 1080, 1920, 1080, 1.0)
        ];

        public List<ComputerWindowInfoDto> Windows { get; } = [];
        public Func<int, int, ComputerWindowInfoDto?>? WindowAtPointResolver { get; set; }
        public Func<ComputerWindowInfoDto?>? ForegroundWindowResolver { get; set; }
        public int CursorX { get; set; } = 100;
        public int CursorY { get; set; } = 100;
        public bool CursorPositionAvailable { get; set; } = true;
        public List<string> ExecutedActions { get; } = [];
        public Action? BeforeCapture { get; set; }
        public Action<string>? PhysicalInterrupt { get; private set; }
        public int StartedInterruptMonitors { get; private set; }
        public Action? AfterMouseMove { get; set; }
        public Action? BeforeMouseClick { get; set; }
        public int? BlockedInputProcessId { get; set; }
        public Action? DuringMouseDrag { get; set; }

        public ComputerDriverStatusDto GetStatus(string emergencyStopShortcut = "CommandOrControl+.")
            => new(true, "win-x64", "FakeComputerDriver", true, true, Displays.Count, "ok", null, emergencyStopShortcut);

        public IReadOnlyList<ComputerDisplayInfoDto> GetDisplays() => Displays;

        public IReadOnlyList<ComputerWindowInfoDto> GetVisibleWindows(
            IReadOnlySet<int> protectedPids,
            IReadOnlySet<string> protectedHandles)
            => Windows.Select(w => w with
            {
                IsProtected = w.IsProtected || protectedPids.Contains(w.ProcessId) || protectedHandles.Contains(w.WindowHandle)
            }).ToArray();

        public bool TryGetCursorPosition(out int x, out int y)
        {
            x = CursorX;
            y = CursorY;
            return CursorPositionAvailable;
        }

        public bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName)
        {
            parentPid = 0;
            parentProcessName = null;
            return false;
        }

        public ComputerWindowInfoDto? GetWindowAtPoint(
            int x,
            int y,
            IReadOnlySet<int> protectedPids,
            IReadOnlySet<string> protectedHandles)
        {
            var resolved = WindowAtPointResolver?.Invoke(x, y)
                           ?? Windows.FirstOrDefault(w => !w.IsMinimized && x >= w.X && x < w.X + w.Width && y >= w.Y && y < w.Y + w.Height);
            if (resolved is null)
            {
                return null;
            }

            return resolved with
            {
                IsProtected = resolved.IsProtected || protectedPids.Contains(resolved.ProcessId) || protectedHandles.Contains(resolved.WindowHandle)
            };
        }

        public ComputerWindowInfoDto? GetForegroundWindow(
            IReadOnlySet<int> protectedPids,
            IReadOnlySet<string> protectedHandles)
        {
            var resolved = ForegroundWindowResolver?.Invoke() ?? Windows.FirstOrDefault(static w => w.IsForeground);
            if (resolved is null)
            {
                return null;
            }

            return resolved with
            {
                IsProtected = resolved.IsProtected || protectedPids.Contains(resolved.ProcessId) || protectedHandles.Contains(resolved.WindowHandle)
            };
        }

        public bool TryBringWindowToForeground(string windowHandle) => true;

        public bool IsProcessInTree(int candidatePid, IReadOnlySet<int> rootProtectedPids)
            => rootProtectedPids.Contains(candidatePid);

        public RawCapturedFrame CaptureRegion(int x, int y, int width, int height)
        {
            BeforeCapture?.Invoke();
            var bgra = new byte[width * height * 4];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 200;
                bgra[i + 1] = 210;
                bgra[i + 2] = 220;
                bgra[i + 3] = 255;
            }

            return new RawCapturedFrame(bgra, width, height, x, y, -1920, 0, 3840, 1080);
        }

        public byte[] EncodeScreenshot(ReadOnlySpan<byte> bgra, int width, int height, out string mimeType)
        {
            mimeType = "image/png";
            return ComputerVisualProcessor.EncodePng(bgra, width, height);
        }

        public bool IsInputBlockedByIntegrity(int targetProcessId) => targetProcessId == BlockedInputProcessId;

        public void PerformMouseClick(int x, int y, string button, int clickCount)
        {
            BeforeMouseClick?.Invoke();
            ExecutedActions.Add($"click:{button}:{clickCount}@{x},{y}");
        }

        public void PerformMouseMove(int x, int y)
        {
            CursorX = x;
            CursorY = y;
            ExecutedActions.Add($"move@{x},{y}");
            AfterMouseMove?.Invoke();
        }

        public void PerformMouseDrag(int startX, int startY, int endX, int endY, string button, CancellationToken ct)
        {
            DuringMouseDrag?.Invoke();
            ct.ThrowIfCancellationRequested();
            CursorX = endX;
            CursorY = endY;
            ExecutedActions.Add($"drag@{startX},{startY}->{endX},{endY}");
        }

        public void PerformMouseScroll(int x, int y, int deltaX, int deltaY)
            => ExecutedActions.Add($"scroll@{x},{y}:{deltaX},{deltaY}");

        public void PerformTypeText(string text)
            => ExecutedActions.Add($"type:{text}");

        public void PerformKeyCombo(IReadOnlyList<string> keys)
            => ExecutedActions.Add($"combo:{string.Join('+', keys)}");

        public IDisposable StartPhysicalInterruptMonitor(Action<string> onPhysicalInterrupt)
        {
            StartedInterruptMonitors++;
            PhysicalInterrupt = onPhysicalInterrupt;
            return new InterruptMonitor(this);
        }

        private sealed class InterruptMonitor(FakeComputerDriver driver) : IDisposable
        {
            public void Dispose() => driver.PhysicalInterrupt = null;
        }
    }

    [Fact]
    public void CoordinateMapper_HandlesNegativeMultiMonitorNormalizedAndWin32Absolute()
    {
        var viewport = new ComputerCaptureViewport(
            CaptureOriginX: -1920,
            CaptureOriginY: -200,
            CaptureWidth: 1920,
            CaptureHeight: 1080,
            ImageWidth: 960,
            ImageHeight: 540,
            VirtualLeft: -1920,
            VirtualTop: -200,
            VirtualWidth: 3840,
            VirtualHeight: 1280,
            EffectiveMode: ComputerCoordinateMode.ScreenshotPixels);

        var pxPt = CoordinateMapper.ToScreenCoordinate(viewport, 480, 270);
        Assert.InRange(pxPt.X, -962, -958);
        Assert.InRange(pxPt.Y, 338, 342);

        var normPt = CoordinateMapper.ToScreenCoordinate(viewport, 500, 500, ComputerCoordinateMode.Normalized1000);
        Assert.InRange(normPt.X, -962, -958);
        Assert.InRange(normPt.Y, 338, 342);

        var absLeftTop = CoordinateMapper.ToWin32VirtualDesktopAbsolute(-1920, -200, -1920, -200, 3840, 1280);
        Assert.Equal(8, absLeftTop.NormalizedX);
        Assert.Equal(25, absLeftTop.NormalizedY);

        var absRightBottom = CoordinateMapper.ToWin32VirtualDesktopAbsolute(1919, 1079, -1920, -200, 3840, 1280);
        Assert.InRange(absRightBottom.NormalizedX, 65500, 65535);
        Assert.InRange(absRightBottom.NormalizedY, 65450, 65535);
    }

    [Fact]
    public void VisualProcessor_DrawsOverlaysAndEncodesPngAndBmp()
    {
        const int w = 320;
        const int h = 240;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 240;
            bgra[i + 1] = 240;
            bgra[i + 2] = 240;
            bgra[i + 3] = 255;
        }

        ComputerVisualProcessor.DrawCoordinateGrid(bgra, w, h, ComputerCoordinateMode.Normalized1000);
        ComputerVisualProcessor.DrawCursorCrosshair(bgra, w, h, 200, 120);
        ComputerVisualProcessor.DrawActionMarker(bgra, w, h, 220, 140, 260, 180);

        var png = ComputerVisualProcessor.EncodePng(bgra, w, h);
        Assert.True(png.Length > 32);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png[..8]);

        // Verify 24bpp bottom-up BMP decoding roundtrip.
        var bmp = BuildSynthetic24BppBmp(4, 2, b: 10, g: 120, r: 230);
        var decoded = ComputerVisualProcessor.DecodeBmp24Or32ToBgra32(bmp, out var bw, out var bh);
        Assert.Equal(4, bw);
        Assert.Equal(2, bh);
        Assert.Equal(10, decoded[0]);
        Assert.Equal(120, decoded[1]);
        Assert.Equal(230, decoded[2]);
        Assert.Equal(255, decoded[3]);
    }

    [Fact]
    public async Task Screenshot_PreservesPixelsInProtectedWindowBoundsAndBlocksInputToHost()
    {
        const int width = 800;
        const int height = 600;
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            AutoMinimizeHostWindow = false,
            EnableGridOverlay = false
        });
        var driver = new FakeComputerDriver { CursorX = -1, CursorY = -1 };
        driver.Displays.Clear();
        driver.Displays.Add(new ComputerDisplayInfoDto("DISPLAY-1", true, 0, 0, width, height, width, height, 1.0));
        var host = new ComputerWindowInfoDto("0x100", 4242, "electron", "CodexFlow", 0, 0, width, 560, false, true, false);
        driver.Windows.Add(host);
        var guard = new ComputerSecurityGuard(options, driver);
        guard.RegisterProtectedWindows(host.ProcessId, [host.WindowHandle]);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance);
        var caller = new ComputerTrustedCallerContext("sess-visible-host", SessionWorkMode.Daily, PlanModeActive: false);

        var raw = driver.CaptureRegion(0, 0, width, height);
        var expected = ComputerVisualProcessor.EncodePng(raw.BgraBytes.Span, width, height);
        var observed = await manager.CaptureScreenshotAsync(caller);

        Assert.True(observed.Succeeded, observed.Output);
        Assert.Equal(expected, Assert.IsType<ComputerScreenshotData>(observed.Screenshot).Bytes.ToArray());

        var click = await manager.ActAsync(caller, new ComputerActRequest("click", X: 200, Y: 200, CoordinateSpace: "pixels"));
        Assert.Equal("self_ui_interaction_forbidden", click.ErrorCode);
        Assert.Empty(driver.ExecutedActions);
    }

    [Fact]
    public void SecurityGuard_BlocksSelfUiSensitiveProcessesAndDangerousCombos()
    {
        var opts = new ComputerOptions();
        var driver = new FakeComputerDriver();
        driver.Windows.Add(new ComputerWindowInfoDto("0x100", 4242, "CodexFlow.Desktop", "CodexFlow Desktop", 0, 0, 800, 600, false, true, false));
        driver.Windows.Add(new ComputerWindowInfoDto("0x200", 5151, "1Password.exe", "1Password", 900, 0, 500, 500, false, false, false));

        var guard = new ComputerSecurityGuard(new StaticOptionsMonitor<ComputerOptions>(opts), driver);
        guard.RegisterProtectedWindows(4242, ["0x100"]);

        var selfClick = guard.EvaluatePointTarget(400, 300);
        Assert.False(selfClick.Allowed);
        Assert.True(selfClick.HitProtectedSelfUi);
        Assert.Equal("self_ui_interaction_forbidden", selfClick.ErrorCode);

        // Even 2px outside edge (margin +-4px) must block self-UI.
        var marginSelfClick = guard.EvaluatePointTarget(802, 300);
        Assert.False(marginSelfClick.Allowed);
        Assert.Equal("self_ui_interaction_forbidden", marginSelfClick.ErrorCode);

        var blockedClick = guard.EvaluatePointTarget(1100, 200);
        Assert.False(blockedClick.Allowed);
        Assert.True(blockedClick.HitBlockedProcess);
        Assert.Equal("blocked_sensitive_application", blockedClick.ErrorCode);

        Assert.True(guard.IsDangerousKeyCombo(["Ctrl", "Alt", "Delete"], out _));
        Assert.True(guard.IsDangerousKeyCombo(["Alt", "Tab"], out _));
        Assert.False(guard.IsDangerousKeyCombo(["Ctrl", "C"], out _));
    }

    [Fact]
    public async Task SessionManager_EnforcesHostAvoidanceBarrierPendingFreezeToctouAndLease()
    {
        var opts = new ComputerOptions
        {
            InteractionApprovalPolicy = ComputerInteractionApprovalPolicy.HighRiskOnly,
            AutoMinimizeHostWindow = true,
            CoordinateMode = ComputerCoordinateMode.ScreenshotPixels,
            MaxImageLongEdge = 1920
        };
        var driver = new FakeComputerDriver();
        var notepad = new ComputerWindowInfoDto("0x300", 7777, "notepad", "Untitled - Notepad", 0, 0, 1200, 900, false, true, false);
        var hostWin = new ComputerWindowInfoDto("0x100", 4242, "electron", "CodexFlow Desktop", 100, 100, 600, 400, false, false, true);
        driver.Windows.Add(notepad);
        driver.Windows.Add(hostWin);

        // Simulate host window covering (250, 250) on first 2 probes (DWM minimize animation), then clearing out of the way.
        var probeCalls = 0;
        driver.WindowAtPointResolver = (x, y) =>
        {
            if (x is >= 240 and <= 260 && y is >= 240 and <= 260)
            {
                probeCalls++;
                // EvaluatePointTarget checks 5 margin points per pass; clear on 2nd pass (>5 calls).
                return probeCalls <= 5 ? hostWin : notepad;
            }

            return notepad;
        };

        var freezeProbe = new StubFreezeProbe(frozen: true);
        var guard = new ComputerSecurityGuard(new StaticOptionsMonitor<ComputerOptions>(opts), driver, freezeProbe);
        guard.RegisterProtectedWindows(4242, ["0x100"]);

        var manager = new ComputerSessionManager(
            new StaticOptionsMonitor<ComputerOptions>(opts),
            driver,
            guard,
            NullLogger<ComputerSessionManager>.Instance,
            new FixedModelSettings(multimodal: true));

        var callerA = new ComputerTrustedCallerContext("sess-a", SessionWorkMode.Development, PlanModeActive: false);
        var callerB = new ComputerTrustedCallerContext("sess-b", SessionWorkMode.Daily, PlanModeActive: false);

        // 1. Frozen while pending approval exists.
        var frozen = await manager.ActAsync(callerA, new ComputerActRequest("click", X: 250, Y: 250));
        Assert.False(frozen.Succeeded);
        Assert.Equal("pending_approval_freeze", frozen.ErrorCode);

        freezeProbe.Frozen = false;

        // 2. Host avoidance barrier waits for window to move away, then executes click.
        var clicked = await manager.ActAsync(callerA, new ComputerActRequest("click", X: 250, Y: 250));
        Assert.True(clicked.Succeeded);
        Assert.NotNull(clicked.Screenshot);
        Assert.Contains(driver.ExecutedActions, static a => a.StartsWith("click:left:1@250,250", StringComparison.Ordinal));

        // 3. Exclusive desktop lease prevents concurrent session B from hijacking the desktop.
        var busy = await manager.CaptureScreenshotAsync(callerB);
        Assert.False(busy.Succeeded);
        Assert.Equal("desktop_busy_by_other_session", busy.ErrorCode);

        // 4. Physical mouse override > 120px aborts and yields control.
        driver.CursorX = 900;
        driver.CursorY = 700;
        var overrideRes = await manager.ActAsync(callerA, new ComputerActRequest("click", X: 250, Y: 250));
        Assert.False(overrideRes.Succeeded);
        Assert.Equal("physical_mouse_override_detected", overrideRes.ErrorCode);

        // 5. Closing session A releases lease so session B can proceed.
        await manager.CloseSessionAsync("sess-a");
        var shotB = await manager.CaptureScreenshotAsync(callerB);
        Assert.True(shotB.Succeeded);
    }

    [Fact]
    public async Task ApprovalCoordinator_EnforcesAlwaysAskAndDetectsToctouWindowDrift()
    {
        var opts = new ComputerOptions
        {
            InteractionApprovalPolicy = ComputerInteractionApprovalPolicy.AlwaysAsk,
            AutoMinimizeHostWindow = false,
            CoordinateMode = ComputerCoordinateMode.ScreenshotPixels,
            MaxImageLongEdge = 1920
        };
        var driver = new FakeComputerDriver();
        var notepad = new ComputerWindowInfoDto("0x300", 7777, "notepad", "Notepad", 0, 0, 1200, 900, false, true, false);
        var calc = new ComputerWindowInfoDto("0x400", 8888, "calc", "Calculator", 0, 0, 1200, 900, false, true, false);
        ComputerWindowInfoDto activeTarget = notepad;
        driver.WindowAtPointResolver = (_, _) => activeTarget;

        var coordinator = new ComputerApprovalCoordinator(
            NullLogger<ComputerApprovalCoordinator>.Instance,
            gateway: null,
            controlPlane: new FixedControlPlane(available: true));
        var guard = new ComputerSecurityGuard(new StaticOptionsMonitor<ComputerOptions>(opts), driver);
        var manager = new ComputerSessionManager(
            new StaticOptionsMonitor<ComputerOptions>(opts),
            driver,
            guard,
            NullLogger<ComputerSessionManager>.Instance,
            new FixedModelSettings(multimodal: true),
            approvalCoordinator: coordinator,
            controlPlane: new FixedControlPlane(available: true));

        var caller = new ComputerTrustedCallerContext("sess-toctou", SessionWorkMode.Daily, PlanModeActive: false);
        var actTask = manager.ActAsync(caller, new ComputerActRequest("click", X: 300, Y: 300));

        var pending = Assert.Single(coordinator.GetPendingApprovalsForSession(caller.SessionId));
        Assert.Equal(["allow_once", "deny"], pending.AllowedDecisions);

        // AlwaysAsk forbids allow_session.
        var rejectSessionScope = await coordinator.TrySubmitDecisionAsync(
            pending.ApprovalId,
            pending.RequestDigest,
            ExecutionApprovalDecisionKind.AllowSession,
            "user1",
            null);
        Assert.Equal(ApprovalSubmissionOutcome.DecisionNotAllowed, rejectSessionScope!.Outcome);

        // Simulate window swap (TOCTOU) right before user approves.
        activeTarget = calc;
        var approved = await coordinator.TrySubmitDecisionAsync(
            pending.ApprovalId,
            pending.RequestDigest,
            ExecutionApprovalDecisionKind.AllowOnce,
            "user1",
            null);
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, approved!.Outcome);

        var actResult = await actTask;
        Assert.False(actResult.Succeeded);
        Assert.Equal("toctou_window_drift", actResult.ErrorCode);
        Assert.Empty(driver.ExecutedActions);
    }

    [Theory]
    [InlineData("Pipe", true, ExecutionPermissionMode.FullAuto, null)]
    [InlineData("File", true, ExecutionPermissionMode.FullAuto, "control_plane_unavailable")]
    [InlineData("Pipe", false, ExecutionPermissionMode.FullAuto, "control_plane_unavailable")]
    [InlineData("Pipe", true, ExecutionPermissionMode.Deny, "execution_permission_denied")]
    public async Task Act_UsesEffectiveExecutionModeForManagedDesktop(
        string delivery, bool available, ExecutionPermissionMode mode, string? expectedError)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            CoordinateMode = ComputerCoordinateMode.ScreenshotPixels,
            MaxImageLongEdge = 1920
        });
        var driver = new FakeComputerDriver();
        var target = new ComputerWindowInfoDto("0x300", 7777, "notepad", "Notes", 0, 0, 1200, 900, false, true, false);
        driver.Windows.Add(target);
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance,
            controlPlane: new FixedControlPlane(available, delivery),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = mode }));

        var caller = new ComputerTrustedCallerContext("sess-policy", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller, new ComputerActRequest("click", X: 72, Y: 61));
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal(expectedError is null, result.Succeeded);
        if (expectedError is null)
        {
            Assert.Equal(["move@72,61", "click:left:1@72,61"], driver.ExecutedActions);
            driver.WindowAtPointResolver = (_, _) => target with { ProcessName = "keepass" };
            var blocked = await manager.ActAsync(caller, new ComputerActRequest("click", X: 72, Y: 61));
            Assert.Equal("blocked_sensitive_application", blocked.ErrorCode);
            Assert.Equal(2, driver.ExecutedActions.Count);
        }
        else
        {
            Assert.Empty(driver.ExecutedActions);
        }
    }

    [Theory]
    [InlineData(250, true, null)]
    [InlineData(100, true, "cursor_position_mismatch")]
    [InlineData(250, false, "cursor_position_unavailable")]
    public async Task Act_ReportsMoveSuccessOnlyWhenCursorReachesTarget(
        int actualX, bool cursorPositionAvailable, string? expectedError)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new FakeComputerDriver();
        driver.AfterMouseMove = () =>
        {
            driver.CursorX = actualX;
            driver.CursorPositionAvailable = cursorPositionAvailable;
        };
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));

        var caller = new ComputerTrustedCallerContext("sess-move-verification", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller,
            new ComputerActRequest("move", X: 250, Y: 250, CoordinateSpace: "pixels"));

        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal(expectedError is null, result.Succeeded);
        Assert.Equal("move@250,250", Assert.Single(driver.ExecutedActions));
        if (expectedError is not null)
        {
            Assert.Contains("目标 (250, 250)", result.Output);
            Assert.Contains("computer_request_takeover", result.Output);
            Assert.Null(result.Screenshot);
        }
        if (expectedError == "cursor_position_mismatch")
        {
            Assert.Contains("实际位置 (100, 250)", result.Output);
        }
    }

    [Fact]
    public async Task Act_WaitsAfterMovingBeforeClicking()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new ReceiptComputerDriver();
        var movedAt = 0L;
        var settleTime = TimeSpan.Zero;
        driver.AfterMouseMove = () => movedAt = Stopwatch.GetTimestamp();
        driver.BeforeMouseClick = () => settleTime = Stopwatch.GetElapsedTime(movedAt);
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));

        var caller = new ComputerTrustedCallerContext("sess-click-pacing", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller,
            new ComputerActRequest("right_click", X: 250, Y: 250, CoordinateSpace: "pixels"));

        Assert.True(result.Succeeded, result.Output);
        Assert.Contains("已提交 2/2 个鼠标事件", result.Summary);
        Assert.Contains("确认目标窗口是否响应", result.Output);
        Assert.Equal(["move@250,250", "click:right:1@250,250"], driver.ExecutedActions);
        Assert.True(settleTime.TotalMilliseconds >= 45, $"Only waited {settleTime.TotalMilliseconds}ms.");
    }

    private sealed class ReceiptComputerDriver : FakeComputerDriver, IComputerDriver
    {
        public int? AcceptedClickEvents { get; init; }

        public ComputerClickReceipt? PerformMouseClickWithReceipt(int x, int y, string button, int clickCount)
        {
            PerformMouseClick(x, y, button, clickCount);
            return new ComputerClickReceipt(clickCount * 2, AcceptedClickEvents ?? clickCount * 2);
        }
    }

    [Fact]
    public void ClickReceipt_DefaultImplementationCallsLegacyVoidMethod()
    {
        var driver = new FakeComputerDriver();
        IComputerDriver platform = driver;
        Assert.Null(platform.PerformMouseClickWithReceipt(250, 250, "left", 1));
        Assert.Equal("click:left:1@250,250", Assert.Single(driver.ExecutedActions));
    }

    [Fact]
    public async Task Act_RejectsIncompleteReceiptFromCustomDriver()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new ReceiptComputerDriver { AcceptedClickEvents = 1 };
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));
        var caller = new ComputerTrustedCallerContext("custom-click-receipt", SessionWorkMode.Daily, PlanModeActive: false);

        var result = await manager.ActAsync(caller, new ComputerActRequest("click", X: 250, Y: 250, CoordinateSpace: "pixels"));

        Assert.False(result.Succeeded);
        Assert.Equal("computer_action_failed", result.ErrorCode);
        Assert.Contains("1/2", result.Output);
    }

    [Theory]
    [InlineData("cursor_position_mismatch")]
    [InlineData("toctou_window_drift")]
    [InlineData("execution_permission_changed")]
    public async Task Act_AbortsClickWhenStateChangesAfterMoving(string expectedError)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new FakeComputerDriver();
        var target = new ComputerWindowInfoDto("0x310", 7777, "notepad", "Notes", 0, 0, 1200, 900, false, true, false);
        driver.Windows.Add(target);
        var execution = new ExecutionOptions { PermissionMode = ExecutionPermissionMode.FullAuto };
        driver.AfterMouseMove = () =>
        {
            switch (expectedError)
            {
                case "cursor_position_mismatch":
                    driver.CursorX = 100;
                    break;
                case "toctou_window_drift":
                    driver.WindowAtPointResolver = (_, _) => target with { WindowHandle = "0x320" };
                    break;
                case "execution_permission_changed":
                    execution.PermissionMode = ExecutionPermissionMode.Ask;
                    break;
            }
        };
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(execution));

        var caller = new ComputerTrustedCallerContext("sess-click-revalidate", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller,
            new ComputerActRequest("click", X: 250, Y: 250, CoordinateSpace: "pixels"));

        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal("move@250,250", Assert.Single(driver.ExecutedActions));
    }

    [Theory]
    [InlineData("move", false)]
    [InlineData("move", true)]
    [InlineData("click", true)]
    public async Task Act_BlocksPointerInputBeforeMovingForElevatedWindow(string action, bool elevatedTarget)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new FakeComputerDriver { BlockedInputProcessId = elevatedTarget ? 7777 : null };
        driver.Windows.Add(new("0x310", 7777, "notepad", "Notes", 0, 0, 1200, 900, false, true, false));
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));

        var caller = new ComputerTrustedCallerContext("sess-elevated-window-mouse", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller,
            new ComputerActRequest(action, X: 250, Y: 250, CoordinateSpace: "pixels"));

        if (elevatedTarget)
        {
            Assert.False(result.Succeeded);
            Assert.Equal("window_input_integrity_blocked", result.ErrorCode);
            Assert.Contains("Notes", result.Output);
            Assert.Contains("computer_request_takeover", result.Output);
            Assert.Empty(driver.ExecutedActions);
        }
        else
        {
            Assert.True(result.Succeeded, result.Output);
            Assert.Equal("move@250,250", Assert.Single(driver.ExecutedActions));
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task Act_ReportsIncompleteWindowsClickAsFailure(uint sent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new StaticOptionsMonitor<ComputerOptions>(new());
        var submitClickBatch = Win32ComputerNative.SubmitClickBatch;
        var driver = new FakeComputerDriver
        {
            BeforeMouseClick = () => submitClickBatch(2, () => (sent, 0), () => true)
        };
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));

        var caller = new ComputerTrustedCallerContext("sess-click-partial-input", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller,
            new ComputerActRequest("click", X: 250, Y: 250, CoordinateSpace: "pixels"));

        Assert.False(result.Succeeded);
        Assert.Equal("computer_action_failed", result.ErrorCode);
        Assert.Contains($"{sent}/2", result.Output);
        Assert.Null(result.Screenshot);
    }

    [Theory]
    [InlineData(2u, 0u, false)]
    [InlineData(2u, 1u, true)]
    [InlineData(2u, 1u, false)]
    [InlineData(2u, 2u, false)]
    [InlineData(4u, 2u, false)]
    [InlineData(4u, 3u, true)]
    public void WindowsClickBatch_ReleasesOnlyUnmatchedPress(uint requested, uint accepted, bool releaseSucceeds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var submitClickBatch = Win32ComputerNative.SubmitClickBatch;
        var submitted = 0;
        var released = 0;
        var receipt = default(ComputerClickReceipt);
        var failure = Record.Exception(() =>
        {
            receipt = submitClickBatch(requested, () =>
            {
                submitted++;
                return (accepted, 5);
            }, () =>
            {
                released++;
                return releaseSucceeds;
            });
        });

        Assert.Equal(1, submitted);
        Assert.Equal(accepted % 2 == 1 ? 1 : 0, released);
        if (requested == accepted)
        {
            Assert.Null(failure);
            Assert.Equal(new ComputerClickReceipt((int)requested, (int)accepted), receipt);
        }
        else
        {
            var error = Assert.IsType<Win32Exception>(failure);
            Assert.Equal(5, error.NativeErrorCode);
            Assert.Contains($"{accepted}/{requested}", error.Message);
            if (released > 0)
            {
                Assert.Contains(releaseSucceeds ? "已补发鼠标松开" : "补发鼠标松开失败", error.Message);
            }
        }
    }

    [Theory]
    [InlineData("before-approval", "window_input_integrity_blocked")]
    [InlineData("during-approval", "window_input_integrity_blocked")]
    [InlineData("changed-view", "fresh_screenshot_required")]
    public async Task Act_ChecksPointerIntegrityBeforeApprovalAndRevalidatesBeforeInput(string phase, string expectedError)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            MaxImageLongEdge = 1920,
            InteractionApprovalPolicy = ComputerInteractionApprovalPolicy.AlwaysAsk
        });
        var driver = new FakeComputerDriver { BlockedInputProcessId = phase == "before-approval" ? 7777 : null };
        driver.Windows.Add(new("0x310", 7777, "notepad", "Notes", 0, 0, 1200, 900, false, true, false));
        var approval = new Mock<IComputerApprovalCoordinator>();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, approvalCoordinator: approval.Object, controlPlane: new FixedControlPlane());
        var caller = new ComputerTrustedCallerContext("approval-revalidation", SessionWorkMode.Daily, PlanModeActive: false);
        approval.Setup(x => x.RequestApprovalAsync(It.IsAny<ComputerApprovalPrompt>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (phase == "changed-view")
                {
                    Assert.True((await manager.CaptureScreenshotAsync(caller)).Succeeded);
                }
                else
                {
                    driver.BlockedInputProcessId = 7777;
                }

                return new ComputerApprovalOutcome(true, false, false, false);
            });
        var screenshot = await manager.CaptureScreenshotAsync(caller);
        Assert.True(screenshot.Succeeded);

        var result = await manager.ActAsync(caller, new ComputerActRequest("click", X: 250, Y: 250, CoordinateSpace: "pixels")
        {
            RequireModelScreenshot = true,
            ModelScreenshot = screenshot.Screenshot
        });

        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Empty(driver.ExecutedActions);
        approval.Verify(x => x.RequestApprovalAsync(It.IsAny<ComputerApprovalPrompt>(), It.IsAny<CancellationToken>()),
            phase == "before-approval" ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task Act_CancelsButtonEventsWhenViewChangesAfterMouseMove()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { MaxImageLongEdge = 1920 });
        var driver = new FakeComputerDriver();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));
        var caller = new ComputerTrustedCallerContext("move-view-change", SessionWorkMode.Daily, PlanModeActive: false);
        var screenshot = await manager.CaptureScreenshotAsync(caller);
        Assert.True(screenshot.Succeeded);
        Task<ComputerOperationResult>? pinChange = null;
        driver.AfterMouseMove = () => pinChange = manager.GetScreenInfoAsync(caller, unpin: true);

        var result = await manager.ActAsync(caller, new ComputerActRequest("click", X: 250, Y: 250, CoordinateSpace: "pixels")
        {
            RequireModelScreenshot = true,
            ModelScreenshot = screenshot.Screenshot
        });

        Assert.NotNull(pinChange);
        Assert.True((await pinChange).Succeeded);
        Assert.Equal("fresh_screenshot_required", result.ErrorCode);
        Assert.Equal("move@250,250", Assert.Single(driver.ExecutedActions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputerTools_BlockCoordinatesWhenActionEchoWasNotAttached(bool captureFails)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            MaxImageLongEdge = 1920,
            MaxScreenshotsPerTurn = captureFails ? 12 : 1
        });
        var driver = new FakeComputerDriver();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));
        var caller = new ComputerTrustedCallerContext("tool-echo", SessionWorkMode.Daily, PlanModeActive: false);
        using var callerScope = AmbientComputerCallerContext.BeginScope(caller);
        using var visualScope = AmbientTurnVisualAttachmentContext.BeginScope(new TurnVisualAttachmentStore(true));
        var screenshotTool = new ComputerScreenshotTool(manager, computerOptions: options);
        var actTool = new ComputerActTool(manager, computerOptions: options);
        Dictionary<string, object?> click = new() { ["action"] = "click", ["x"] = 250d, ["y"] = 250d, ["coordinate_space"] = "pixels" };

        var noScreenshot = await actTool.ExecuteAsync(click);
        Assert.Equal("fresh_screenshot_required", Assert.IsType<System.Text.Json.JsonElement>(noScreenshot.Metadata).GetProperty("errorCode").GetString());
        var screenshot = await screenshotTool.ExecuteAsync(new());
        Assert.True(Assert.IsType<System.Text.Json.JsonElement>(screenshot.Metadata).GetProperty("screenshotAttached").GetBoolean());
        if (captureFails)
        {
            driver.BeforeCapture = () => throw new InvalidOperationException("echo unavailable");
        }

        var first = await actTool.ExecuteAsync(click);
        Assert.Equal(ToolResultStatus.Success, first.Status);
        Assert.False(Assert.IsType<System.Text.Json.JsonElement>(first.Metadata).GetProperty("screenshotAttached").GetBoolean());
        Assert.Contains("未向模型附加新截图", first.Output);
        var stale = await actTool.ExecuteAsync(click);
        Assert.Equal("fresh_screenshot_required", Assert.IsType<System.Text.Json.JsonElement>(stale.Metadata).GetProperty("errorCode").GetString());
        Assert.Equal(["move@250,250", "click:left:1@250,250"], driver.ExecutedActions);

        driver.BeforeCapture = null;
        options.CurrentValue.MaxScreenshotsPerTurn = 12;
        Assert.True(Assert.IsType<System.Text.Json.JsonElement>((await screenshotTool.ExecuteAsync(new())).Metadata).GetProperty("screenshotAttached").GetBoolean());
        Assert.True(Assert.IsType<System.Text.Json.JsonElement>((await actTool.ExecuteAsync(click)).Metadata).GetProperty("screenshotAttached").GetBoolean());
        Assert.Equal(4, driver.ExecutedActions.Count);
    }

    [Fact]
    public void WindowsInput_AcceptsCompleteBatchAndReadsProcessIntegrity()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32ComputerNative.ValidateInputCount(sent: 2, requested: 2, error: 0);
            Assert.NotNull(Win32ComputerNative.ReadProcessIntegrityLevel(Environment.ProcessId));
        }
    }

    [Fact]
    public void PhysicalEscDoubleTap_IgnoresInjectedEventsAndKeyRepeat()
    {
        var tracker = new PhysicalEscDoubleTapTracker();
        Assert.False(tracker.Observe(pressed: true, injected: true, timestampMs: 100));
        Assert.False(tracker.Observe(pressed: true, injected: false, timestampMs: 110));
        Assert.False(tracker.Observe(pressed: false, injected: true, timestampMs: 150));
        Assert.False(tracker.Observe(pressed: true, injected: false, timestampMs: 160));
        Assert.False(tracker.Observe(pressed: false, injected: false, timestampMs: 200));
        Assert.False(tracker.Observe(pressed: true, injected: false, timestampMs: 600));
        Assert.False(tracker.Observe(pressed: false, injected: false, timestampMs: 650));
        Assert.True(tracker.Observe(pressed: true, injected: false, timestampMs: 800));
    }

    [Fact]
    public async Task DesktopLease_HandlesPhysicalStopBetweenActionsAndIgnoresOldCallback()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new());
        var driver = new FakeComputerDriver();
        var published = new List<ComputerSessionStateDto>();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, statePublisher: new TestStatePublisher(published.Add));
        var caller = new ComputerTrustedCallerContext("esc-between-actions", SessionWorkMode.Daily, PlanModeActive: false);
        var other = caller with { SessionId = "next-lease-owner" };

        Assert.True((await manager.CaptureScreenshotAsync(caller)).Succeeded);
        var stoppedLease = Assert.IsType<Action<string>>(driver.PhysicalInterrupt);
        Assert.True((await manager.CaptureScreenshotAsync(caller)).Succeeded);
        Assert.Same(stoppedLease, driver.PhysicalInterrupt);
        Assert.Equal(1, driver.StartedInterruptMonitors);

        stoppedLease("physical_double_esc");
        Assert.True(published[^1].EmergencyStopped);
        Assert.False(published[^1].Active);
        Assert.Null(driver.PhysicalInterrupt);
        Assert.Equal("emergency_stopped", (await manager.CaptureScreenshotAsync(caller)).ErrorCode);
        Assert.Equal("emergency_stopped", (await manager.GetScreenInfoAsync(caller, unpin: true)).ErrorCode);
        Assert.Equal("emergency_stopped", (await manager.ActAsync(caller, new ComputerActRequest("wait"))).ErrorCode);
        Assert.True((await manager.CaptureScreenshotAsync(other)).Succeeded);
        Assert.True(manager.GetSessionState(other.SessionId).Active);

        var previousLease = Assert.IsType<Action<string>>(driver.PhysicalInterrupt);
        await manager.EndTurnAsync(other.SessionId);
        Assert.False(published[^1].Active);
        Assert.Equal("idle", published[^1].Status);
        Assert.Null(driver.PhysicalInterrupt);

        manager.ResetEmergencyStopForNewTurn(caller.SessionId);
        Assert.True((await manager.CaptureScreenshotAsync(caller)).Succeeded);
        previousLease("physical_double_esc");
        stoppedLease("physical_double_esc");
        Assert.True(manager.GetSessionState(caller.SessionId).Active);
        Assert.False(manager.GetSessionState(caller.SessionId).EmergencyStopped);
    }

    [Fact]
    public async Task PhysicalStop_CancelsWaitAndReleasesLease()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new());
        var driver = new FakeComputerDriver();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance);
        var caller = new ComputerTrustedCallerContext("esc-during-wait", SessionWorkMode.Daily, PlanModeActive: false);

        var waiting = manager.ActAsync(caller, new ComputerActRequest("wait", DurationMs: 15_000));
        Assert.False(waiting.IsCompleted);
        Assert.IsType<Action<string>>(driver.PhysicalInterrupt)("physical_double_esc");

        Assert.Equal("emergency_stopped", (await waiting.WaitAsync(TimeSpan.FromSeconds(2))).ErrorCode);
        Assert.Null(driver.PhysicalInterrupt);
        Assert.True((await manager.CaptureScreenshotAsync(caller with { SessionId = "after-wait" })).Succeeded);
    }

    [Fact]
    public async Task PhysicalStop_CancelsPendingApprovalAndPreventsInput()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new());
        var driver = new FakeComputerDriver();
        var controlPlane = new FixedControlPlane();
        var coordinator = new ComputerApprovalCoordinator(NullLogger<ComputerApprovalCoordinator>.Instance,
            gateway: null, controlPlane);
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, approvalCoordinator: coordinator, controlPlane: controlPlane);
        var caller = new ComputerTrustedCallerContext("esc-during-approval", SessionWorkMode.Daily, PlanModeActive: false);

        var waiting = manager.ActAsync(caller, new ComputerActRequest("click", X: 100, Y: 100));
        Assert.Single(coordinator.GetPendingApprovalsForSession(caller.SessionId));
        Assert.IsType<Action<string>>(driver.PhysicalInterrupt)("physical_double_esc");

        Assert.Equal("emergency_stopped", (await waiting.WaitAsync(TimeSpan.FromSeconds(2))).ErrorCode);
        Assert.Empty(coordinator.GetPendingApprovalsForSession(caller.SessionId));
        Assert.Empty(driver.ExecutedActions);
        Assert.Null(driver.PhysicalInterrupt);
    }

    [Fact]
    public async Task PhysicalStop_KeepsLeaseUntilInterruptedDragReturns()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new());
        var driver = new FakeComputerDriver();
        await using var manager = new ComputerSessionManager(options, driver, new ComputerSecurityGuard(options, driver),
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(new() { PermissionMode = ExecutionPermissionMode.FullAuto }));
        var caller = new ComputerTrustedCallerContext("esc-during-drag", SessionWorkMode.Daily, PlanModeActive: false);
        var other = caller with { SessionId = "after-interrupted-drag" };
        Task<ComputerOperationResult>? competing = null;
        driver.DuringMouseDrag = () =>
        {
            Assert.IsType<Action<string>>(driver.PhysicalInterrupt)("physical_double_esc");
            competing = manager.CaptureScreenshotAsync(other);
        };

        var result = await manager.ActAsync(caller, new ComputerActRequest("drag", X: 100, Y: 100, EndX: 200, EndY: 200));

        Assert.Equal("emergency_stopped", result.ErrorCode);
        Assert.NotNull(competing);
        Assert.Equal("desktop_busy_by_other_session", (await competing).ErrorCode);
        Assert.True((await manager.CaptureScreenshotAsync(other)).Succeeded);
    }

    [Fact]
    public async Task Act_AbortsBeforeInputWhenFullAutoIsRevoked()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new() { AutoMinimizeHostWindow = true, MaxImageLongEdge = 1920 });
        var driver = new FakeComputerDriver();
        var guard = new ComputerSecurityGuard(options, driver);
        var execution = new ExecutionOptions { PermissionMode = ExecutionPermissionMode.FullAuto };
        var targetChecks = 0;
        driver.WindowAtPointResolver = (x, y) =>
        {
            if (x == 20 && y == 20 && ++targetChecks == 2)
            {
                execution.PermissionMode = ExecutionPermissionMode.Ask;
            }

            return null;
        };
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, controlPlane: new FixedControlPlane(),
            executionOptions: new StaticOptionsMonitor<ExecutionOptions>(execution));

        var caller = new ComputerTrustedCallerContext("sess-tightened", SessionWorkMode.Daily, PlanModeActive: false);
        var result = await manager.ActAsync(caller, new ComputerActRequest("click", X: 20, Y: 20, CoordinateSpace: "pixels"));

        Assert.Equal("execution_permission_changed", result.ErrorCode);
        Assert.Empty(driver.ExecutedActions);
    }

    [Theory]
    [InlineData("click", false)]
    [InlineData("type_text", false)]
    [InlineData("click", true)]
    public async Task ApprovalPointerMovement_UsesOriginalTargetAndKeepsHostAwayThroughInput(
        string action, bool moveAfterApproval)
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            CoordinateMode = ComputerCoordinateMode.ScreenshotPixels,
            MaxImageLongEdge = 1920
        });
        var driver = new FakeComputerDriver();
        var host = new ComputerWindowInfoDto("0x100", 4242, "electron", "CodexFlow", 0, 0, 850, 850, false, true, true);
        var target = new ComputerWindowInfoDto("0x300", 7777, "notepad", "Notes", 0, 0, 1200, 900, false, true, false);
        var hostVisible = false;
        driver.WindowAtPointResolver = (_, _) => hostVisible ? host : target;
        driver.ForegroundWindowResolver = () => hostVisible ? host : target;
        var publisher = new TestStatePublisher(state =>
        {
            hostVisible = !state.HostWindowAvoidanceRequested;
            if (!hostVisible && moveAfterApproval) driver.CursorX = 1100;
        });
        var controlPlane = new FixedControlPlane();
        var coordinator = new ComputerApprovalCoordinator(NullLogger<ComputerApprovalCoordinator>.Instance,
            gateway: null, controlPlane);
        var guard = new ComputerSecurityGuard(options, driver);
        guard.RegisterProtectedWindows(host.ProcessId, [host.WindowHandle]);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, approvalCoordinator: coordinator,
            statePublisher: publisher, controlPlane: controlPlane);

        var caller = new ComputerTrustedCallerContext("sess-pointer", SessionWorkMode.Daily, PlanModeActive: false);
        var request = new ComputerActRequest(action, X: 170, Y: 185, Text: "hello");
        var actTask = manager.ActAsync(caller, request);
        var approval = Assert.Single(coordinator.GetPendingApprovalsForSession(caller.SessionId));

        // The user clicks the approval card far from the requested target, and the chat window takes focus.
        hostVisible = true;
        driver.CursorX = 810;
        driver.CursorY = 615;
        var echoesWhileHidden = false;
        driver.BeforeCapture = () => echoesWhileHidden = !hostVisible;
        var decision = await coordinator.TrySubmitDecisionAsync(approval.ApprovalId, approval.RequestDigest,
            ExecutionApprovalDecisionKind.AllowOnce, "user", null);
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, decision!.Outcome);

        var result = await actTask;
        Assert.True(hostVisible);
        if (moveAfterApproval)
        {
            Assert.Equal("physical_mouse_override_detected", result.ErrorCode);
            Assert.Empty(driver.ExecutedActions);
            Assert.False(echoesWhileHidden);
        }
        else
        {
            Assert.True(result.Succeeded, result.Output);
            if (action == "click")
            {
                Assert.Equal(["move@170,185", "click:left:1@170,185"], driver.ExecutedActions);
            }
            else
            {
                Assert.Equal("type:hello", Assert.Single(driver.ExecutedActions));
            }
            Assert.True(echoesWhileHidden);
        }
    }

    [Fact]
    public async Task ApprovalRejectsDragWhenOnlyDestinationMoves()
    {
        var options = new StaticOptionsMonitor<ComputerOptions>(new()
        {
            CoordinateMode = ComputerCoordinateMode.ScreenshotPixels,
            MaxImageLongEdge = 1920
        });
        var driver = new FakeComputerDriver();
        var start = new ComputerWindowInfoDto("0x300", 7777, "notepad", "Notes", 0, 0, 600, 900, false, true, false);
        var end = new ComputerWindowInfoDto("0x400", 8888, "calc", "Calculator", 600, 0, 600, 900, false, false, false);
        driver.WindowAtPointResolver = (x, _) => x < 600 ? start : end;
        var controlPlane = new FixedControlPlane();
        var coordinator = new ComputerApprovalCoordinator(NullLogger<ComputerApprovalCoordinator>.Instance,
            gateway: null, controlPlane);
        var guard = new ComputerSecurityGuard(options, driver);
        await using var manager = new ComputerSessionManager(options, driver, guard,
            NullLogger<ComputerSessionManager>.Instance, approvalCoordinator: coordinator, controlPlane: controlPlane);

        var caller = new ComputerTrustedCallerContext("sess-drag", SessionWorkMode.Daily, PlanModeActive: false);
        var task = manager.ActAsync(caller, new ComputerActRequest("drag", X: 120, Y: 145, EndX: 720, EndY: 145));
        var approval = Assert.Single(coordinator.GetPendingApprovalsForSession(caller.SessionId));
        end = end with { X = 630 };
        await coordinator.TrySubmitDecisionAsync(approval.ApprovalId, approval.RequestDigest,
            ExecutionApprovalDecisionKind.AllowOnce, "user", null);

        Assert.Equal("toctou_window_drift", (await task).ErrorCode);
        Assert.Empty(driver.ExecutedActions);
    }

    [Fact]
    public void TurnVisualAttachmentStore_SlidingWindowKeepsLatestThreeFramesAcrossLongLoop()
    {
        var store = new TurnVisualAttachmentStore(isMultimodalEnabled: true);
        var markers = new List<string>();
        for (var i = 1; i <= 20; i++)
        {
            var item = store.RegisterSliding("computer-screenshot", 3, $"computer-screenshot-{i}.png", "image/png", new byte[] { 1, 2, 3, (byte)i });
            Assert.NotNull(item);
            markers.Add(TurnVisualAttachmentStore.FormatMarker(item!));
        }

        var combinedText = string.Join("\n", markers);
        var retained = store.ExtractReferencedAttachments(combinedText);
        Assert.Equal(3, retained.Count);
        Assert.Equal(
            ["computer-screenshot-18.png", "computer-screenshot-19.png", "computer-screenshot-20.png"],
            retained.Select(static r => r.FileName).ToArray());
    }

    [Fact]
    public void PermissionClamp_And_PlanSafeToolPolicy_EnforceTightenOnlyAndReadOnlyBoundaries()
    {
        var clamp = new ComputerPermissionClamp();
        var options = new ComputerOptions
        {
            Enabled = true,
            InteractionApprovalPolicy = ComputerInteractionApprovalPolicy.AlwaysAsk,
            BlockedProcessNames = ["keepass"]
        };
        clamp.PostConfigure(null, options);

        // Attempted disk hot-reload relaxation must be ignored; tightening must apply.
        options.InteractionApprovalPolicy = ComputerInteractionApprovalPolicy.HighRiskOnly;
        options.BlockedProcessNames = [];
        clamp.PostConfigure(null, options);
        Assert.Equal(ComputerInteractionApprovalPolicy.AlwaysAsk, options.InteractionApprovalPolicy);
        Assert.Contains("keepass", options.BlockedProcessNames, StringComparer.OrdinalIgnoreCase);

        options.Enabled = false;
        clamp.PostConfigure(null, options);
        Assert.False(options.Enabled);

        Assert.True(PlanSafeToolPolicy.IsToolNameAllowed(ComputerToolNames.ScreenInfo, SessionWorkMode.Daily));
        Assert.True(PlanSafeToolPolicy.IsToolNameAllowed(ComputerToolNames.Screenshot, SessionWorkMode.Development));
        Assert.False(PlanSafeToolPolicy.IsToolNameAllowed(ComputerToolNames.Act, SessionWorkMode.Daily));
        Assert.False(PlanSafeToolPolicy.IsToolNameAllowed(ComputerToolNames.RequestTakeover, SessionWorkMode.Development));
    }

    private static byte[] BuildSynthetic24BppBmp(int width, int height, byte b, byte g, byte r)
    {
        var stride = ((width * 24 + 31) / 32) * 4;
        var pixelSize = stride * height;
        var total = 54 + pixelSize;
        var buf = new byte[total];
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2, 4), total);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), 24);

        for (var y = 0; y < height; y++)
        {
            var row = 54 + y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 3;
                buf[p] = b;
                buf[p + 1] = g;
                buf[p + 2] = r;
            }
        }

        return buf;
    }
}
