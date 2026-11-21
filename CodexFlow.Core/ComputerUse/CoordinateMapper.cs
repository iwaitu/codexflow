// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.ComputerUse;

/// <summary>
/// Captures the exact mapping geometry between the captured desktop/window region (in driver native coordinates:
/// physical pixels on Windows under Per-Monitor DPI v2, or global logical points on macOS) and the downsampled
/// image sent to the multimodal LLM.
/// </summary>
public sealed record ComputerCaptureViewport(
    int CaptureOriginX,
    int CaptureOriginY,
    int CaptureWidth,
    int CaptureHeight,
    int ImageWidth,
    int ImageHeight,
    int VirtualLeft,
    int VirtualTop,
    int VirtualWidth,
    int VirtualHeight,
    ComputerCoordinateMode EffectiveMode);

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct ImagePoint(int X, int Y);

public readonly record struct AbsoluteDesktopPoint(int NormalizedX, int NormalizedY);

/// <summary>
/// Pure managed coordinate mapper handling downsampled screenshot pixels, <c>[0, 1000]</c> normalized coordinates,
/// negative multi-monitor coordinates, and unbiased Win32 <c>0..65535</c> virtual desktop mapping.
/// </summary>
public static class CoordinateMapper
{
    public static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int maxLongEdge)
    {
        var safeW = Math.Max(1, sourceWidth);
        var safeH = Math.Max(1, sourceHeight);
        var cap = Math.Clamp(maxLongEdge, 320, 4096);

        var longEdge = Math.Max(safeW, safeH);
        if (longEdge <= cap)
        {
            return (safeW, safeH);
        }

        var scale = (double)cap / longEdge;
        var scaledW = Math.Max(1, (int)Math.Round(safeW * scale));
        var scaledH = Math.Max(1, (int)Math.Round(safeH * scale));
        return (scaledW, scaledH);
    }

    public static ScreenPoint ToScreenCoordinate(
        ComputerCaptureViewport viewport,
        double inputX,
        double inputY,
        ComputerCoordinateMode? overrideMode = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (double.IsNaN(inputX) || double.IsInfinity(inputX) ||
            double.IsNaN(inputY) || double.IsInfinity(inputY))
        {
            throw new ArgumentOutOfRangeException(nameof(inputX), "输入坐标必须为有限数值。");
        }

        var mode = overrideMode ?? viewport.EffectiveMode;
        double relX;
        double relY;

        if (mode == ComputerCoordinateMode.Normalized1000)
        {
            var clampedX = Math.Clamp(inputX, 0d, 1000d);
            var clampedY = Math.Clamp(inputY, 0d, 1000d);
            relX = clampedX / 1000d * Math.Max(1, viewport.CaptureWidth);
            relY = clampedY / 1000d * Math.Max(1, viewport.CaptureHeight);
        }
        else
        {
            var imgW = Math.Max(1, viewport.ImageWidth);
            var imgH = Math.Max(1, viewport.ImageHeight);
            var clampedX = Math.Clamp(inputX, 0d, imgW - 1);
            var clampedY = Math.Clamp(inputY, 0d, imgH - 1);
            var ratioX = imgW == 1 ? 0d : clampedX / imgW;
            var ratioY = imgH == 1 ? 0d : clampedY / imgH;
            // Sample the center of the pixel cell when downsampled so hit targets stay centered.
            relX = (clampedX + 0.5d) / imgW * Math.Max(1, viewport.CaptureWidth);
            relY = (clampedY + 0.5d) / imgH * Math.Max(1, viewport.CaptureHeight);
            if (viewport.ImageWidth == viewport.CaptureWidth && viewport.ImageHeight == viewport.CaptureHeight)
            {
                relX = clampedX;
                relY = clampedY;
            }
            _ = ratioX;
            _ = ratioY;
        }

        var maxRelX = Math.Max(0, viewport.CaptureWidth - 1);
        var maxRelY = Math.Max(0, viewport.CaptureHeight - 1);
        var targetX = viewport.CaptureOriginX + Math.Clamp((int)Math.Round(relX), 0, maxRelX);
        var targetY = viewport.CaptureOriginY + Math.Clamp((int)Math.Round(relY), 0, maxRelY);
        return new ScreenPoint(targetX, targetY);
    }

    public static ImagePoint ToImageCoordinate(ComputerCaptureViewport viewport, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var capW = Math.Max(1, viewport.CaptureWidth);
        var capH = Math.Max(1, viewport.CaptureHeight);
        var imgW = Math.Max(1, viewport.ImageWidth);
        var imgH = Math.Max(1, viewport.ImageHeight);

        var normX = (double)(screenX - viewport.CaptureOriginX) / capW;
        var normY = (double)(screenY - viewport.CaptureOriginY) / capH;
        var ix = Math.Clamp((int)Math.Round(normX * imgW), 0, imgW - 1);
        var iy = Math.Clamp((int)Math.Round(normY * imgH), 0, imgH - 1);
        return new ImagePoint(ix, iy);
    }

    /// <summary>
    /// Converts a physical pixel position across a multi-monitor virtual desktop (including negative
    /// coordinates on secondary monitors positioned left or above primary) into Win32 <c>SendInput</c>
    /// <c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c> coordinates in <c>[0, 65535]</c>.
    /// </summary>
    public static AbsoluteDesktopPoint ToWin32VirtualDesktopAbsolute(
        int physicalX,
        int physicalY,
        int virtualLeft,
        int virtualTop,
        int virtualWidth,
        int virtualHeight)
    {
        var safeW = Math.Max(1L, virtualWidth);
        var safeH = Math.Max(1L, virtualHeight);
        var clampedX = Math.Clamp((long)physicalX, virtualLeft, virtualLeft + safeW - 1);
        var clampedY = Math.Clamp((long)physicalY, virtualTop, virtualTop + safeH - 1);

        var normX = ((clampedX - virtualLeft) * 65536L + 32768L) / safeW;
        var normY = ((clampedY - virtualTop) * 65536L + 32768L) / safeH;

        return new AbsoluteDesktopPoint(
            (int)Math.Clamp(normX, 0L, 65535L),
            (int)Math.Clamp(normY, 0L, 65535L));
    }
}
