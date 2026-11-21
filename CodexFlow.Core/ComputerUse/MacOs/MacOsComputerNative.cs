// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse.MacOs;

#pragma warning disable SYSLIB1054 // Explicit P/Invoke with framework paths & [UnmanagedCallersOnly] callbacks for Native AOT.

/// <summary>
/// Native AOT safe CoreGraphics / CoreFoundation / ApplicationServices interop for macOS (arm64/x64) Computer Use.
/// Zero Objective-C runtime bridge dependency; works seamlessly with ScreenCaptureKit via <c>/usr/sbin/screencapture</c>
/// and CGEvent/CGEventTap with <c>kCGEventSourceUserData</c> self-injection tagging.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacOsComputerNative
{
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint CfStringEncodingUtf8 = 0x08000100;
    private const int CfNumberSInt32Type = 3;
    private const int CfNumberSInt64Type = 4;

    private const uint CgWindowListOptionOnScreenOnly = 1u << 0;
    private const uint CgWindowListExcludeDesktopElements = 1u << 4;

    private const uint CgHidEventTap = 0;
    private const uint CgHeadInsertEventTap = 0;
    private const uint CgEventTapOptionListenOnly = 1;
    private const ulong CgEventMaskKeyDown = 1UL << 10;

    private const uint CgEventLeftMouseDown = 1;
    private const uint CgEventLeftMouseUp = 2;
    private const uint CgEventRightMouseDown = 3;
    private const uint CgEventRightMouseUp = 4;
    private const uint CgEventMouseMoved = 5;
    private const uint CgEventLeftMouseDragged = 6;
    private const uint CgEventRightMouseDragged = 7;
    private const uint CgEventOtherMouseDown = 25;
    private const uint CgEventOtherMouseUp = 26;
    private const uint CgEventOtherMouseDragged = 27;

    private const uint CgMouseButtonLeft = 0;
    private const uint CgMouseButtonRight = 1;
    private const uint CgMouseButtonCenter = 2;

    private const uint CgScrollEventUnitPixel = 0;
    private const uint CgMouseEventClickState = 1;
    private const uint CgKeyboardEventKeycode = 9;
    private const uint CgEventSourceUserData = 42;
    private const long CodexFlowInjectedUserData = 0x43464C57; // "CFLW"
    private const long MacVkEscape = 53;

    private const ulong FlagShift = 0x00020000;
    private const ulong FlagControl = 0x00040000;
    private const ulong FlagAlternate = 0x00080000;
    private const ulong FlagCommand = 0x00100000;

    private static readonly object KeyCacheGate = new();
    private static volatile bool s_keysInitialized;
    private static nint s_kNumber;
    private static nint s_kOwnerPid;
    private static nint s_kOwnerName;
    private static nint s_kName;
    private static nint s_kBounds;
    private static nint s_kLayer;

    private static readonly object TapGate = new();
    private static Action<string>? s_activeInterruptCallback;
    private static long s_lastPhysicalEscTicks;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    public static bool HasScreenCapturePermission()
    {
        try
        {
            return CGPreflightScreenCaptureAccess() != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool HasAccessibilityPermission()
    {
        try
        {
            return AXIsProcessTrusted() != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static IReadOnlyList<ComputerDisplayInfoDto> EnumerateDisplays()
    {
        if (CGGetActiveDisplayList(0, null, out var count) != 0 || count == 0)
        {
            return [];
        }

        var ids = new uint[Math.Min(count, 32u)];
        fixed (uint* ptr = ids)
        {
            if (CGGetActiveDisplayList((uint)ids.Length, ptr, out var actual) != 0 || actual == 0)
            {
                return [];
            }

            var mainId = CGMainDisplayID();
            var list = new List<ComputerDisplayInfoDto>((int)actual);
            for (var i = 0; i < actual; i++)
            {
                var id = ids[i];
                var bounds = CGDisplayBounds(id);
                var logicalW = Math.Max(1, (int)Math.Round(bounds.Size.Width));
                var logicalH = Math.Max(1, (int)Math.Round(bounds.Size.Height));
                var pixelW = Math.Max(1, (int)CGDisplayPixelsWide(id));
                var pixelH = Math.Max(1, (int)CGDisplayPixelsHigh(id));
                var scale = logicalW > 0 ? Math.Round((double)pixelW / logicalW, 2) : 1.0d;

                list.Add(new ComputerDisplayInfoDto(
                    DisplayId: string.Create(CultureInfo.InvariantCulture, $"CGDisplay-{id}"),
                    IsPrimary: id == mainId,
                    X: (int)Math.Round(bounds.Origin.X),
                    Y: (int)Math.Round(bounds.Origin.Y),
                    Width: logicalW,
                    Height: logicalH,
                    PixelWidth: pixelW,
                    PixelHeight: pixelH,
                    ScaleFactor: scale));
            }

            return list;
        }
    }

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        var evt = CGEventCreate(0);
        if (evt == 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        try
        {
            var pt = CGEventGetLocation(evt);
            x = (int)Math.Round(pt.X);
            y = (int)Math.Round(pt.Y);
            return true;
        }
        finally
        {
            CFRelease(evt);
        }
    }

    public static HashSet<int> ExpandProtectedProcessTree(IReadOnlySet<int> seedPids)
    {
        var result = new HashSet<int>(seedPids);
        if (seedPids.Count == 0)
        {
            return result;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-axo");
            psi.ArgumentList.Add("pid=,ppid=");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return result;
            }

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            }

            var childrenByParent = new Dictionary<int, List<int>>();
            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid) &&
                    pid > 0 && ppid > 0 && pid != ppid)
                {
                    if (!childrenByParent.TryGetValue(ppid, out var kids))
                    {
                        kids = [];
                        childrenByParent[ppid] = kids;
                    }

                    kids.Add(pid);
                }
            }

            var queue = new Queue<int>(seedPids);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (childrenByParent.TryGetValue(current, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        if (result.Add(kid))
                        {
                            queue.Enqueue(kid);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    public static IReadOnlyList<ComputerWindowInfoDto> EnumerateVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        EnsureWindowKeys();
        var expanded = ExpandProtectedProcessTree(protectedPids);
        var array = CGWindowListCopyWindowInfo(CgWindowListOptionOnScreenOnly | CgWindowListExcludeDesktopElements, 0);
        if (array == 0)
        {
            return [];
        }

        try
        {
            var count = (int)CFArrayGetCount(array);
            var list = new List<ComputerWindowInfoDto>(Math.Min(count, 64));
            var firstNormalAssigned = false;

            for (var i = 0; i < count; i++)
            {
                var dict = CFArrayGetValueAtIndex(array, i);
                if (dict == 0)
                {
                    continue;
                }

                var layer = TryReadDictInt(dict, s_kLayer) ?? -1;
                if (layer != 0)
                {
                    continue;
                }

                var bounds = TryReadDictBounds(dict, s_kBounds);
                if (bounds is not { } rect || rect.Size.Width < 20 || rect.Size.Height < 20)
                {
                    continue;
                }

                var winNumber = TryReadDictInt64(dict, s_kNumber) ?? 0;
                if (winNumber <= 0)
                {
                    continue;
                }

                var pid = TryReadDictInt(dict, s_kOwnerPid) ?? 0;
                var ownerName = TryReadDictString(dict, s_kOwnerName) ?? string.Empty;
                var title = TryReadDictString(dict, s_kName) ?? string.Empty;

                if (string.Equals(ownerName, "Window Server", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ownerName, "Dock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var handleStr = winNumber.ToString(CultureInfo.InvariantCulture);
                var isProtected = expanded.Contains(pid) || protectedHandles.Contains(handleStr);
                var isForeground = !firstNormalAssigned;
                firstNormalAssigned = true;

                list.Add(new ComputerWindowInfoDto(
                    WindowHandle: handleStr,
                    ProcessId: pid,
                    ProcessName: ownerName,
                    Title: string.IsNullOrWhiteSpace(title) ? ownerName : title,
                    X: (int)Math.Round(rect.Origin.X),
                    Y: (int)Math.Round(rect.Origin.Y),
                    Width: (int)Math.Round(rect.Size.Width),
                    Height: (int)Math.Round(rect.Size.Height),
                    IsMinimized: false,
                    IsForeground: isForeground,
                    IsProtected: isProtected));
            }

            return list;
        }
        finally
        {
            CFRelease(array);
        }
    }

    public static ComputerWindowInfoDto? GetWindowAtPoint(
        int x,
        int y,
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        var windows = EnumerateVisibleWindows(protectedPids, protectedHandles);
        foreach (var w in windows)
        {
            if (x >= w.X && x < w.X + w.Width && y >= w.Y && y < w.Y + w.Height)
            {
                return w;
            }
        }

        return null;
    }

    public static ComputerWindowInfoDto? GetForegroundWindowInfo(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        var windows = EnumerateVisibleWindows(protectedPids, protectedHandles);
        foreach (var win in windows)
        {
            if (win.IsForeground)
            {
                return win;
            }
        }

        return windows.Count > 0 ? windows[0] : null;
    }

    public static bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName)
    {
        parentPid = 0;
        parentProcessName = null;
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ppid=");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            _ = proc.WaitForExit(1000);
            if (!int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0)
            {
                return false;
            }

            var namePsi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            namePsi.ArgumentList.Add("-p");
            namePsi.ArgumentList.Add(parentPid.ToString(CultureInfo.InvariantCulture));
            namePsi.ArgumentList.Add("-o");
            namePsi.ArgumentList.Add("comm=");

            using var nameProc = Process.Start(namePsi);
            if (nameProc is not null)
            {
                var comm = nameProc.StandardOutput.ReadToEnd().Trim();
                _ = nameProc.WaitForExit(1000);
                if (!string.IsNullOrWhiteSpace(comm))
                {
                    parentProcessName = Path.GetFileName(comm);
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryBringProcessWindowToForeground(string windowHandle)
    {
        // On macOS without ObjC bridge, look up owning PID and activate via AppleScript `osascript` using pure PID integer.
        var windows = EnumerateVisibleWindows(new HashSet<int>(), new HashSet<string>(StringComparer.Ordinal));
        var target = windows.FirstOrDefault(w => string.Equals(w.WindowHandle, windowHandle.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null || target.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"tell application \"System Events\" to set frontmost of first process whose unix id is {target.ProcessId} to true"));

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            return proc.WaitForExit(2000) && proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static RawCapturedFrame CaptureRegionViaScreencapture(int x, int y, int width, int height)
    {
        var displays = EnumerateDisplays();
        var (vLeft, vTop, vWidth, vHeight) = ComputeVirtualBounds(displays);
        var safeW = Math.Max(1, width);
        var safeH = Math.Max(1, height);

        var tempFile = Path.Combine(Path.GetTempPath(), $"cf-mac-cap-{Guid.NewGuid():N}.bmp");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/screencapture",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("bmp");
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"{x},{y},{safeW},{safeH}"));
            psi.ArgumentList.Add(tempFile);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 /usr/sbin/screencapture。");
            var err = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new TimeoutException("macOS 屏幕捕获超时。");
            }

            if (proc.ExitCode != 0 || !File.Exists(tempFile))
            {
                throw new UnauthorizedAccessException(
                    $"macOS 屏幕捕获失败（exit={proc.ExitCode}）：{err}。请确认已在系统设置 → 隐私与安全性 → 录屏 中授权 CodexFlow。");
            }

            var bmpBytes = File.ReadAllBytes(tempFile);
            var bgra = ComputerVisualProcessor.DecodeBmp24Or32ToBgra32(bmpBytes, out var pixelW, out var pixelH);
            return new RawCapturedFrame(
                BgraBytes: bgra,
                Width: pixelW,
                Height: pixelH,
                OriginX: x,
                OriginY: y,
                VirtualLeft: vLeft,
                VirtualTop: vTop,
                VirtualWidth: vWidth,
                VirtualHeight: vHeight);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    public static void MoveMouse(int x, int y)
    {
        var evt = CGEventCreateMouseEvent(0, CgEventMouseMoved, new CGPoint { X = x, Y = y }, CgMouseButtonLeft);
        PostAndRelease(evt);
    }

    public static void ClickMouse(int x, int y, string button, int clickCount)
    {
        var (downType, upType, cgBtn) = ResolveMouseTypes(button);
        var count = Math.Clamp(clickCount, 1, 3);
        var pt = new CGPoint { X = x, Y = y };

        MoveMouse(x, y);
        for (var i = 1; i <= count; i++)
        {
            var down = CGEventCreateMouseEvent(0, downType, pt, cgBtn);
            if (down != 0)
            {
                CGEventSetIntegerValueField(down, CgMouseEventClickState, i);
                PostAndRelease(down);
            }

            var up = CGEventCreateMouseEvent(0, upType, pt, cgBtn);
            if (up != 0)
            {
                CGEventSetIntegerValueField(up, CgMouseEventClickState, i);
                PostAndRelease(up);
            }
        }
    }

    public static void DragMouse(int startX, int startY, int endX, int endY, string button)
    {
        var (downType, upType, cgBtn) = ResolveMouseTypes(button);
        var dragType = (button?.Trim().ToLowerInvariant()) switch
        {
            "right" => CgEventRightMouseDragged,
            "middle" => CgEventOtherMouseDragged,
            _ => CgEventLeftMouseDragged
        };

        var startPt = new CGPoint { X = startX, Y = startY };
        var down = CGEventCreateMouseEvent(0, downType, startPt, cgBtn);
        PostAndRelease(down);

        try
        {
            const int steps = 10;
            for (var step = 1; step <= steps; step++)
            {
                var ix = startX + (endX - startX) * (step / (double)steps);
                var iy = startY + (endY - startY) * (step / (double)steps);
                var drag = CGEventCreateMouseEvent(0, dragType, new CGPoint { X = ix, Y = iy }, cgBtn);
                PostAndRelease(drag);
                Thread.Sleep(12);
            }
        }
        finally
        {
            var up = CGEventCreateMouseEvent(0, upType, new CGPoint { X = endX, Y = endY }, cgBtn);
            PostAndRelease(up);
        }
    }

    public static void ScrollMouse(int x, int y, int deltaX, int deltaY)
    {
        MoveMouse(x, y);
        var scrollY = deltaY == 0 ? 0 : -(int)Math.Round(deltaY / 10.0d);
        var scrollX = deltaX == 0 ? 0 : -(int)Math.Round(deltaX / 10.0d);
        if (scrollY == 0 && deltaY != 0)
        {
            scrollY = deltaY > 0 ? -1 : 1;
        }

        if (scrollX == 0 && deltaX != 0)
        {
            scrollX = deltaX > 0 ? -1 : 1;
        }

        var evt = CGEventCreateScrollWheelEvent(0, CgScrollEventUnitPixel, 2, scrollY, scrollX);
        PostAndRelease(evt);
    }

    public static void TypeUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Split control chars (\n, \r, \t) and slice printable chunks (<= 20 UTF-16 chars per CGEvent).
        var buffer = new StringBuilder(20);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                FlushUnicodeBuffer(buffer);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                PostSingleKeyTap(36 /* kVK_Return */, 0);
                continue;
            }

            if (ch == '\n')
            {
                FlushUnicodeBuffer(buffer);
                PostSingleKeyTap(36 /* kVK_Return */, 0);
                continue;
            }

            if (ch == '\t')
            {
                FlushUnicodeBuffer(buffer);
                PostSingleKeyTap(48 /* kVK_Tab */, 0);
                continue;
            }

            buffer.Append(ch);
            if (buffer.Length >= 20)
            {
                FlushUnicodeBuffer(buffer);
            }
        }

        FlushUnicodeBuffer(buffer);
    }

    private static void FlushUnicodeBuffer(StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var chunk = buffer.ToString();
        buffer.Clear();

        fixed (char* ptr = chunk)
        {
            var down = CGEventCreateKeyboardEvent(0, 0, 1);
            if (down != 0)
            {
                CGEventKeyboardSetUnicodeString(down, (nuint)chunk.Length, (ushort*)ptr);
                PostAndRelease(down);
            }

            var up = CGEventCreateKeyboardEvent(0, 0, 0);
            if (up != 0)
            {
                CGEventKeyboardSetUnicodeString(up, (nuint)chunk.Length, (ushort*)ptr);
                PostAndRelease(up);
            }
        }
    }

    public static void SendKeyCombo(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        ulong combinedFlags = 0;
        var nonModifierKeys = new List<ushort>();

        foreach (var raw in keys)
        {
            var token = raw.Trim().ToLowerInvariant();
            switch (token)
            {
                case "cmd" or "command" or "meta" or "win" or "super":
                    combinedFlags |= FlagCommand;
                    break;
                case "ctrl" or "control":
                    combinedFlags |= FlagControl;
                    break;
                case "alt" or "option" or "menu":
                    combinedFlags |= FlagAlternate;
                    break;
                case "shift":
                    combinedFlags |= FlagShift;
                    break;
                default:
                    if (!TryMapMacKeyCode(token, out var kc))
                    {
                        throw new ArgumentException($"不支持的 macOS 按键名称：{raw}", nameof(keys));
                    }

                    nonModifierKeys.Add(kc);
                    break;
            }
        }

        var pressedKeyCodes = new List<ushort>(nonModifierKeys.Count);
        try
        {
            foreach (var kc in nonModifierKeys)
            {
                var down = CGEventCreateKeyboardEvent(0, kc, 1);
                if (down != 0)
                {
                    if (combinedFlags != 0)
                    {
                        CGEventSetFlags(down, combinedFlags);
                    }

                    pressedKeyCodes.Add(kc);
                    PostAndRelease(down);
                }
            }
        }
        finally
        {
            for (var i = pressedKeyCodes.Count - 1; i >= 0; i--)
            {
                var kc = pressedKeyCodes[i];
                var up = CGEventCreateKeyboardEvent(0, kc, 0);
                if (up != 0)
                {
                    PostAndRelease(up);
                }
            }
        }
    }

    public static IDisposable StartPhysicalEscDoubleTapMonitor(Action<string> onPhysicalInterrupt)
    {
        ArgumentNullException.ThrowIfNull(onPhysicalInterrupt);
        return new MacOsEventTapMonitor(onPhysicalInterrupt);
    }

    private sealed class MacOsEventTapMonitor : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _loopThread;
        private long _runLoopHandle;
        private int _disposed;

        public MacOsEventTapMonitor(Action<string> onPhysicalInterrupt)
        {
            _loopThread = new Thread(() =>
            {
                lock (TapGate)
                {
                    s_activeInterruptCallback = onPhysicalInterrupt;
                    s_lastPhysicalEscTicks = 0;
                }

                var tap = CGEventTapCreate(
                    CgHidEventTap,
                    CgHeadInsertEventTap,
                    CgEventTapOptionListenOnly,
                    CgEventMaskKeyDown,
                    &EventTapCallback,
                    0);
                if (tap == 0)
                {
                    _ready.Set();
                    return;
                }

                var source = CFMachPortCreateRunLoopSource(0, tap, 0);
                if (source == 0)
                {
                    CFRelease(tap);
                    _ready.Set();
                    return;
                }

                var rl = CFRunLoopGetCurrent();
                Volatile.Write(ref _runLoopHandle, rl);
                var mode = CFStringCreateWithCString(0, "kCFRunLoopDefaultMode"u8, CfStringEncodingUtf8);
                CFRunLoopAddSource(rl, source, mode);
                CGEventTapEnable(tap, 1);
                _ready.Set();

                try
                {
                    CFRunLoopRun();
                }
                finally
                {
                    CFRunLoopRemoveSource(rl, source, mode);
                    CFRelease(mode);
                    CFRelease(source);
                    CFRelease(tap);
                    lock (TapGate)
                    {
                        s_activeInterruptCallback = null;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CodexFlow-MacOS-EscMonitor"
            };
            _loopThread.Start();
            _ready.Wait(TimeSpan.FromMilliseconds(500));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var rl = (nint)Volatile.Read(ref _runLoopHandle);
            if (rl != 0)
            {
                CFRunLoopStop(rl);
            }

            _loopThread.Join(TimeSpan.FromMilliseconds(400));
            _ready.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint EventTapCallback(nint proxy, uint type, nint eventRef, nint userInfo)
    {
        if (eventRef != 0)
        {
            var sourceTag = CGEventGetIntegerValueField(eventRef, CgEventSourceUserData);
            var keycode = CGEventGetIntegerValueField(eventRef, CgKeyboardEventKeycode);
            if (sourceTag != CodexFlowInjectedUserData && keycode == MacVkEscape)
            {
                var now = Environment.TickCount64;
                var prev = Interlocked.Exchange(ref s_lastPhysicalEscTicks, now);
                if (prev > 0 && now - prev <= 450)
                {
                    Interlocked.Exchange(ref s_lastPhysicalEscTicks, 0);
                    var cb = s_activeInterruptCallback;
                    if (cb is not null)
                    {
                        ThreadPool.QueueUserWorkItem(static state =>
                        {
                            var action = (Action<string>)state!;
                            action("physical_double_esc");
                        }, cb);
                    }
                }
            }
        }

        return eventRef;
    }

    private static void PostSingleKeyTap(ushort keyCode, ulong flags)
    {
        var down = CGEventCreateKeyboardEvent(0, keyCode, 1);
        if (down != 0)
        {
            if (flags != 0)
            {
                CGEventSetFlags(down, flags);
            }

            PostAndRelease(down);
        }

        var up = CGEventCreateKeyboardEvent(0, keyCode, 0);
        if (up != 0)
        {
            PostAndRelease(up);
        }
    }

    private static void PostAndRelease(nint evt)
    {
        if (evt == 0)
        {
            return;
        }

        try
        {
            CGEventSetIntegerValueField(evt, CgEventSourceUserData, CodexFlowInjectedUserData);
            CGEventPost(CgHidEventTap, evt);
        }
        finally
        {
            CFRelease(evt);
        }
    }

    private static (uint Down, uint Up, uint Button) ResolveMouseTypes(string? button)
        => (button?.Trim().ToLowerInvariant()) switch
        {
            "right" => (CgEventRightMouseDown, CgEventRightMouseUp, CgMouseButtonRight),
            "middle" => (CgEventOtherMouseDown, CgEventOtherMouseUp, CgMouseButtonCenter),
            _ => (CgEventLeftMouseDown, CgEventLeftMouseUp, CgMouseButtonLeft)
        };

    internal static bool TryMapMacKeyCode(string token, out ushort keyCode)
    {
        keyCode = token switch
        {
            "a" => 0,
            "s" => 1,
            "d" => 2,
            "f" => 3,
            "h" => 4,
            "g" => 5,
            "z" => 6,
            "x" => 7,
            "c" => 8,
            "v" => 9,
            "b" => 11,
            "q" => 12,
            "w" => 13,
            "e" => 14,
            "r" => 15,
            "y" => 16,
            "t" => 17,
            "1" => 18,
            "2" => 19,
            "3" => 20,
            "4" => 21,
            "6" => 22,
            "5" => 23,
            "=" or "plus" or "equal" => 24,
            "9" => 25,
            "7" => 26,
            "-" or "minus" => 27,
            "8" => 28,
            "0" => 29,
            "o" => 31,
            "u" => 32,
            "i" => 34,
            "p" => 35,
            "enter" or "return" => 36,
            "l" => 37,
            "j" => 38,
            "k" => 40,
            "n" => 45,
            "m" => 46,
            "," or "comma" => 43,
            "/" or "slash" => 44,
            "." or "period" => 47,
            "tab" => 48,
            "space" or " " => 49,
            "backspace" or "back" => 51,
            "esc" or "escape" => 53,
            "f17" => 64,
            "f18" => 79,
            "f19" => 80,
            "f20" => 90,
            "f5" => 96,
            "f6" => 97,
            "f7" => 98,
            "f3" => 99,
            "f8" => 100,
            "f9" => 101,
            "f11" => 103,
            "f13" => 105,
            "f16" => 106,
            "f14" => 107,
            "f10" => 109,
            "f12" => 111,
            "f15" => 113,
            "home" => 115,
            "pageup" or "pgup" => 116,
            "delete" or "del" => 117,
            "f4" => 118,
            "end" => 119,
            "f2" => 120,
            "pagedown" or "pgdn" => 121,
            "f1" => 122,
            "left" or "arrowleft" => 123,
            "right" or "arrowright" => 124,
            "down" or "arrowdown" => 125,
            "up" or "arrowup" => 126,
            _ => ushort.MaxValue
        };

        return keyCode != ushort.MaxValue;
    }

    private static (int Left, int Top, int Width, int Height) ComputeVirtualBounds(IReadOnlyList<ComputerDisplayInfoDto> displays)
    {
        if (displays.Count == 0)
        {
            return (0, 0, 1440, 900);
        }

        var minX = displays.Min(static d => d.X);
        var minY = displays.Min(static d => d.Y);
        var maxX = displays.Max(static d => d.X + d.Width);
        var maxY = displays.Max(static d => d.Y + d.Height);
        return (minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private static void EnsureWindowKeys()
    {
        if (s_keysInitialized)
        {
            return;
        }

        lock (KeyCacheGate)
        {
            if (s_keysInitialized)
            {
                return;
            }

            s_kNumber = CreateCfString("kCGWindowNumber"u8);
            s_kOwnerPid = CreateCfString("kCGWindowOwnerPID"u8);
            s_kOwnerName = CreateCfString("kCGWindowOwnerName"u8);
            s_kName = CreateCfString("kCGWindowName"u8);
            s_kBounds = CreateCfString("kCGWindowBounds"u8);
            s_kLayer = CreateCfString("kCGWindowLayer"u8);
            s_keysInitialized = true;
        }
    }

    private static nint CreateCfString(ReadOnlySpan<byte> utf8NullFree)
    {
        var terminated = new byte[utf8NullFree.Length + 1];
        utf8NullFree.CopyTo(terminated);
        fixed (byte* ptr = terminated)
        {
            return CFStringCreateWithCString(0, ptr, CfStringEncodingUtf8);
        }
    }

    private static int? TryReadDictInt(nint dict, nint key)
    {
        if (dict == 0 || key == 0 || CFDictionaryGetValueIfPresent(dict, key, out var val) == 0 || val == 0)
        {
            return null;
        }

        int num = 0;
        return CFNumberGetValue(val, CfNumberSInt32Type, &num) != 0 ? num : null;
    }

    private static long? TryReadDictInt64(nint dict, nint key)
    {
        if (dict == 0 || key == 0 || CFDictionaryGetValueIfPresent(dict, key, out var val) == 0 || val == 0)
        {
            return null;
        }

        long num = 0;
        return CFNumberGetValue(val, CfNumberSInt64Type, &num) != 0 ? num : null;
    }

    private static string? TryReadDictString(nint dict, nint key)
    {
        if (dict == 0 || key == 0 || CFDictionaryGetValueIfPresent(dict, key, out var val) == 0 || val == 0)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* ptr = buffer)
        {
            if (CFStringGetCString(val, ptr, buffer.Length, CfStringEncodingUtf8) == 0)
            {
                return null;
            }

            var len = buffer.IndexOf((byte)0);
            if (len < 0)
            {
                len = buffer.Length;
            }

            return Encoding.UTF8.GetString(buffer[..len]);
        }
    }

    private static CGRect? TryReadDictBounds(nint dict, nint key)
    {
        if (dict == 0 || key == 0 || CFDictionaryGetValueIfPresent(dict, key, out var val) == 0 || val == 0)
        {
            return null;
        }

        CGRect rect;
        return CGRectMakeWithDictionaryRepresentation(val, &rect) != 0 ? rect : null;
    }

    private static nint CFStringCreateWithCString(nint alloc, ReadOnlySpan<byte> utf8, uint encoding)
    {
        var terminated = new byte[utf8.Length + 1];
        utf8.CopyTo(terminated);
        fixed (byte* ptr = terminated)
        {
            return CFStringCreateWithCString(alloc, ptr, encoding);
        }
    }

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte CGPreflightScreenCaptureAccess();

    [DllImport(ApplicationServicesLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte AXIsProcessTrusted();

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint* activeDisplays, out uint displayCount);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte CGRectMakeWithDictionaryRepresentation(nint dict, CGRect* rect);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGEventCreate(nint source);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern CGPoint CGEventGetLocation(nint eventRef);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGEventCreateMouseEvent(nint source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGEventCreateScrollWheelEvent(nint source, uint units, uint wheelCount, int wheel1, int wheel2);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, byte keyDown);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CGEventKeyboardSetUnicodeString(nint eventRef, nuint stringLength, ushort* unicodeString);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CGEventSetFlags(nint eventRef, ulong flags);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CGEventSetIntegerValueField(nint eventRef, uint field, long value);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long CGEventGetIntegerValueField(nint eventRef, uint field);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CGEventPost(uint tap, nint eventRef);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint> callback, nint userInfo);

    [DllImport(CoreGraphicsLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CGEventTapEnable(nint tap, byte enable);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CFRelease(nint cf);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CFArrayGetCount(nint theArray);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CFArrayGetValueAtIndex(nint theArray, nint idx);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte CFDictionaryGetValueIfPresent(nint theDict, nint key, out nint value);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte CFNumberGetValue(nint number, int theType, void* valuePtr);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CFStringCreateWithCString(nint alloc, byte* cStr, uint encoding);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern byte CFStringGetCString(nint theString, byte* buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint CFRunLoopGetCurrent();

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CFRunLoopAddSource(nint rl, nint source, nint mode);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CFRunLoopRemoveSource(nint rl, nint source, nint mode);

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundationLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void CFRunLoopStop(nint rl);
}

#pragma warning restore SYSLIB1054
