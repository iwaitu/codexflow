// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexFlow.Core.Services;

public sealed record MultimodalProbeResult(
    bool IsSupported,
    string Status,
    string Message);

/// <summary>
/// Probes whether the configured model endpoint accepts image inputs (<see cref="DataContent"/>)
/// using a small built-in 64x64 test PNG image.
/// </summary>
public interface IMultimodalCapabilityProbe
{
    Task<MultimodalProbeResult> ProbeAsync(
        string apiUrl,
        string? apiMode,
        string model,
        string apiKey,
        CancellationToken ct = default);
}

public sealed class DefaultMultimodalCapabilityProbe : IMultimodalCapabilityProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    // 64x64 solid red PNG satisfies minimum image dimension thresholds (e.g. >= 28px for Qwen-VL/vLLM).
    private static readonly byte[] BuiltInTestPng = CreateSolidPng(64, 64, r: 220, g: 38, b: 38);

    private readonly ILogger<DefaultMultimodalCapabilityProbe> _logger;

    public DefaultMultimodalCapabilityProbe(ILogger<DefaultMultimodalCapabilityProbe>? logger = null)
    {
        _logger = logger ?? NullLogger<DefaultMultimodalCapabilityProbe>.Instance;
    }

    public static ReadOnlyMemory<byte> BuiltInTestPngBytes => BuiltInTestPng;

    public async Task<MultimodalProbeResult> ProbeAsync(
        string apiUrl,
        string? apiMode,
        string model,
        string apiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return new MultimodalProbeResult(
                IsSupported: false,
                Status: "NotProbed",
                Message: "模型地址、API Key 或模型名称不完整，未执行多模态测试。");
        }

        using var handler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout };
        using var httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = ProbeTimeout };

        IChatClient? client = null;
        try
        {
            client = VllmChatClientFactory.Create(apiUrl, apiKey, model, apiMode, httpClient);
        }
        catch (Exception ex)
        {
            return new MultimodalProbeResult(
                IsSupported: false,
                Status: "ProbeFailed",
                Message: $"创建模型客户端失败: {ex.Message}");
        }

        using var disposableClient = client;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var probeMessage = new ChatMessage(
                ChatRole.User,
                [
                    new TextContent("请用1到5个字回答：这张图片是什么颜色？"),
                    new DataContent(BuiltInTestPng, "image/png")
                ]);

            var options = new ChatOptions
            {
                MaxOutputTokens = 32,
                Temperature = 0f
            };

            var response = await client.GetResponseAsync([probeMessage], options, timeoutCts.Token).ConfigureAwait(false);
            var reply = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return new MultimodalProbeResult(
                    IsSupported: false,
                    Status: "ProbeFailed",
                    Message: "模型未返回有效文本响应，暂未开启多模态。");
            }

            _logger.LogInformation("Multimodal capability probe succeeded for model {Model}: {Reply}", model, reply);
            return new MultimodalProbeResult(
                IsSupported: true,
                Status: "Supported",
                Message: "内置测试图片识别成功，已自动启用多模态能力。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MultimodalProbeResult(
                IsSupported: false,
                Status: "ProbeFailed",
                Message: "多模态探测请求超时，已保持关闭多模态（保存成功）。");
        }
        catch (Exception ex)
        {
            var status = IsExplicitMultimodalRejection(ex.Message) ? "Unsupported" : "ProbeFailed";
            var summary = ex.Message.Length > 180 ? ex.Message[..180] + "..." : ex.Message;
            _logger.LogInformation(ex, "Multimodal capability probe did not pass for model {Model}", model);
            return new MultimodalProbeResult(
                IsSupported: false,
                Status: status,
                Message: status == "Unsupported"
                    ? $"模型接口拒绝了图片输入，已设为非多模态：{summary}"
                    : $"多模态探测未通过，已设为非多模态：{summary}");
        }
    }

    private static bool IsExplicitMultimodalRejection(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("image", StringComparison.OrdinalIgnoreCase)
               || message.Contains("vision", StringComparison.OrdinalIgnoreCase)
               || message.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
               || message.Contains("image_url", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unsupported content", StringComparison.OrdinalIgnoreCase);
    }

    internal static byte[] CreateSolidPng(int width, int height, byte r, byte g, byte b)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(ms, "IHDR", ihdr);

        var rowStride = 1 + width * 3;
        var raw = new byte[rowStride * height];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * rowStride;
            raw[rowOffset] = 0; // filter type None
            for (var x = 0; x < width; x++)
            {
                var px = rowOffset + 1 + x * 3;
                raw[px] = r;
                raw[px + 1] = g;
                raw[px + 2] = b;
            }
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = compressedStream.ToArray();
        }

        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        output.Write(lenBytes);

        Span<byte> typeBytes = stackalloc byte[4];
        typeBytes[0] = (byte)type[0];
        typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2];
        typeBytes[3] = (byte)type[3];
        output.Write(typeBytes);

        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = ComputeCrc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> typeBytes, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        crc = UpdateCrc32(crc, typeBytes);
        crc = UpdateCrc32(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }

        return crc;
    }
}
