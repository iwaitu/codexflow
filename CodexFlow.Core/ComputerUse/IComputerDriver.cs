// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse;

public sealed record RawCapturedFrame(
    ReadOnlyMemory<byte> BgraBytes,
    int Width,
    int Height,
    int OriginX,
    int OriginY,
    int VirtualLeft,
    int VirtualTop,
    int VirtualWidth,
    int VirtualHeight);

/// <summary>
/// Platform abstraction for desktop display/window inspection, screen pixel capture,
/// input injection, and physical interrupt hooks (Windows x64 &amp; macOS).
/// </summary>
public interface IComputerDriver
{
    ComputerDriverStatusDto GetStatus(string emergencyStopShortcut = "CommandOrControl+.");

    IReadOnlyList<ComputerDisplayInfoDto> GetDisplays();

    IReadOnlyList<ComputerWindowInfoDto> GetVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles);

    bool TryGetCursorPosition(out int x, out int y);

    bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName);

    ComputerWindowInfoDto? GetWindowAtPoint(
        int x,
        int y,
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles);

    ComputerWindowInfoDto? GetForegroundWindow(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles);

    bool TryBringWindowToForeground(string windowHandle);

    bool IsProcessInTree(int candidatePid, IReadOnlySet<int> rootProtectedPids);

    RawCapturedFrame CaptureRegion(int x, int y, int width, int height);

    byte[] EncodeScreenshot(ReadOnlySpan<byte> bgra, int width, int height, out string mimeType);

    void PerformMouseClick(int x, int y, string button, int clickCount);

    void PerformMouseMove(int x, int y);

    void PerformMouseDrag(int startX, int startY, int endX, int endY, string button);

    void PerformMouseScroll(int x, int y, int deltaX, int deltaY);

    void PerformTypeText(string text);

    void PerformKeyCombo(IReadOnlyList<string> keys);

    IDisposable StartPhysicalInterruptMonitor(Action<string> onPhysicalInterrupt);
}

public static class ComputerDriverFactory
{
    public static IComputerDriver CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Windows.WindowsComputerDriver();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOs.MacOsComputerDriver();
        }

        return new UnsupportedPlatformComputerDriver();
    }
}
