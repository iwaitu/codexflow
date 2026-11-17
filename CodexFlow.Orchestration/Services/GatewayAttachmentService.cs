// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexFlow.Core.Execution;

namespace CodexFlow.Orchestration.Services;

public sealed record UploadedAttachmentResult(
    string Name,
    string RelativePath,
    long SizeBytes,
    string ContentType);

public sealed record ChatAttachmentInput(
    string Name,
    string RelativePath,
    long SizeBytes,
    string? ContentType,
    bool IsWorkspaceReference);

public sealed record ValidatedChatAttachment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("isWorkspaceReference")] bool IsWorkspaceReference);

public sealed class GatewayAttachmentValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Handles safe per-session file attachment staging and validation inside the session workspace.
/// Reuses <see cref="WorkspaceFileWriter"/> and <see cref="WorkspaceFileExplorerService"/> to prevent symlink or path escapes.
/// </summary>
public static class GatewayAttachmentService
{
    public const string AttachmentDirectoryName = ".codexflow-attachments";
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const int MaxAttachmentsPerMessage = 10;
    public const string DefaultAttachmentMessage = "请查看并处理以下附带的文件。";

    private const int MaxBaseFileNameLength = 120;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = false,
    };

    public static async Task<UploadedAttachmentResult> SaveUploadedAttachmentAsync(
        string? workspaceRoot,
        string? rawFileName,
        string? contentType,
        Stream fileStream,
        long? declaredLength,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            throw new GatewayAttachmentValidationException("workspace_unavailable", "当前会话工作区目录不存在。");
        }

        if (declaredLength.HasValue && declaredLength.Value <= 0)
        {
            throw new GatewayAttachmentValidationException("empty_attachment", "不能上传空文件。");
        }

        if (declaredLength.HasValue && declaredLength.Value > MaxAttachmentBytes)
        {
            throw new GatewayAttachmentValidationException(
                "attachment_too_large",
                $"单个附件不能超过 {MaxAttachmentBytes / (1024 * 1024)} MB。");
        }

        var (displayName, safeFileName) = SanitizeFileName(rawFileName);
        var bytes = await ReadBoundedBytesAsync(fileStream, MaxAttachmentBytes, ct).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new GatewayAttachmentValidationException("empty_attachment", "不能上传空文件。");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var attachDir = Path.Combine(fullRoot, AttachmentDirectoryName);
        var dirResult = WorkspaceFileWriter.EnsureDirectory(fullRoot, attachDir);
        if (!dirResult.Success)
        {
            throw new GatewayAttachmentValidationException("attachment_storage_denied", dirResult.Message);
        }

        var prefix = $"{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";
        var uniqueFileName = $"{prefix}-{safeFileName}";
        var fullTargetPath = Path.Combine(attachDir, uniqueFileName);
        var writeResult = WorkspaceFileWriter.WriteFile(fullRoot, fullTargetPath, bytes, expectedSha256: null);
        if (!writeResult.Success)
        {
            throw new GatewayAttachmentValidationException("attachment_storage_denied", writeResult.Message);
        }

        var normalizedMime = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();

        return new UploadedAttachmentResult(
            displayName,
            $"{AttachmentDirectoryName}/{uniqueFileName}",
            bytes.LongLength,
            normalizedMime);
    }

    public static IReadOnlyList<ValidatedChatAttachment> ValidateChatAttachments(
        string? workspaceRoot,
        IReadOnlyList<ChatAttachmentInput>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        if (attachments.Count > MaxAttachmentsPerMessage)
        {
            throw new GatewayAttachmentValidationException(
                "too_many_attachments",
                $"单条消息最多附加 {MaxAttachmentsPerMessage} 个文件。");
        }

        var validated = new List<ValidatedChatAttachment>(attachments.Count);
        var seenPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var item in attachments)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.RelativePath))
            {
                throw new GatewayAttachmentValidationException("invalid_attachment", "附件路径不能为空。");
            }

            if (!WorkspaceFileExplorerService.TryResolveWorkspaceRelativePath(
                    workspaceRoot,
                    item.RelativePath,
                    allowEmpty: false,
                    out _,
                    out var fullTarget,
                    out var normalizedRel,
                    out _,
                    out var errMsg))
            {
                throw new GatewayAttachmentValidationException(
                    "invalid_attachment_path",
                    errMsg ?? "附件路径无效或不在当前工作区内。");
            }

            if (!File.Exists(fullTarget))
            {
                throw new GatewayAttachmentValidationException(
                    "attachment_not_found",
                    $"附件文件不存在：{normalizedRel}");
            }

            if (!seenPaths.Add(normalizedRel))
            {
                continue;
            }

            var actualSize = new FileInfo(fullTarget).Length;
            var (displayName, _) = SanitizeFileName(string.IsNullOrWhiteSpace(item.Name)
                ? Path.GetFileName(fullTarget)
                : item.Name);
            var mime = string.IsNullOrWhiteSpace(item.ContentType)
                ? "application/octet-stream"
                : item.ContentType.Trim();

            validated.Add(new ValidatedChatAttachment(
                displayName,
                normalizedRel,
                actualSize,
                mime,
                item.IsWorkspaceReference));
        }

        return validated;
    }

    public static string FormatUserMessageWithAttachments(
        string? messageText,
        IReadOnlyList<ValidatedChatAttachment> attachments)
    {
        var baseText = string.IsNullOrWhiteSpace(messageText)
            ? DefaultAttachmentMessage
            : messageText.Trim();

        if (attachments.Count == 0)
        {
            return baseText;
        }

        var json = JsonSerializer.Serialize(attachments, MetadataJsonOptions);
        var sb = new StringBuilder(baseText.Length + json.Length + 256);
        sb.Append(baseText);
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("<codexflow_attachments>");
        sb.Append(json);
        sb.AppendLine("</codexflow_attachments>");
        sb.AppendLine("[用户附带文件]");
        foreach (var item in attachments)
        {
            sb.Append("- ");
            sb.Append(item.Name);
            sb.Append(" (");
            sb.Append(item.RelativePath);
            sb.Append(", ");
            sb.Append(FormatBytes(item.SizeBytes));
            sb.AppendLine(")");
        }
        sb.Append("以上文件位于当前会话工作区内，请使用 `ivilson_read`（支持文本、图片及 docx/doc/xls/xlsx/ppt/pptx/pdf 办公文档，PDF 可传 `start_page`/`end_page` 按页读取）、`search_in_files` 或工作区命令按需读取分析。");
        return sb.ToString();
    }

    private static readonly Regex EmbeddedAttachmentTagRegex = new(
        @"<codexflow_attachments>([\s\S]*?)</codexflow_attachments>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ValidatedChatAttachment> ExtractEmbeddedAttachments(string? messageContent)
    {
        if (string.IsNullOrWhiteSpace(messageContent))
        {
            return [];
        }

        var match = EmbeddedAttachmentTagRegex.Match(messageContent);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<ValidatedChatAttachment>>(match.Groups[1].Value);
            return items is { Count: > 0 } ? items : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static (string DisplayName, string SafeFileName) SanitizeFileName(string? rawFileName)
    {
        var candidate = (rawFileName ?? string.Empty).Trim();
        var slashIdx = candidate.LastIndexOfAny(['/', '\\']);
        if (slashIdx >= 0)
        {
            candidate = candidate[(slashIdx + 1)..];
        }

        var display = string.IsNullOrWhiteSpace(candidate) ? "attachment.bin" : candidate.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(display.Length);
        foreach (var ch in display)
        {
            if (ch < 0x20 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || Array.IndexOf(invalidChars, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var safe = sb.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
        {
            safe = "attachment.bin";
        }

        if (safe.Length > MaxBaseFileNameLength)
        {
            var ext = Path.GetExtension(safe);
            if (ext.Length > 16)
            {
                ext = string.Empty;
            }
            var stemLen = Math.Max(1, MaxBaseFileNameLength - ext.Length);
            safe = string.Concat(safe.AsSpan(0, stemLen), ext).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = "attachment" + ext;
            }
        }

        // Prevent Windows reserved device stems such as CON.txt / NUL.bin.
        var stem = safe.Contains('.', StringComparison.Ordinal) ? safe[..safe.IndexOf('.', StringComparison.Ordinal)] : safe;
        if (!WorkspacePath.TryNormalizeStrict(safe, out _, out _))
        {
            safe = "file_" + stem.Replace('.', '_') + Path.GetExtension(safe);
            safe = safe.TrimEnd('.', ' ');
            if (!WorkspacePath.TryNormalizeStrict(safe, out _, out _))
            {
                safe = "attachment.bin";
            }
        }

        return (display, safe);
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new GatewayAttachmentValidationException(
                    "attachment_too_large",
                    $"单个附件不能超过 {maxBytes / (1024 * 1024)} MB。");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static string FormatBytes(long sizeBytes)
    {
        if (sizeBytes < 1024)
        {
            return $"{sizeBytes} B";
        }

        if (sizeBytes < 1024 * 1024)
        {
            return $"{(sizeBytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB";
        }

        return $"{(sizeBytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture)} MB";
    }
}
