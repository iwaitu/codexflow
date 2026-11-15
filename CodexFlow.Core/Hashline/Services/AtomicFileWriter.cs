// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// 原子文件写入器实现。
/// 实现安全的原子写入：
/// 1. 先写入临时文件
/// 2. 然后原子替换原文件
/// 3. 保留原文件的编码和换行风格
/// </summary>
public sealed class AtomicFileWriter : IAtomicFileWriter
{
    private readonly ILogger<AtomicFileWriter> _logger;

    public AtomicFileWriter(ILogger<AtomicFileWriter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 原子写入文件（无版本校验的兼容重载：仅供没有受信执行上下文的纯库调用；有上下文时拒绝）。
    /// </summary>
    public async Task WriteAsync(
        string filePath,
        string content,
        string encodingName,
        bool hasBom,
        string newLineStyle,
        bool hasTrailingNewline,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (CodexFlow.Core.Agents.Tools.ToolPathResolver.HasAmbientTrustedContext)
        {
            // This overload has no root and no expected version: it is a plain path write. Inside a trusted execution
            // context every write must be identity-checked, so the unguarded overload is not callable there.
            throw new HashlineException(
                HashlineErrorCodes.FileAccessDenied,
                "未带版本校验的写入在受信执行上下文中被拒绝，请使用 WriteVerifiedAsync。");
        }

        if (string.IsNullOrEmpty(filePath))
        {
            throw new HashlineException(HashlineErrorCodes.InvalidRequest, "FilePath 不能为空");
        }

        try
        {
            // 1. 规范化内容中的换行符（内容本身已由 EditApplier 处理末尾换行）
            var normalizedContent = NormalizeLineBreaks(content, newLineStyle);

            // 2-3. 编码内容（hasBom 时显式写入 BOM）
            var bytes = Encode(normalizedContent, encodingName, hasBom);

            // 4. 创建临时文件
            var tempFile = filePath + ".tmp." + Guid.NewGuid().ToString("N");

            try
            {
                // 写入临时文件
                await File.WriteAllBytesAsync(tempFile, bytes, ct).ConfigureAwait(false);

                // 5. 原子替换
                if (File.Exists(filePath))
                {
                    // 在 macOS/Linux 上，File.Replace 不可用，使用移动方式
                    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                    {
                        // 移动临时文件到目标位置（不在源码旁留下备份文件）
                        File.Move(tempFile, filePath, true);
                    }
                    else
                    {
                        // Windows: 使用原子替换
                        File.Replace(tempFile, filePath, destinationBackupFileName: null);
                    }
                }
                else
                {
                    // 文件不存在，直接移动
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Move(tempFile, filePath);
                }

                _logger.LogInformation(
                    "[Hashline] Atomically wrote file: {FilePath}, Size={Size} bytes, Encoding={Encoding}, NewLine={NewLine}, TrailingNewline={TrailingNewline}",
                    filePath,
                    bytes.Length,
                    encodingName,
                    newLineStyle,
                    hasTrailingNewline);
            }
            finally
            {
                // 清理临时文件（如果还存在）
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                        // 忽略清理失败
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HashlineException(HashlineErrorCodes.FileAccessDenied, $"无法访问文件: {filePath}", ex);
        }
        catch (IOException ex)
        {
            throw new HashlineException(HashlineErrorCodes.WriteFailed, $"写入文件失败: {filePath}", ex);
        }
        catch (Exception ex)
        {
            throw new HashlineException(HashlineErrorCodes.UnknownError, $"写入文件时发生未知错误: {filePath}", ex);
        }
    }

    public Task WriteVerifiedAsync(
        string filePath,
        string rootDirectory,
        string expectedRawSha256,
        string content,
        string encodingName,
        bool hasBom,
        string newLineStyle,
        bool hasTrailingNewline,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(rootDirectory) || string.IsNullOrEmpty(expectedRawSha256))
        {
            throw new HashlineException(HashlineErrorCodes.InvalidRequest, "FilePath、根目录和期望版本 hash 都不能为空");
        }

        var bytes = Encode(NormalizeLineBreaks(content, newLineStyle), encodingName, hasBom);
        var result = CodexFlow.Core.Execution.WorkspaceFileWriter.WriteFile(rootDirectory, filePath, bytes, expectedRawSha256);
        if (!result.Success)
        {
            var code = result.ReasonCode == CodexFlow.Core.Execution.ExecutionReasonCodes.ExportRejectedConcurrentModification
                ? HashlineErrorCodes.FileFingerprintMismatch
                : HashlineErrorCodes.FileAccessDenied;
            throw new HashlineException(code, $"写入被拒绝（{result.ReasonCode}）: {result.Message}: {filePath}");
        }

        _logger.LogInformation("[Hashline] Identity-checked write: {FilePath}, Size={Size} bytes", filePath, bytes.Length);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Encodes the text and, when the original file had a BOM, writes it again. <c>Encoding.GetBytes</c> never emits the preamble
    /// (only a StreamWriter does), so returning <c>Encoding.UTF8</c> for "has a BOM" silently dropped the BOM on every write.
    /// </summary>
    internal static byte[] Encode(string content, string encodingName, bool hasBom)
    {
        var encoding = EncodingDetector.GetEncodingFromName(encodingName, hasBom);
        var body = encoding.GetBytes(content);
        if (!hasBom)
        {
            return body;
        }

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            return body; // e.g. UTF8Encoding(false): nothing to write
        }

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>
    /// 规范化换行符。
    /// </summary>
    private static string NormalizeLineBreaks(string content, string newLineStyle)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // 先统一为 \n
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // 再转换为目标换行风格
        if (newLineStyle == "\r\n")
        {
            normalized = normalized.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return normalized;
    }
}