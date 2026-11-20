// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Configuration;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Read-only metadata listing tool for the session workspace and host-authorized shared directories.
/// Excludes sensitive credential folders/files, blocks symlink escapes outside authorized roots, and
/// returns bounded paginated metadata without reading file contents.
/// </summary>
public sealed class ListAuthorizedDirectoryTool(
    IConfiguration? configuration = null,
    IWorkspaceSettingsService? workspaceSettings = null) : ICodexTool
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly HashSet<string> SensitiveDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        ".kube",
        ".codexflow"
    };

    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "credentials",
        "credentials.json"
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem",
        ".key",
        ".pfx",
        ".p12"
    };

    public string Name => "list_authorized_directory";

    public string Description =>
        "只读列举当前工作区或宿主已授权共享目录中的文件与子目录元数据（不读取文件内容，支持分页、扩展名和最近修改时间过滤）。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 可选): 相对当前工作区的路径，或宿主允许的绝对路径，默认 \".\"\n" +
        "  - extension (string, 可选): 按扩展名过滤，例如 \".pdf\" 或 \"xlsx\"\n" +
        "  - recent_days (int, 可选): 仅返回最近 N 天内修改的条目\n" +
        "  - limit (int, 可选): 单页最大返回数，默认 50，最大 200\n" +
        "  - cursor (int, 可选): 分页游标（从 0 开始），默认 0";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.ReadOnly;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var workspaceRootRaw = arguments.GetValueOrDefault("workspace_path")?.ToString();
        if (string.IsNullOrWhiteSpace(workspaceRootRaw))
        {
            return Task.FromResult(CodexToolResult.Error("Missing host-bound workspace_path."));
        }

        string workspaceRoot;
        try
        {
            workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRootRaw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(CodexToolResult.Error($"Invalid workspace_path: {ex.Message}"));
        }

        var rawPath = arguments.GetValueOrDefault("path")?.ToString()?.Trim();
        if (string.IsNullOrEmpty(rawPath))
        {
            rawPath = ".";
        }

        if (!TryResolveAuthorizedTarget(rawPath, workspaceRoot, out var resolvedTarget, out var denyReason))
        {
            return Task.FromResult(CodexToolResult.Error(denyReason));
        }

        if (!Directory.Exists(resolvedTarget))
        {
            return Task.FromResult(CodexToolResult.Error($"Directory not found: {rawPath}"));
        }

        var extensionFilter = NormalizeExtensionFilter(arguments.GetValueOrDefault("extension")?.ToString());
        int? recentDays = ParseOptionalInt(arguments.GetValueOrDefault("recent_days"));
        if (recentDays is < 1 or > 36_500)
        {
            return Task.FromResult(CodexToolResult.Error("Parameter 'recent_days' must be between 1 and 36500."));
        }

        var limit = ParseOptionalInt(arguments.GetValueOrDefault("limit")) ?? DefaultLimit;
        limit = Math.Clamp(limit, 1, MaxLimit);
        var cursor = Math.Max(0, ParseOptionalInt(arguments.GetValueOrDefault("cursor")) ?? 0);
        DateTimeOffset? cutoffUtc = recentDays.HasValue
            ? DateTimeOffset.UtcNow.AddDays(-recentDays.Value)
            : null;

        try
        {
            var directoryInfo = new DirectoryInfo(resolvedTarget);
            if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                !IsLinkTargetAuthorized(directoryInfo, workspaceRoot))
            {
                return Task.FromResult(CodexToolResult.Error("[authorized_directory_symlink_denied] Target directory is a symlink pointing outside authorized roots."));
            }

            var entries = EnumerateSafeEntries(directoryInfo, extensionFilter, cutoffUtc)
                .OrderByDescending(static e => string.Equals(e.Kind, "directory", StringComparison.Ordinal))
                .ThenBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var totalMatched = entries.Count;
            var page = entries.Skip(cursor).Take(limit).ToList();
            var nextCursor = cursor + page.Count < totalMatched ? cursor + page.Count : (int?)null;

            var payload = JsonSerializer.Serialize(
                new AuthorizedDirectoryToolResponse(rawPath, totalMatched, cursor, limit, nextCursor, page),
                BuiltInToolJsonContext.Web.AuthorizedDirectoryToolResponse);

            return Task.FromResult(CodexToolResult.Succeeded(
                payload,
                metadata: new { totalMatched, returned = page.Count, nextCursor },
                summary: $"已列举目录 `{rawPath}`（共 {totalMatched} 项，本次返回 {page.Count} 项）"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(CodexToolResult.Error($"Failed to list directory '{rawPath}': {ex.Message}"));
        }
    }

    private bool TryResolveAuthorizedTarget(
        string rawPath,
        string workspaceRoot,
        out string resolvedTarget,
        out string denyReason)
    {
        resolvedTarget = string.Empty;
        denyReason = string.Empty;

        if (rawPath is "." or "./" or ".\\")
        {
            resolvedTarget = workspaceRoot;
            return true;
        }

        string candidate;
        if (!Path.IsPathRooted(rawPath))
        {
            if (!WorkspacePath.TryNormalizeStrict(rawPath, out var normalizedRel, out var relReason))
            {
                denyReason = $"[invalid_path] {relReason}";
                return false;
            }

            candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(workspaceRoot, normalizedRel.Replace('/', Path.DirectorySeparatorChar))));
        }
        else
        {
            try
            {
                candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                denyReason = $"[invalid_path] {ex.Message}";
                return false;
            }
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, workspaceRoot, comparison) ||
            candidate.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, comparison))
        {
            if (HasSensitiveSegment(Path.GetRelativePath(workspaceRoot, candidate)))
            {
                denyReason = "[sensitive_directory_denied] Access to sensitive directories is not permitted.";
                return false;
            }

            resolvedTarget = candidate;
            return true;
        }

        if (!IsPathInsideConfiguredRoots(candidate, workspaceRoot))
        {
            denyReason = "[directory_not_authorized] Target path is outside the session workspace and host-authorized directories.";
            return false;
        }

        if (SensitiveDirectoryNames.Contains(Path.GetFileName(candidate)))
        {
            denyReason = "[sensitive_directory_denied] Access to sensitive directories is not permitted.";
            return false;
        }

        resolvedTarget = candidate;
        return true;
    }

    private bool IsPathInsideConfiguredRoots(string fullPath, string workspaceRoot)
    {
        var roots = new List<string>();
        var configRoots = LocalRepositoryAuthorization.ReadAllowedRoots(configuration);
        if (configRoots.IsValid)
        {
            roots.AddRange(configRoots.Roots);
        }

        if (workspaceSettings is not null)
        {
            roots.AddRange(workspaceSettings.ReadAllowedLocalRepositoryRoots());
        }

        return roots.Count > 0 && LocalRepositoryAuthorization.Evaluate(fullPath, workspaceRoot, roots).Allowed;
    }

    private bool IsLinkTargetAuthorized(FileSystemInfo info, string workspaceRoot)
    {
        try
        {
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
            {
                return false;
            }

            var targetFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(targetFull, workspaceRoot, comparison) ||
                targetFull.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }

            return IsPathInsideConfiguredRoots(targetFull, workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static IEnumerable<AuthorizedEntryDto> EnumerateSafeEntries(
        DirectoryInfo directoryInfo,
        string? extensionFilter,
        DateTimeOffset? cutoffUtc)
    {
        foreach (var entry in directoryInfo.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo)
            {
                if (SensitiveDirectoryNames.Contains(entry.Name))
                {
                    continue;
                }

                if (extensionFilter is not null)
                {
                    continue;
                }

                var modifiedUtc = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (cutoffUtc.HasValue && modifiedUtc < cutoffUtc.Value)
                {
                    continue;
                }

                yield return new AuthorizedEntryDto(
                    entry.Name,
                    "directory",
                    null,
                    modifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            }
            else if (entry is FileInfo file)
            {
                if (IsSensitiveFile(file.Name))
                {
                    continue;
                }

                if (extensionFilter is not null &&
                    !string.Equals(file.Extension, extensionFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var modifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (cutoffUtc.HasValue && modifiedUtc < cutoffUtc.Value)
                {
                    continue;
                }

                yield return new AuthorizedEntryDto(
                    file.Name,
                    "file",
                    file.Length,
                    modifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            }
        }
    }

    private static bool HasSensitiveSegment(string relativePath)
    {
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SensitiveDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSensitiveFile(string fileName)
    {
        if (SensitiveFileNames.Contains(fileName) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && SensitiveExtensions.Contains(ext);
    }

    private static string? NormalizeExtensionFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static int? ParseOptionalInt(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji) => ji,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    internal sealed record AuthorizedEntryDto(
        string Name,
        string Kind,
        long? SizeBytes,
        string LastModifiedUtc);
}
