// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse.Windows;

#pragma warning disable SYSLIB1054 // Explicit P/Invoke with [DefaultDllImportSearchPaths(System32)] for blittable structs & [UnmanagedCallersOnly] callbacks.

/// <summary>
/// Native AOT safe Win32 P/Invoke layer for Windows x64 Computer Use:
/// Per-Monitor DPI v2 coordinates, BitBlt capture, zero-COM-wrapper GDI+ JPEG encoding,
/// SendInput Unicode/virtual-desktop input, process-tree self-UI tracing, and non-injected Esc x2 hook.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Win32ComputerNative
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint MonitorInfoPrimary = 1;
    private const uint GaRoot = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int TokenUiAccess = 26;

    private const int DibRgbColors = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfLeftDown = 0x0002;
    private const uint MouseEventfLeftUp = 0x0004;
    private const uint MouseEventfRightDown = 0x0008;
    private const uint MouseEventfRightUp = 0x0010;
    private const uint MouseEventfMiddleDown = 0x0020;
    private const uint MouseEventfMiddleUp = 0x0040;
    private const uint MouseEventfWheel = 0x0800;
    private const uint MouseEventfHWheel = 0x1000;
    private const uint MouseEventfVirtualDesk = 0x4000;
    private const uint MouseEventfAbsolute = 0x8000;

    private const uint KeyEventfExtendedKey = 0x0001;
    private const uint KeyEventfKeyUp = 0x0002;
    private const uint KeyEventfUnicode = 0x0004;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint VkEscape = 0x1B;
    private const uint LlkhfInjected = 0x10;

    private const int PixelFormat32bppArgb = 0x26200A;
    private static readonly Guid JpegEncoderClsid = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid EncoderQualityGuid = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    private static readonly object GdiPlusGate = new();
    private static nuint s_gdiPlusToken;
    private static bool s_gdiPlusInitialized;

    private static readonly object HookGate = new();
    private static Action<string>? s_activeInterruptCallback;
    private static nint s_activeHookHandle;
    private static PhysicalEscDoubleTapTracker s_escTaps;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderParameter
    {
        public Guid Guid;
        public uint NumberOfValues;
        public uint Type;
        public nint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderParameters
    {
        public uint Count;
        public EncoderParameter Parameter;
    }

    public static IDisposable EnterPerMonitorDpiScope()
        => new DpiAwarenessScope(SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2));

    public static (int Left, int Top, int Width, int Height) GetVirtualDesktopBounds()
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return (left, top, width, height);
    }

    public static IReadOnlyList<ComputerDisplayInfoDto> EnumerateDisplays()
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var list = new List<ComputerDisplayInfoDto>();
        var handle = GCHandle.Alloc(list);
        try
        {
            _ = EnumDisplayMonitors(0, 0, &EnumMonitorCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumMonitorCallback(nint hMonitor, nint hdcMonitor, RECT* lprcMonitor, nint dwData)
    {
        var list = GCHandle.FromIntPtr(dwData).Target as List<ComputerDisplayInfoDto>;
        if (list is null)
        {
            return 0;
        }

        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (GetMonitorInfoW(hMonitor, &info) != 0)
        {
            var rect = info.rcMonitor;
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            var scale = 1.0d;
            if (GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
            {
                scale = Math.Round(dpiX / 96.0d, 2);
            }

            var deviceName = new string(info.szDevice).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                deviceName = $"DISPLAY-{list.Count + 1}";
            }

            list.Add(new ComputerDisplayInfoDto(
                DisplayId: deviceName,
                IsPrimary: (info.dwFlags & MonitorInfoPrimary) != 0,
                X: rect.Left,
                Y: rect.Top,
                Width: width,
                Height: height,
                PixelWidth: width,
                PixelHeight: height,
                ScaleFactor: scale));
        }

        return 1;
    }

    public static bool TryGetCursorPos(out int x, out int y)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        if (GetCursorPos(out var pt) != 0)
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static HashSet<int> ExpandProtectedProcessTree(IReadOnlySet<int> seedPids)
    {
        var result = new HashSet<int>(seedPids);
        if (seedPids.Count == 0)
        {
            return result;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return result;
        }

        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (Process32FirstW(snapshot, &entry) != 0)
            {
                do
                {
                    var pid = (int)entry.th32ProcessID;
                    var ppid = (int)entry.th32ParentProcessID;
                    if (pid > 0 && ppid > 0 && pid != ppid)
                    {
                        if (!childrenByParent.TryGetValue(ppid, out var children))
                        {
                            children = [];
                            childrenByParent[ppid] = children;
                        }

                        children.Add(pid);
                    }
                }
                while (Process32NextW(snapshot, &entry) != 0);
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
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return result;
    }

    public static bool TryGetParentProcess(int pid, out int parentPid, out string? parentProcessName)
    {
        parentPid = 0;
        parentProcessName = null;
        if (pid <= 0)
        {
            return false;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return false;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (Process32FirstW(snapshot, &entry) != 0)
            {
                do
                {
                    if ((int)entry.th32ProcessID == pid)
                    {
                        parentPid = (int)entry.th32ParentProcessID;
                        if (parentPid > 0)
                        {
                            parentProcessName = ResolveProcessName(parentPid);
                            return true;
                        }

                        return false;
                    }
                }
                while (Process32NextW(snapshot, &entry) != 0);
            }
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return false;
    }

    private sealed class WindowEnumState(
        nint foregroundHwnd,
        HashSet<int> expandedProtectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        public nint ForegroundHwnd { get; } = foregroundHwnd;
        public HashSet<int> ExpandedProtectedPids { get; } = expandedProtectedPids;
        public IReadOnlySet<string> ProtectedHandles { get; } = protectedHandles;
        public List<ComputerWindowInfoDto> Results { get; } = [];
    }

    public static IReadOnlyList<ComputerWindowInfoDto> EnumerateVisibleWindows(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var expanded = ExpandProtectedProcessTree(protectedPids);
        var fg = GetForegroundWindow();
        var state = new WindowEnumState(fg, expanded, protectedHandles);
        var handle = GCHandle.Alloc(state);
        try
        {
            _ = EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return state.Results;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        var state = GCHandle.FromIntPtr(lParam).Target as WindowEnumState;
        if (state is null)
        {
            return 0;
        }

        if (IsWindowVisible(hwnd) == 0)
        {
            return 1;
        }

        // Skip cloaked UWP/virtual-desktop hidden windows.
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return 1;
        }

        var dto = ReadWindowDto(hwnd, state.ForegroundHwnd, state.ExpandedProtectedPids, state.ProtectedHandles);
        if (dto is not null && (!string.IsNullOrWhiteSpace(dto.Title) || dto.IsProtected))
        {
            if (dto.Width >= 20 && dto.Height >= 20)
            {
                state.Results.Add(dto);
            }
        }

        return 1;
    }

    public static ComputerWindowInfoDto? GetWindowAtPoint(
        int x,
        int y,
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var pt = new POINT { X = x, Y = y };
        var hit = WindowFromPoint(pt);
        if (hit == 0)
        {
            return null;
        }

        var root = GetAncestor(hit, GaRoot);
        if (root == 0)
        {
            root = hit;
        }

        var expanded = ExpandProtectedProcessTree(protectedPids);
        return ReadWindowDto(root, GetForegroundWindow(), expanded, protectedHandles);
    }

    public static ComputerWindowInfoDto? GetForegroundWindowInfo(
        IReadOnlySet<int> protectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var fg = GetForegroundWindow();
        if (fg == 0)
        {
            return null;
        }

        var expanded = ExpandProtectedProcessTree(protectedPids);
        return ReadWindowDto(fg, fg, expanded, protectedHandles);
    }

    public static bool TryBringWindowToForeground(string windowHandle)
    {
        if (!TryParseWindowHandle(windowHandle, out var hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd) != 0)
        {
            _ = ShowWindow(hwnd, 9 /* SW_RESTORE */);
        }

        return SetForegroundWindow(hwnd) != 0;
    }

    public static bool TryParseWindowHandle(string? handle, out nint hwnd)
    {
        hwnd = 0;
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        var trimmed = handle.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
            {
                hwnd = (nint)hexVal;
                return hwnd != 0;
            }

            return false;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decVal))
        {
            hwnd = (nint)decVal;
            return hwnd != 0;
        }

        return false;
    }

    public static string FormatWindowHandle(nint hwnd)
        => string.Create(CultureInfo.InvariantCulture, $"0x{hwnd:X}");

    private static ComputerWindowInfoDto? ReadWindowDto(
        nint hwnd,
        nint foregroundHwnd,
        HashSet<int> expandedProtectedPids,
        IReadOnlySet<string> protectedHandles)
    {
        if (hwnd == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pidUint);
        var pid = (int)pidUint;
        var procName = ResolveProcessName(pid);
        var title = ReadWindowTitle(hwnd);
        var minimized = IsIconic(hwnd) != 0;

        RECT rect;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, &rect, sizeof(RECT)) != 0)
        {
            if (GetWindowRect(hwnd, &rect) == 0)
            {
                return null;
            }
        }

        var handleHex = FormatWindowHandle(hwnd);
        var handleDec = ((long)hwnd).ToString(CultureInfo.InvariantCulture);
        var isProtected = expandedProtectedPids.Contains(pid) ||
                          protectedHandles.Contains(handleHex) ||
                          protectedHandles.Contains(handleDec);

        return new ComputerWindowInfoDto(
            WindowHandle: handleHex,
            ProcessId: pid,
            ProcessName: procName,
            Title: title,
            X: rect.Left,
            Y: rect.Top,
            Width: Math.Max(0, rect.Right - rect.Left),
            Height: Math.Max(0, rect.Bottom - rect.Top),
            IsMinimized: minimized,
            IsForeground: hwnd == foregroundHwnd,
            IsProtected: isProtected);
    }

    private static string ResolveProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "unknown";
        }

        var hProc = OpenProcess(ProcessQueryLimitedInformation, 0, (uint)pid);
        if (hProc != 0)
        {
            try
            {
                Span<char> buffer = stackalloc char[512];
                var len = (uint)buffer.Length;
                fixed (char* ptr = buffer)
                {
                    if (QueryFullProcessImageNameW(hProc, 0, ptr, &len) != 0 && len > 0)
                    {
                        var fullPath = new string(buffer[..(int)len]);
                        return Path.GetFileNameWithoutExtension(fullPath);
                    }
                }
            }
            finally
            {
                _ = CloseHandle(hProc);
            }
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch (Exception)
        {
            return $"pid:{pid}";
        }
    }

    private static string ReadWindowTitle(nint hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var cap = Math.Min(len + 1, 512);
        Span<char> buffer = stackalloc char[cap];
        fixed (char* ptr = buffer)
        {
            var actual = GetWindowTextW(hwnd, ptr, cap);
            return actual > 0 ? new string(buffer[..actual]) : string.Empty;
        }
    }

    public static RawCapturedFrame CaptureScreenRegion(int x, int y, int width, int height)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var (vLeft, vTop, vWidth, vHeight) = GetVirtualDesktopBounds();
        var safeW = Math.Max(1, width);
        var safeH = Math.Max(1, height);

        var hdcScreen = GetDC(0);
        if (hdcScreen == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取桌面设备上下文 (GetDC)。");
        }

        nint hdcMem = 0;
        nint hBitmap = 0;
        nint oldObj = 0;
        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建兼容内存 DC。");
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = safeW,
                    biHeight = -safeH, // Top-down DIB matching BGRA32 buffer order.
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            hBitmap = CreateDIBSection(hdcScreen, &bmi, DibRgbColors, out nint bitsPtr, 0, 0);
            if (hBitmap == 0 || bitsPtr == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection 分配位图失败。");
            }

            oldObj = SelectObject(hdcMem, hBitmap);
            if (BitBlt(hdcMem, 0, 0, safeW, safeH, hdcScreen, x, y, SrcCopy | CaptureBlt) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt 屏幕捕获失败。");
            }

            var totalBytes = checked(safeW * safeH * 4);
            var buffer = new byte[totalBytes];
            Marshal.Copy(bitsPtr, buffer, 0, totalBytes);
            // Force opaque alpha channel across captured desktop pixels.
            for (var i = 3; i < buffer.Length; i += 4)
            {
                buffer[i] = 255;
            }

            return new RawCapturedFrame(
                buffer,
                safeW,
                safeH,
                x,
                y,
                vLeft,
                vTop,
                vWidth,
                vHeight);
        }
        finally
        {
            if (oldObj != 0 && hdcMem != 0)
            {
                _ = SelectObject(hdcMem, oldObj);
            }

            if (hBitmap != 0)
            {
                _ = DeleteObject(hBitmap);
            }

            if (hdcMem != 0)
            {
                _ = DeleteDC(hdcMem);
            }

            _ = ReleaseDC(0, hdcScreen);
        }
    }

    public static bool TryEncodeJpegZeroComWrapper(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        long quality,
        out byte[] encoded)
    {
        encoded = [];
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
        {
            return false;
        }

        if (!EnsureGdiPlusInitialized())
        {
            return false;
        }

        nint gpBitmap = 0;
        nint streamPtr = 0;
        try
        {
            fixed (byte* scan0 = bgra)
            {
                if (GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppArgb, (nint)scan0, out gpBitmap) != 0 ||
                    gpBitmap == 0)
                {
                    return false;
                }

                if (CreateStreamOnHGlobal(0, 1, out streamPtr) != 0 || streamPtr == 0)
                {
                    return false;
                }

                var q = Math.Clamp(quality, 30L, 95L);
                var encoderParams = new EncoderParameters
                {
                    Count = 1,
                    Parameter = new EncoderParameter
                    {
                        Guid = EncoderQualityGuid,
                        NumberOfValues = 1,
                        Type = 4, // EncoderParameterValueTypeLong
                        Value = (nint)(&q)
                    }
                };

                var clsid = JpegEncoderClsid;
                if (GdipSaveImageToStream(gpBitmap, streamPtr, &clsid, &encoderParams) != 0)
                {
                    return false;
                }
            }

            if (GetHGlobalFromStream(streamPtr, out var hGlobal) != 0 || hGlobal == 0)
            {
                return false;
            }

            var sizeUInt = GlobalSize(hGlobal);
            if (sizeUInt == 0 || sizeUInt > 64 * 1024 * 1024)
            {
                return false;
            }

            var locked = GlobalLock(hGlobal);
            if (locked == 0)
            {
                return false;
            }

            try
            {
                encoded = new byte[(int)sizeUInt];
                Marshal.Copy(locked, encoded, 0, (int)sizeUInt);
                return encoded.Length > 0;
            }
            finally
            {
                _ = GlobalUnlock(hGlobal);
            }
        }
        finally
        {
            if (gpBitmap != 0)
            {
                _ = GdipDisposeImage(gpBitmap);
            }

            if (streamPtr != 0)
            {
                ReleaseComStreamNative(streamPtr);
            }
        }
    }

    private static void ReleaseComStreamNative(nint streamPtr)
    {
        // IUnknown::Release is vtable slot 2; invoked via raw unmanaged function pointer (no ComImport / IL2050).
        var vtable = *(nint**)streamPtr;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        _ = release(streamPtr);
    }

    private static bool EnsureGdiPlusInitialized()
    {
        lock (GdiPlusGate)
        {
            if (s_gdiPlusInitialized)
            {
                return true;
            }

            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            var status = GdiplusStartup(out s_gdiPlusToken, &input, 0);
            s_gdiPlusInitialized = status == 0 && s_gdiPlusToken != 0;
            return s_gdiPlusInitialized;
        }
    }

    public static void MoveMouseAbsolute(int physX, int physY)
    {
        using var dpiScope = EnterPerMonitorDpiScope();
        var (vLeft, vTop, vWidth, vHeight) = GetVirtualDesktopBounds();
        var abs = CoordinateMapper.ToWin32VirtualDesktopAbsolute(physX, physY, vLeft, vTop, vWidth, vHeight);

        var input = new INPUT
        {
            type = InputMouse,
            union = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = abs.NormalizedX,
                    dy = abs.NormalizedY,
                    dwFlags = MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk
                }
            }
        };

        SendInputArray([input]);
    }

    public static bool IsInputBlockedByIntegrity(int targetProcessId)
        => targetProcessId > 0 && targetProcessId != Environment.ProcessId &&
           ReadProcessIntegrityLevel(Environment.ProcessId, requireNoUiAccess: true) is { } callerLevel &&
           ReadProcessIntegrityLevel(targetProcessId) > callerLevel;

    internal static uint? ReadProcessIntegrityLevel(int processId, bool requireNoUiAccess = false)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, 0, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (OpenProcessToken(process, TokenQuery, out var token) == 0)
            {
                return null;
            }

            try
            {
                if (requireNoUiAccess &&
                    (GetTokenInformation(token, TokenUiAccess, out uint uiAccess, sizeof(uint), out _) == 0 || uiAccess != 0))
                {
                    return null;
                }

                _ = GetTokenInformation(token, TokenIntegrityLevel, null, 0, out var length);
                if (length < sizeof(nint))
                {
                    return null;
                }

                byte* buffer = stackalloc byte[(int)length];
                if (GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _) == 0)
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL begins with the SID pointer.
                var sid = *(nint*)buffer;
                var subAuthorityCount = *GetSidSubAuthorityCount(sid);
                return subAuthorityCount == 0 ? null : *GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    public static int ClickAtCursor(int expectedX, int expectedY, string button, int clickCount)
    {
        if (!TryGetCursorPos(out var x, out var y) || x != expectedX || y != expectedY)
        {
            throw new InvalidOperationException("光标在点击前已离开确认位置，已取消点击。请重新截图后重试。");
        }

        var (downFlag, upFlag) = ResolveMouseButtonFlags(button);
        var count = Math.Clamp(clickCount, 1, 3);

        var inputs = new INPUT[count * 2];
        for (var i = 0; i < count; i++)
        {
            inputs[i * 2] = new INPUT
            {
                type = InputMouse,
                union = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = downFlag
                    }
                }
            };
            inputs[i * 2 + 1] = new INPUT
            {
                type = InputMouse,
                union = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = upFlag
                    }
                }
            };
        }

        return SendInputArray(inputs);
    }

    public static void MouseDragAbsolute(int startX, int startY, int endX, int endY, string button, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var dpiScope = EnterPerMonitorDpiScope();
        var (downFlag, upFlag) = ResolveMouseButtonFlags(button);
        var (vLeft, vTop, vWidth, vHeight) = GetVirtualDesktopBounds();
        var startAbs = CoordinateMapper.ToWin32VirtualDesktopAbsolute(startX, startY, vLeft, vTop, vWidth, vHeight);

        SendInputArray([
            new INPUT
            {
                type = InputMouse,
                union = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = startAbs.NormalizedX,
                        dy = startAbs.NormalizedY,
                        dwFlags = MouseEventfMove | downFlag | MouseEventfAbsolute | MouseEventfVirtualDesk
                    }
                }
            }
        ]);

        try
        {
            const int steps = 10;
            for (var step = 1; step <= steps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var ix = startX + (int)Math.Round((endX - startX) * (step / (double)steps));
                var iy = startY + (int)Math.Round((endY - startY) * (step / (double)steps));
                var abs = CoordinateMapper.ToWin32VirtualDesktopAbsolute(ix, iy, vLeft, vTop, vWidth, vHeight);
                SendInputArray([
                    new INPUT
                    {
                        type = InputMouse,
                        union = new INPUTUNION
                        {
                            mi = new MOUSEINPUT
                            {
                                dx = abs.NormalizedX,
                                dy = abs.NormalizedY,
                                dwFlags = MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk
                            }
                        }
                    }
                ]);
                Thread.Sleep(12);
            }
        }
        finally
        {
            SendInputArray([
                new INPUT
                {
                    type = InputMouse,
                    union = new INPUTUNION
                    {
                        mi = new MOUSEINPUT
                        {
                        dwFlags = upFlag
                        }
                    }
                }
            ]);
        }
    }

    public static void MouseScrollAt(int physX, int physY, int deltaX, int deltaY)
    {
        MoveMouseAbsolute(physX, physY);
        var inputs = new List<INPUT>(2);
        if (deltaY != 0)
        {
            // Positive deltaY means scroll down to align with browser_act semantics; Win32 WHEEL_DELTA negative = down.
            inputs.Add(new INPUT
            {
                type = InputMouse,
                union = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)(-deltaY)),
                        dwFlags = MouseEventfWheel
                    }
                }
            });
        }

        if (deltaX != 0)
        {
            inputs.Add(new INPUT
            {
                type = InputMouse,
                union = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)deltaX),
                        dwFlags = MouseEventfHWheel
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInputArray([.. inputs]);
        }
    }

    public static void TypeUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                AddVirtualKeyTap(inputs, 0x0D /* VK_RETURN */, extended: false);
                continue;
            }

            if (ch == '\n')
            {
                AddVirtualKeyTap(inputs, 0x0D /* VK_RETURN */, extended: false);
                continue;
            }

            if (ch == '\t')
            {
                AddVirtualKeyTap(inputs, 0x09 /* VK_TAB */, extended: false);
                continue;
            }

            inputs.Add(new INPUT
            {
                type = InputKeyboard,
                union = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KeyEventfUnicode
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = InputKeyboard,
                union = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KeyEventfUnicode | KeyEventfKeyUp
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInputArray([.. inputs]);
        }
    }

    public static void SendKeyCombo(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var resolved = new List<(ushort Vk, bool Extended)>(keys.Count);
        foreach (var rawKey in keys)
        {
            if (!TryMapVirtualKey(rawKey, out var vk, out var extended))
            {
                throw new ArgumentException($"不支持的按键名称：{rawKey}", nameof(keys));
            }

            resolved.Add((vk, extended));
        }

        var pressed = new List<(ushort Vk, bool Extended)>(resolved.Count);
        try
        {
            foreach (var item in resolved)
            {
                pressed.Add(item);
                SendInputArray([
                    new INPUT
                    {
                        type = InputKeyboard,
                        union = new INPUTUNION
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = item.Vk,
                                dwFlags = item.Extended ? KeyEventfExtendedKey : 0u
                            }
                        }
                    }
                ]);
            }
        }
        finally
        {
            // Release in reverse order even if cancelled mid-combo to guarantee zero stuck modifiers.
            if (pressed.Count > 0)
            {
                var release = new INPUT[pressed.Count];
                for (var i = 0; i < pressed.Count; i++)
                {
                    var item = pressed[pressed.Count - 1 - i];
                    release[i] = new INPUT
                    {
                        type = InputKeyboard,
                        union = new INPUTUNION
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = item.Vk,
                                dwFlags = KeyEventfKeyUp | (item.Extended ? KeyEventfExtendedKey : 0u)
                            }
                        }
                    };
                }

                SendInputArray(release);
            }
        }
    }

    public static IDisposable StartPhysicalEscDoubleTapMonitor(Action<string> onPhysicalInterrupt)
    {
        ArgumentNullException.ThrowIfNull(onPhysicalInterrupt);
        return new Win32KeyboardHookMonitor(onPhysicalInterrupt);
    }

    private sealed class Win32KeyboardHookMonitor : IDisposable
    {
        private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _pumpThread;
        private uint _pumpThreadId;
        private int _disposed;

        public Win32KeyboardHookMonitor(Action<string> onPhysicalInterrupt)
        {
            _pumpThread = new Thread(() =>
            {
                MSG msg;
                // Create the message queue before publishing the thread ID to Dispose.
                _ = PeekMessageW(&msg, 0, 0, 0, 0);
                _pumpThreadId = GetCurrentThreadId();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                nint hook;
                var error = 0;
                lock (HookGate)
                {
                    var hMod = GetModuleHandleW(null);
                    hook = SetWindowsHookExW(WhKeyboardLl, &LowLevelKeyboardProc, hMod, 0);
                    if (hook != 0)
                    {
                        s_activeInterruptCallback = onPhysicalInterrupt;
                        s_escTaps = default;
                        s_activeHookHandle = hook;
                    }
                    else
                    {
                        error = Marshal.GetLastWin32Error();
                    }
                }

                _ready.TrySetResult(hook != 0 ? 0 : Math.Max(1, error));
                if (hook == 0)
                {
                    return;
                }

                try
                {
                    while (Volatile.Read(ref _disposed) == 0 && GetMessageW(&msg, 0, 0, 0) > 0)
                    {
                        _ = TranslateMessage(&msg);
                        _ = DispatchMessageW(&msg);
                    }
                }
                finally
                {
                    lock (HookGate)
                    {
                        if (s_activeHookHandle == hook)
                        {
                            s_activeHookHandle = 0;
                            s_activeInterruptCallback = null;
                        }
                    }

                    _ = UnhookWindowsHookEx(hook);
                }
            })
            {
                IsBackground = true,
                Name = "CodexFlow-Win32-EscMonitor"
            };
            _pumpThread.Start();
            if (!_ready.Task.Wait(TimeSpan.FromMilliseconds(500)))
            {
                Dispose();
                throw new TimeoutException("Timed out starting the Windows Esc monitor.");
            }

            if (_ready.Task.Result is > 0 and var error)
            {
                Dispose();
                throw new Win32Exception(error, "Could not install the Windows Esc monitor.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_pumpThreadId != 0)
            {
                _ = PostThreadMessageW(_pumpThreadId, WmQuit, 0, 0);
            }

            _pumpThread.Join(TimeSpan.FromMilliseconds(400));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp) && lParam != 0)
        {
            var kb = (KBDLLHOOKSTRUCT*)lParam;
            if (kb->vkCode == VkEscape)
            {
                Action<string>? callback = null;
                lock (HookGate)
                {
                    if (s_escTaps.Observe(wParam is WmKeyDown or WmSysKeyDown,
                            (kb->flags & LlkhfInjected) != 0, Environment.TickCount64))
                    {
                        callback = s_activeInterruptCallback;
                    }
                }

                if (callback is not null)
                {
                    ThreadPool.QueueUserWorkItem(static cb => cb("physical_double_esc"), callback, preferLocal: false);
                }
            }
        }

        return CallNextHookEx(s_activeHookHandle, nCode, wParam, lParam);
    }

    private static void AddVirtualKeyTap(List<INPUT> inputs, ushort vk, bool extended)
    {
        var ext = extended ? KeyEventfExtendedKey : 0u;
        inputs.Add(new INPUT
        {
            type = InputKeyboard,
            union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = ext } }
        });
        inputs.Add(new INPUT
        {
            type = InputKeyboard,
            union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = ext | KeyEventfKeyUp } }
        });
    }

    private static (uint Down, uint Up) ResolveMouseButtonFlags(string? button)
        => (button?.Trim().ToLowerInvariant()) switch
        {
            "right" => (MouseEventfRightDown, MouseEventfRightUp),
            "middle" => (MouseEventfMiddleDown, MouseEventfMiddleUp),
            _ => (MouseEventfLeftDown, MouseEventfLeftUp)
        };

    private static int SendInputArray(INPUT[] inputs)
    {
        fixed (INPUT* ptr = inputs)
        {
            var sent = SendInput((uint)inputs.Length, ptr, sizeof(INPUT));
            ValidateInputCount(sent, (uint)inputs.Length, Marshal.GetLastPInvokeError());
            return (int)sent;
        }
    }

    internal static void ValidateInputCount(uint sent, uint requested, int error)
    {
        if (sent != requested)
        {
            throw new Win32Exception(error, $"Windows 仅接收了 {sent}/{requested} 个键鼠事件（SendInput），操作未完整提交。请先截图确认当前状态。");
        }
    }

    internal static bool TryMapVirtualKey(string raw, out ushort vk, out bool extended)
    {
        vk = 0;
        extended = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var key = raw.Trim().ToLowerInvariant();
        switch (key)
        {
            case "ctrl" or "control":
                vk = 0x11;
                return true;
            case "alt" or "option" or "menu":
                vk = 0x12;
                return true;
            case "shift":
                vk = 0x10;
                return true;
            case "win" or "meta" or "cmd" or "command" or "super":
                vk = 0x5B;
                extended = true;
                return true;
            case "enter" or "return":
                vk = 0x0D;
                return true;
            case "esc" or "escape":
                vk = 0x1B;
                return true;
            case "tab":
                vk = 0x09;
                return true;
            case "space" or " ":
                vk = 0x20;
                return true;
            case "backspace" or "back":
                vk = 0x08;
                return true;
            case "delete" or "del":
                vk = 0x2E;
                extended = true;
                return true;
            case "insert" or "ins":
                vk = 0x2D;
                extended = true;
                return true;
            case "home":
                vk = 0x24;
                extended = true;
                return true;
            case "end":
                vk = 0x23;
                extended = true;
                return true;
            case "pageup" or "pgup":
                vk = 0x21;
                extended = true;
                return true;
            case "pagedown" or "pgdn":
                vk = 0x22;
                extended = true;
                return true;
            case "left" or "arrowleft":
                vk = 0x25;
                extended = true;
                return true;
            case "up" or "arrowup":
                vk = 0x26;
                extended = true;
                return true;
            case "right" or "arrowright":
                vk = 0x27;
                extended = true;
                return true;
            case "down" or "arrowdown":
                vk = 0x28;
                extended = true;
                return true;
            case "." or "period":
                vk = 0xBE;
                return true;
            case "," or "comma":
                vk = 0xBC;
                return true;
            case "-" or "minus":
                vk = 0xBD;
                return true;
            case "=" or "plus" or "equal":
                vk = 0xBB;
                return true;
            case "/" or "slash":
                vk = 0xBF;
                return true;
        }

        if (key.Length == 1)
        {
            var ch = key[0];
            if (ch is >= 'a' and <= 'z')
            {
                vk = (ushort)(ch - 'a' + 0x41);
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                vk = (ushort)(ch - '0' + 0x30);
                return true;
            }
        }

        if (key.StartsWith('f') && int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fn) && fn is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }

        return false;
    }

    private sealed class DpiAwarenessScope(nint previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0 && previous != 0)
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int EnumDisplayMonitors(nint hdc, nint lprcClip, delegate* unmanaged[Stdcall]<nint, nint, RECT*, nint, int> lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMonitorInfoW(nint hMonitor, MONITORINFOEXW* lpmi);

    [DllImport("shcore.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowRect(nint hWnd, RECT* lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DeleteDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateDIBSection(nint hdc, BITMAPINFO* pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DeleteObject(nint ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nuint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PeekMessageW(MSG* lpMsg, nint hWnd, uint minMessage, uint maxMessage, uint removeMessage);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int TranslateMessage(MSG* lpMsg);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DispatchMessageW(MSG* lpMsg);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetModuleHandleW(char* lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Process32FirstW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Process32NextW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int QueryFullProcessImageNameW(nint hProcess, uint dwFlags, char* lpExeName, uint* lpdwSize);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CloseHandle(nint hObject);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetTokenInformation(nint tokenHandle, int informationClass, void* information, uint length, out uint requiredLength);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetTokenInformation(nint tokenHandle, int informationClass, out uint information, uint length, out uint requiredLength);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern byte* GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint* GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("gdiplus.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GdiplusStartup(out nuint token, GdiplusStartupInput* input, nint output);

    [DllImport("gdiplus.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [DllImport("gdiplus.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GdipSaveImageToStream(nint image, nint stream, Guid* clsidEncoder, EncoderParameters* encoderParams);

    [DllImport("gdiplus.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GdipDisposeImage(nint image);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateStreamOnHGlobal(nint hGlobal, int fDeleteOnRelease, out nint ppstm);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetHGlobalFromStream(nint pstm, out nint phglobal);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nuint GlobalSize(nint hMem);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GlobalUnlock(nint hMem);
}

#pragma warning restore SYSLIB1054
