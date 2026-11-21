// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace CodexFlow.Core.ComputerUse;

public readonly record struct ComputerProtectedRect(
    int X,
    int Y,
    int Width,
    int Height,
    string Label = "CODEXFLOW PROTECTED");

/// <summary>
/// Pure managed BGRA32 image processing pipeline: bilinear resize, coordinate grid ruler,
/// dual-tone cursor crosshair, action echo reticle/drag arrow, self-UI/sensitive occlusion masking,
/// BMP24/32 decoder, and zero-dependency PNG encoder (Native AOT safe, 100% cross-platform testable).
/// </summary>
public static class ComputerVisualProcessor
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] ResizeBilinearBgra32(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int destWidth,
        int destHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destWidth <= 0 || destHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Image dimensions must be positive.");
        }

        if (source.Length < checked(sourceWidth * sourceHeight * 4))
        {
            throw new ArgumentException("Source buffer is smaller than expected BGRA32 size.", nameof(source));
        }

        if (sourceWidth == destWidth && sourceHeight == destHeight)
        {
            return source[..(sourceWidth * sourceHeight * 4)].ToArray();
        }

        var dst = new byte[checked(destWidth * destHeight * 4)];
        var scaleX = (double)sourceWidth / destWidth;
        var scaleY = (double)sourceHeight / destHeight;

        for (var dy = 0; dy < destHeight; dy++)
        {
            var sy = Math.Clamp((dy + 0.5d) * scaleY - 0.5d, 0d, sourceHeight - 1);
            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var wy1 = sy - y0;
            var wy0 = 1d - wy1;

            var rowOffset = dy * destWidth * 4;
            var srcRow0 = y0 * sourceWidth * 4;
            var srcRow1 = y1 * sourceWidth * 4;

            for (var dx = 0; dx < destWidth; dx++)
            {
                var sx = Math.Clamp((dx + 0.5d) * scaleX - 0.5d, 0d, sourceWidth - 1);
                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var wx1 = sx - x0;
                var wx0 = 1d - wx1;

                var p00 = srcRow0 + x0 * 4;
                var p10 = srcRow0 + x1 * 4;
                var p01 = srcRow1 + x0 * 4;
                var p11 = srcRow1 + x1 * 4;

                var w00 = wx0 * wy0;
                var w10 = wx1 * wy0;
                var w01 = wx0 * wy1;
                var w11 = wx1 * wy1;

                var outIdx = rowOffset + dx * 4;
                dst[outIdx] = (byte)Math.Clamp((int)Math.Round(source[p00] * w00 + source[p10] * w10 + source[p01] * w01 + source[p11] * w11), 0, 255);
                dst[outIdx + 1] = (byte)Math.Clamp((int)Math.Round(source[p00 + 1] * w00 + source[p10 + 1] * w10 + source[p01 + 1] * w01 + source[p11 + 1] * w11), 0, 255);
                dst[outIdx + 2] = (byte)Math.Clamp((int)Math.Round(source[p00 + 2] * w00 + source[p10 + 2] * w10 + source[p01 + 2] * w01 + source[p11 + 2] * w11), 0, 255);
                dst[outIdx + 3] = 255;
            }
        }

        return dst;
    }

    public static void ApplyProtectedRectMasks(
        Span<byte> bgra,
        int width,
        int height,
        IReadOnlyList<ComputerProtectedRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
        {
            return;
        }

        foreach (var rect in rects)
        {
            var x0 = Math.Max(0, rect.X);
            var y0 = Math.Max(0, rect.Y);
            var x1 = Math.Min(width, rect.X + rect.Width);
            var y1 = Math.Min(height, rect.Y + rect.Height);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            FillRect(bgra, width, height, x0, y0, x1 - x0, y1 - y0, 14, 14, 18);
            DrawRectOutline(bgra, width, height, x0, y0, x1 - x0, y1 - y0, 2, 60, 76, 231);

            var label = string.IsNullOrWhiteSpace(rect.Label) ? "CODEXFLOW PROTECTED" : rect.Label.Trim();
            if (x1 - x0 >= 32 && y1 - y0 >= 16)
            {
                var scale = (x1 - x0) >= 240 ? 2 : 1;
                var textW = MeasureTextWidth(label, scale);
                var textH = 7 * scale;
                var tx = x0 + Math.Max(6, ((x1 - x0) - textW) / 2);
                var ty = y0 + Math.Max(6, ((y1 - y0) - textH) / 2);
                DrawText(bgra, width, height, tx, ty, label, scale, 240, 244, 255);
            }
        }
    }

    public static void DrawCoordinateGrid(
        Span<byte> bgra,
        int width,
        int height,
        ComputerCoordinateMode effectiveMode)
    {
        if (width < 40 || height < 40 || bgra.Length < checked(width * height * 4))
        {
            return;
        }

        const int gridStep = 100;
        var isNormalized = effectiveMode == ComputerCoordinateMode.Normalized1000;
        var countX = isNormalized ? 9 : width / gridStep;
        var countY = isNormalized ? 9 : height / gridStep;

        for (var i = 1; i <= countX; i++)
        {
            var gx = isNormalized
                ? (int)Math.Round(i * 100d / 1000d * width)
                : i * gridStep;
            if (gx <= 4 || gx >= width - 4)
            {
                continue;
            }

            BlendVerticalLine(bgra, width, height, gx, 0, height - 1, 255, 190, 64, 0.28f);
            var labelValue = isNormalized ? i * 100 : gx;
            var label = labelValue.ToString(CultureInfo.InvariantCulture);
            DrawLabelBadge(bgra, width, height, Math.Min(width - MeasureTextWidth(label, 1) - 6, gx + 2), 3, label);
        }

        for (var j = 1; j <= countY; j++)
        {
            var gy = isNormalized
                ? (int)Math.Round(j * 100d / 1000d * height)
                : j * gridStep;
            if (gy <= 4 || gy >= height - 4)
            {
                continue;
            }

            BlendHorizontalLine(bgra, width, height, gy, 0, width - 1, 255, 190, 64, 0.28f);
            var labelValue = isNormalized ? j * 100 : gy;
            var label = labelValue.ToString(CultureInfo.InvariantCulture);
            DrawLabelBadge(bgra, width, height, 3, Math.Min(height - 13, gy + 2), label);
        }
    }

    public static void DrawCursorCrosshair(Span<byte> bgra, int width, int height, int imgX, int imgY)
    {
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
        {
            return;
        }

        var cx = Math.Clamp(imgX, 0, width - 1);
        var cy = Math.Clamp(imgY, 0, height - 1);
        const int arm = 14;

        // Outer dark outline for contrast on light backgrounds, inner cyan core for dark backgrounds.
        DrawHorizontalLine(bgra, width, height, cy - 1, cx - arm, cx + arm, 12, 12, 16);
        DrawHorizontalLine(bgra, width, height, cy + 1, cx - arm, cx + arm, 12, 12, 16);
        DrawVerticalLine(bgra, width, height, cx - 1, cy - arm, cy + arm, 12, 12, 16);
        DrawVerticalLine(bgra, width, height, cx + 1, cy - arm, cy + arm, 12, 12, 16);

        DrawHorizontalLine(bgra, width, height, cy, cx - arm, cx + arm, 255, 224, 40);
        DrawVerticalLine(bgra, width, height, cx, cy - arm, cy + arm, 255, 224, 40);
        DrawCircle(bgra, width, height, cx, cy, 5, 255, 224, 40);
    }

    public static void DrawActionMarker(
        Span<byte> bgra,
        int width,
        int height,
        int x,
        int y,
        int? dragEndX = null,
        int? dragEndY = null)
    {
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
        {
            return;
        }

        var cx = Math.Clamp(x, 0, width - 1);
        var cy = Math.Clamp(y, 0, height - 1);

        DrawCircle(bgra, width, height, cx, cy, 12, 255, 255, 255);
        DrawCircle(bgra, width, height, cx, cy, 11, 56, 56, 239);
        DrawCircle(bgra, width, height, cx, cy, 10, 56, 56, 239);
        FillRect(bgra, width, height, cx - 2, cy - 2, 5, 5, 56, 56, 239);

        if (dragEndX is { } exRaw && dragEndY is { } eyRaw)
        {
            var ex = Math.Clamp(exRaw, 0, width - 1);
            var ey = Math.Clamp(eyRaw, 0, height - 1);
            DrawLine(bgra, width, height, cx, cy, ex, ey, 56, 56, 239);
            DrawCircle(bgra, width, height, ex, ey, 8, 94, 197, 34);
            DrawCircle(bgra, width, height, ex, ey, 7, 94, 197, 34);
        }
    }

    public static (byte[] Bgra, int Width, int Height) CropPreviewRegion(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int centerX,
        int centerY,
        int cropWidth = 240,
        int cropHeight = 160)
    {
        var cw = Math.Min(width, Math.Max(32, cropWidth));
        var ch = Math.Min(height, Math.Max(32, cropHeight));
        var startX = Math.Clamp(centerX - cw / 2, 0, Math.Max(0, width - cw));
        var startY = Math.Clamp(centerY - ch / 2, 0, Math.Max(0, height - ch));

        var cropped = new byte[checked(cw * ch * 4)];
        for (var y = 0; y < ch; y++)
        {
            var srcIdx = ((startY + y) * width + startX) * 4;
            var dstIdx = y * cw * 4;
            bgra.Slice(srcIdx, cw * 4).CopyTo(cropped.AsSpan(dstIdx, cw * 4));
        }

        DrawActionMarker(cropped, cw, ch, centerX - startX, centerY - startY);
        return (cropped, cw, ch);
    }

    /// <summary>
    /// Decodes a 24bpp or 32bpp uncompressed Windows/macOS BMP into top-down BGRA32 pixels.
    /// </summary>
    public static byte[] DecodeBmp24Or32ToBgra32(ReadOnlySpan<byte> bmpBytes, out int width, out int height)
    {
        if (bmpBytes.Length < 54 || bmpBytes[0] != (byte)'B' || bmpBytes[1] != (byte)'M')
        {
            throw new InvalidDataException("Invalid BMP header.");
        }

        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.Slice(10, 4));
        var dibSize = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.Slice(14, 4));
        if (dibSize < 40 || pixelOffset < 54 || pixelOffset >= bmpBytes.Length)
        {
            throw new InvalidDataException("Unsupported BMP DIB header.");
        }

        width = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.Slice(18, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.Slice(22, 4));
        var topDown = rawHeight < 0;
        height = Math.Abs(rawHeight);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(bmpBytes.Slice(28, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(bmpBytes.Slice(30, 4));

        if (width <= 0 || height <= 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3))
        {
            throw new InvalidDataException($"Unsupported BMP format (bpp={bpp}, compression={compression}).");
        }

        var bytesPerPixel = bpp / 8;
        var stride = ((width * bpp + 31) / 32) * 4;
        var requiredBytes = pixelOffset + checked(stride * height);
        if (bmpBytes.Length < requiredBytes)
        {
            throw new InvalidDataException("Truncated BMP pixel payload.");
        }

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var srcY = topDown ? y : (height - 1 - y);
            var srcRow = pixelOffset + srcY * stride;
            var dstRow = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * bytesPerPixel;
                var d = dstRow + x * 4;
                output[d] = bmpBytes[s];
                output[d + 1] = bmpBytes[s + 1];
                output[d + 2] = bmpBytes[s + 2];
                output[d + 3] = 255;
            }
        }

        return output;
    }

    /// <summary>
    /// Zero-dependency pure managed PNG encoder using BCL <see cref="ZLibStream"/>.
    /// Converts input BGRA32 pixels to standard RGBA8 PNG.
    /// </summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        }

        if (bgra.Length < checked(width * height * 4))
        {
            throw new ArgumentException("Input buffer is smaller than expected BGRA32 size.", nameof(bgra));
        }

        var rowBytes = width * 4;
        var scanlines = new byte[checked((rowBytes + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            var dstRow = y * (rowBytes + 1);
            scanlines[dstRow] = 0; // Filter type 0 (None)
            var srcRow = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * 4;
                var d = dstRow + 1 + x * 4;
                scanlines[d] = bgra[s + 2];     // R
                scanlines[d + 1] = bgra[s + 1]; // G
                scanlines[d + 2] = bgra[s];     // B
                scanlines[d + 3] = 255;         // A
            }
        }

        byte[] compressedIdat;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(scanlines, 0, scanlines.Length);
            }

            compressedIdat = ms.ToArray();
        }

        using var output = new MemoryStream(128 + compressedIdat.Length);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        output.Write(signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;  // Bit depth
        ihdr[9] = 6;  // Color type: RGBA
        ihdr[10] = 0; // Compression
        ihdr[11] = 0; // Filter
        ihdr[12] = 0; // Interlace
        WritePngChunk(output, "IHDR"u8, ihdr);
        WritePngChunk(output, "IDAT"u8, compressedIdat);
        WritePngChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WritePngChunk(MemoryStream target, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        target.Write(lenBuf);
        target.Write(type);
        if (!data.IsEmpty)
        {
            target.Write(data);
        }

        var crc = ComputeCrc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, crc);
        target.Write(lenBuf);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static void DrawLabelBadge(Span<byte> bgra, int width, int height, int x, int y, string text)
    {
        var tw = MeasureTextWidth(text, 1);
        FillRect(bgra, width, height, x, y, tw + 4, 11, 18, 20, 26);
        DrawText(bgra, width, height, x + 2, y + 2, text, 1, 80, 232, 255);
    }

    private static void FillRect(Span<byte> bgra, int width, int height, int rx, int ry, int rw, int rh, byte b, byte g, byte r)
    {
        var x0 = Math.Max(0, rx);
        var y0 = Math.Max(0, ry);
        var x1 = Math.Min(width, rx + rw);
        var y1 = Math.Min(height, ry + rh);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                SetPixel(bgra, width, x, y, b, g, r);
            }
        }
    }

    private static void DrawRectOutline(Span<byte> bgra, int width, int height, int rx, int ry, int rw, int rh, int thickness, byte b, byte g, byte r)
    {
        FillRect(bgra, width, height, rx, ry, rw, thickness, b, g, r);
        FillRect(bgra, width, height, rx, ry + rh - thickness, rw, thickness, b, g, r);
        FillRect(bgra, width, height, rx, ry, thickness, rh, b, g, r);
        FillRect(bgra, width, height, rx + rw - thickness, ry, thickness, rh, b, g, r);
    }

    private static void BlendVerticalLine(Span<byte> bgra, int width, int height, int x, int y0, int y1, byte b, byte g, byte r, float alpha)
    {
        if ((uint)x >= (uint)width)
        {
            return;
        }

        var sy = Math.Max(0, y0);
        var ey = Math.Min(height - 1, y1);
        for (var y = sy; y <= ey; y++)
        {
            BlendPixel(bgra, width, x, y, b, g, r, alpha);
        }
    }

    private static void BlendHorizontalLine(Span<byte> bgra, int width, int height, int y, int x0, int x1, byte b, byte g, byte r, float alpha)
    {
        if ((uint)y >= (uint)height)
        {
            return;
        }

        var sx = Math.Max(0, x0);
        var ex = Math.Min(width - 1, x1);
        for (var x = sx; x <= ex; x++)
        {
            BlendPixel(bgra, width, x, y, b, g, r, alpha);
        }
    }

    private static void DrawVerticalLine(Span<byte> bgra, int width, int height, int x, int y0, int y1, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)width)
        {
            return;
        }

        var sy = Math.Max(0, y0);
        var ey = Math.Min(height - 1, y1);
        for (var y = sy; y <= ey; y++)
        {
            SetPixel(bgra, width, x, y, b, g, r);
        }
    }

    private static void DrawHorizontalLine(Span<byte> bgra, int width, int height, int y, int x0, int x1, byte b, byte g, byte r)
    {
        if ((uint)y >= (uint)height)
        {
            return;
        }

        var sx = Math.Max(0, x0);
        var ex = Math.Min(width - 1, x1);
        for (var x = sx; x <= ex; x++)
        {
            SetPixel(bgra, width, x, y, b, g, r);
        }
    }

    private static void DrawCircle(Span<byte> bgra, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
    {
        var x = radius;
        var y = 0;
        var err = 1 - x;
        while (x >= y)
        {
            PlotIfInside(bgra, width, height, cx + x, cy + y, b, g, r);
            PlotIfInside(bgra, width, height, cx + y, cy + x, b, g, r);
            PlotIfInside(bgra, width, height, cx - y, cy + x, b, g, r);
            PlotIfInside(bgra, width, height, cx - x, cy + y, b, g, r);
            PlotIfInside(bgra, width, height, cx - x, cy - y, b, g, r);
            PlotIfInside(bgra, width, height, cx - y, cy - x, b, g, r);
            PlotIfInside(bgra, width, height, cx + y, cy - x, b, g, r);
            PlotIfInside(bgra, width, height, cx + x, cy - y, b, g, r);
            y++;
            if (err < 0)
            {
                err += 2 * y + 1;
            }
            else
            {
                x--;
                err += 2 * (y - x + 1);
            }
        }
    }

    private static void DrawLine(Span<byte> bgra, int width, int height, int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            FillRect(bgra, width, height, x0 - 1, y0 - 1, 3, 3, b, g, r);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void PlotIfInside(Span<byte> bgra, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x < (uint)width && (uint)y < (uint)height)
        {
            SetPixel(bgra, width, x, y, b, g, r);
        }
    }

    private static void SetPixel(Span<byte> bgra, int width, int x, int y, byte b, byte g, byte r)
    {
        var idx = (y * width + x) * 4;
        bgra[idx] = b;
        bgra[idx + 1] = g;
        bgra[idx + 2] = r;
        bgra[idx + 3] = 255;
    }

    private static void BlendPixel(Span<byte> bgra, int width, int x, int y, byte b, byte g, byte r, float alpha)
    {
        var idx = (y * width + x) * 4;
        var inv = 1f - alpha;
        bgra[idx] = (byte)Math.Clamp((int)(bgra[idx] * inv + b * alpha), 0, 255);
        bgra[idx + 1] = (byte)Math.Clamp((int)(bgra[idx + 1] * inv + g * alpha), 0, 255);
        bgra[idx + 2] = (byte)Math.Clamp((int)(bgra[idx + 2] * inv + r * alpha), 0, 255);
        bgra[idx + 3] = 255;
    }

    private static int MeasureTextWidth(string text, int scale)
        => text.Length == 0 ? 0 : (text.Length * 6 - 1) * Math.Max(1, scale);

    private static void DrawText(Span<byte> bgra, int width, int height, int x, int y, string text, int scale, byte b, byte g, byte r)
    {
        var s = Math.Max(1, scale);
        var cursorX = x;
        foreach (var ch in text)
        {
            var glyph = GetGlyph(char.ToUpperInvariant(ch));
            for (var row = 0; row < 7; row++)
            {
                var bits = glyph[row];
                for (var col = 0; col < 5; col++)
                {
                    if ((bits & (1 << (4 - col))) != 0)
                    {
                        FillRect(bgra, width, height, cursorX + col * s, y + row * s, s, s, b, g, r);
                    }
                }
            }

            cursorX += 6 * s;
        }
    }

    // 5x7 minimal bitmap glyphs (0-9 and uppercase letters used in labels).
    private static ReadOnlySpan<byte> GetGlyph(char ch) => ch switch
    {
        '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        '2' => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        '3' => [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
        '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        'D' => [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
        'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
        'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
        '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
    };
}
