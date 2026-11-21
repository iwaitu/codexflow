// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.ComputerUse;
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

    private sealed class FixedControlPlane(bool available = true) : IControlPlaneStateProvider
    {
        public bool IsControlPlaneAvailable => available;
        public bool SelfApprovalProtection => true;
        public string TokenDeliveryMode => "Pipe";
    }

    private sealed class StubFreezeProbe(bool frozen) : IComputerSessionApprovalFreezeProbe
    {
        public bool Frozen { get; set; } = frozen;
        public bool HasAnyPendingApproval(string sessionId) => Frozen;
    }

    private sealed class FakeComputerDriver : IComputerDriver
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
        public List<string> ExecutedActions { get; } = [];

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
            return true;
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

        public void PerformMouseClick(int x, int y, string button, int clickCount)
        {
            CursorX = x;
            CursorY = y;
            ExecutedActions.Add($"click:{button}:{clickCount}@{x},{y}");
        }

        public void PerformMouseMove(int x, int y)
        {
            CursorX = x;
            CursorY = y;
            ExecutedActions.Add($"move@{x},{y}");
        }

        public void PerformMouseDrag(int startX, int startY, int endX, int endY, string button)
        {
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
            => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
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
    public void VisualProcessor_MasksProtectedRectanglesDrawsOverlaysAndEncodesPngAndBmp()
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

        ComputerVisualProcessor.ApplyProtectedRectMasks(bgra, w, h, [new ComputerProtectedRect(40, 40, 120, 80)]);
        // Inside protected rectangle interior, original light pixel (240,240,240) must be completely overwritten.
        var interiorIdx = (80 * w + 80) * 4;
        Assert.True(bgra[interiorIdx] < 40 && bgra[interiorIdx + 1] < 40);

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

        // Wait briefly for pending approval registration.
        ComputerApprovalRequestDto? pending = null;
        for (var i = 0; i < 40 && pending is null; i++)
        {
            pending = coordinator.GetPendingApprovalsForSession("sess-toctou").FirstOrDefault();
            if (pending is null) await Task.Delay(25);
        }

        Assert.NotNull(pending);
        Assert.Equal(["allow_once", "deny"], pending!.AllowedDecisions);

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
