// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse;

public sealed class UnsupportedPlatformComputerDriver : IComputerDriver
{
    private static string PlatformLabel =>
        OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;

    public ComputerDriverStatusDto GetStatus(string emergencyStopShortcut = "CommandOrControl+.")
        => new(
            Available: false,
            Platform: PlatformLabel,
            DriverName: "UnsupportedPlatformComputerDriver",
            ScreenCaptureGranted: false,
            AccessibilityGranted: false,
            DisplayCount: 0,
            DiagnosticMessage: $"当前操作系统（{PlatformLabel}）暂未启用本机原生 Computer Use 驱动，一期仅支持 Windows x64 与 macOS。",
            PermissionGuide: "请在 Windows x64 或 macOS 桌面环境下运行 CodexFlow，或使用内置 Browser Use 工具操作网页应用。",
            EmergencyStopShortcut: emergencyStopShortcut);

    public IReadOnlyList<ComputerDisplayInfoDto> GetDisplays() => [];

    public IReadOnlyList<ComputerWindowInfoDto> GetVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles) => [];

    public bool TryGetCursorPosition(out int x, out int y)
    {
        x = 0;
        y = 0;
        return false;
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
        IReadOnlySet<string> protectedHandles) => null;

    public ComputerWindowInfoDto? GetForegroundWindow(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles) => null;

    public bool TryBringWindowToForeground(string windowHandle) => false;

    public bool IsProcessInTree(int candidatePid, IReadOnlySet<int> rootProtectedPids)
    {
        ArgumentNullException.ThrowIfNull(rootProtectedPids);
        return rootProtectedPids.Contains(candidatePid);
    }

    public RawCapturedFrame CaptureRegion(int x, int y, int width, int height)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 屏幕捕获。");

    public byte[] EncodeScreenshot(ReadOnlySpan<byte> bgra, int width, int height, out string mimeType)
    {
        mimeType = "image/png";
        return ComputerVisualProcessor.EncodePng(bgra, width, height);
    }

    public void PerformMouseClick(int x, int y, string button, int clickCount)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 鼠标注入。");

    public void PerformMouseMove(int x, int y)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 鼠标移动。");

    public void PerformMouseDrag(int startX, int startY, int endX, int endY, string button, CancellationToken ct)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 鼠标拖拽。");

    public void PerformMouseScroll(int x, int y, int deltaX, int deltaY)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 鼠标滚轮。");

    public void PerformTypeText(string text)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 文本输入。");

    public void PerformKeyCombo(IReadOnlyList<string> keys)
        => throw new PlatformNotSupportedException("当前平台暂不支持原生 Computer Use 快捷键注入。");

    public IDisposable StartPhysicalInterruptMonitor(Action<string> onPhysicalInterrupt)
        => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
