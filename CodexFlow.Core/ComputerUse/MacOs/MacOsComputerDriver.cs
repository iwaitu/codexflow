// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse.MacOs;

[SupportedOSPlatform("macos")]
public sealed class MacOsComputerDriver : IComputerDriver
{
    public ComputerDriverStatusDto GetStatus(string emergencyStopShortcut = "CommandOrControl+.")
    {
        var displays = GetDisplays();
        var screenGranted = MacOsComputerNative.HasScreenCapturePermission();
        var axGranted = MacOsComputerNative.HasAccessibilityPermission();
        var available = displays.Count > 0 && screenGranted && axGranted;

        string? guide = null;
        string? diag = null;
        if (!screenGranted || !axGranted)
        {
            var missing = new List<string>(2);
            if (!screenGranted) missing.Add("屏幕录制（Screen Recording）");
            if (!axGranted) missing.Add("辅助功能（Accessibility）");
            diag = $"macOS 缺少必要系统授权：{string.Join("、", missing)}。";
            guide = "请打开「系统设置 → 隐私与安全性」，在「屏幕录制」与「辅助功能」中勾选 CodexFlow（或终端宿主），完成后重新启用。";
        }
        else
        {
            diag = $"已检测到 {displays.Count} 台显示器，macOS 录屏与辅助功能权限均已就绪。";
        }

        return new ComputerDriverStatusDto(
            Available: available,
            Platform: RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64",
            DriverName: "MacOsComputerDriver (CoreGraphics / ScreenCaptureKit / CGEvent)",
            ScreenCaptureGranted: screenGranted,
            AccessibilityGranted: axGranted,
            DisplayCount: displays.Count,
            DiagnosticMessage: diag,
            PermissionGuide: guide,
            EmergencyStopShortcut: emergencyStopShortcut);
    }

    public IReadOnlyList<ComputerDisplayInfoDto> GetDisplays()
        => MacOsComputerNative.EnumerateDisplays();

    public IReadOnlyList<ComputerWindowInfoDto> GetVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        ArgumentNullException.ThrowIfNull(protectedPids);
        ArgumentNullException.ThrowIfNull(protectedHandles);
        return MacOsComputerNative.EnumerateVisibleWindows(protectedPids, protectedHandles);
    }

    public bool TryGetCursorPosition(out int x, out int y)
        => MacOsComputerNative.TryGetCursorPosition(out x, out y);

    public bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName)
        => MacOsComputerNative.TryGetParentProcess(pid, out parentPid, out parentProcessName);

    public ComputerWindowInfoDto? GetWindowAtPoint(
        int x,
        int y,
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
        => MacOsComputerNative.GetWindowAtPoint(x, y, protectedPids, protectedHandles);

    public ComputerWindowInfoDto? GetForegroundWindow(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
        => MacOsComputerNative.GetForegroundWindowInfo(protectedPids, protectedHandles);

    public bool TryBringWindowToForeground(string windowHandle)
        => MacOsComputerNative.TryBringProcessWindowToForeground(windowHandle);

    public bool IsProcessInTree(int candidatePid, IReadOnlySet<int> rootProtectedPids)
    {
        ArgumentNullException.ThrowIfNull(rootProtectedPids);
        return MacOsComputerNative.ExpandProtectedProcessTree(rootProtectedPids).Contains(candidatePid);
    }

    public RawCapturedFrame CaptureRegion(int x, int y, int width, int height)
    {
        if (!MacOsComputerNative.HasScreenCapturePermission())
        {
            throw new UnauthorizedAccessException(
                "[macos_screen_capture_permission_denied] 未获得 macOS 屏幕录制权限，请在「系统设置 → 隐私与安全性 → 录屏」中开启授权。");
        }

        return MacOsComputerNative.CaptureRegionViaScreencapture(x, y, width, height);
    }

    public byte[] EncodeScreenshot(ReadOnlySpan<byte> bgra, int width, int height, out string mimeType)
    {
        mimeType = "image/png";
        return ComputerVisualProcessor.EncodePng(bgra, width, height);
    }

    public void PerformMouseClick(int x, int y, string button, int clickCount)
    {
        EnsureAccessibility();
        MacOsComputerNative.ClickMouse(x, y, button, clickCount);
    }

    public void PerformMouseMove(int x, int y)
    {
        EnsureAccessibility();
        MacOsComputerNative.MoveMouse(x, y);
    }

    public void PerformMouseDrag(int startX, int startY, int endX, int endY, string button, CancellationToken ct)
    {
        EnsureAccessibility();
        MacOsComputerNative.DragMouse(startX, startY, endX, endY, button, ct);
    }

    public void PerformMouseScroll(int x, int y, int deltaX, int deltaY)
    {
        EnsureAccessibility();
        MacOsComputerNative.ScrollMouse(x, y, deltaX, deltaY);
    }

    public void PerformTypeText(string text)
    {
        EnsureAccessibility();
        MacOsComputerNative.TypeUnicodeText(text);
    }

    public void PerformKeyCombo(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        EnsureAccessibility();
        MacOsComputerNative.SendKeyCombo(keys);
    }

    public IDisposable StartPhysicalInterruptMonitor(Action<string> onPhysicalInterrupt)
        => MacOsComputerNative.StartPhysicalEscDoubleTapMonitor(onPhysicalInterrupt);

    private static void EnsureAccessibility()
    {
        if (!MacOsComputerNative.HasAccessibilityPermission())
        {
            throw new UnauthorizedAccessException(
                "[macos_accessibility_permission_denied] 未获得 macOS 辅助功能（Accessibility）权限，请在「系统设置 → 隐私与安全性 → 辅助功能」中开启授权。");
        }
    }
}
