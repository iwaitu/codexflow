// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Versioning;
using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsComputerDriver : IComputerDriver
{
    public ComputerDriverStatusDto GetStatus(string emergencyStopShortcut = "CommandOrControl+.")
    {
        var displays = GetDisplays();
        return new ComputerDriverStatusDto(
            Available: displays.Count > 0,
            Platform: "win-x64",
            DriverName: "WindowsComputerDriver (Per-Monitor DPI v2 / BitBlt / SendInput)",
            ScreenCaptureGranted: true,
            AccessibilityGranted: true,
            DisplayCount: displays.Count,
            DiagnosticMessage: displays.Count > 0
                ? $"已检测到 {displays.Count} 台显示器。注意：受 Windows UIPI 限制，无法向高权限管理员窗口或 UAC 安全桌面发送输入。"
                : "未检测到活跃桌面显示器（可能处于无桌面会话环境）。",
            PermissionGuide: null,
            EmergencyStopShortcut: emergencyStopShortcut);
    }

    public IReadOnlyList<ComputerDisplayInfoDto> GetDisplays()
        => Win32ComputerNative.EnumerateDisplays();

    public IReadOnlyList<ComputerWindowInfoDto> GetVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
        => Win32ComputerNative.EnumerateVisibleWindows(protectedPids, protectedHandles);

    public bool TryGetCursorPosition(out int x, out int y)
        => Win32ComputerNative.TryGetCursorPos(out x, out y);

    public bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName)
        => Win32ComputerNative.TryGetParentProcess(pid, out parentPid, out parentProcessName);

    public ComputerWindowInfoDto? GetWindowAtPoint(
        int x,
        int y,
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
        => Win32ComputerNative.GetWindowAtPoint(x, y, protectedPids, protectedHandles);

    public ComputerWindowInfoDto? GetForegroundWindow(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
        => Win32ComputerNative.GetForegroundWindowInfo(protectedPids, protectedHandles);

    public bool TryBringWindowToForeground(string windowHandle)
        => Win32ComputerNative.TryBringWindowToForeground(windowHandle);

    public bool IsProcessInTree(int candidatePid, IReadOnlySet<int> rootProtectedPids)
    {
        ArgumentNullException.ThrowIfNull(rootProtectedPids);
        return Win32ComputerNative.ExpandProtectedProcessTree(rootProtectedPids).Contains(candidatePid);
    }

    public RawCapturedFrame CaptureRegion(int x, int y, int width, int height)
        => Win32ComputerNative.CaptureScreenRegion(x, y, width, height);

    public byte[] EncodeScreenshot(ReadOnlySpan<byte> bgra, int width, int height, out string mimeType)
    {
        if (Win32ComputerNative.TryEncodeJpegZeroComWrapper(bgra, width, height, 80, out var jpegBytes))
        {
            mimeType = "image/jpeg";
            return jpegBytes;
        }

        mimeType = "image/png";
        return ComputerVisualProcessor.EncodePng(bgra, width, height);
    }

    public bool IsInputBlockedByIntegrity(int targetProcessId)
        => Win32ComputerNative.IsInputBlockedByIntegrity(targetProcessId);

    public void PerformMouseClick(int x, int y, string button, int clickCount)
        => PerformMouseClickWithReceipt(x, y, button, clickCount);

    public ComputerClickReceipt? PerformMouseClickWithReceipt(int x, int y, string button, int clickCount)
        => Win32ComputerNative.ClickAtCursor(x, y, button, clickCount);

    public void PerformMouseMove(int x, int y)
        => Win32ComputerNative.MoveMouseAbsolute(x, y);

    public void PerformMouseDrag(int startX, int startY, int endX, int endY, string button, CancellationToken ct)
        => Win32ComputerNative.MouseDragAbsolute(startX, startY, endX, endY, button, ct);

    public void PerformMouseScroll(int x, int y, int deltaX, int deltaY)
        => Win32ComputerNative.MouseScrollAt(x, y, deltaX, deltaY);

    public void PerformTypeText(string text)
        => Win32ComputerNative.TypeUnicodeText(text);

    public void PerformKeyCombo(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Win32ComputerNative.SendKeyCombo(keys);
    }

    public IDisposable StartPhysicalInterruptMonitor(Action<string> onPhysicalInterrupt)
        => Win32ComputerNative.StartPhysicalEscDoubleTapMonitor(onPhysicalInterrupt);
}
