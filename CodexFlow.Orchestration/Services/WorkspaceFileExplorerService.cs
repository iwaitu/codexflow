// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Documents;

namespace CodexFlow.Orchestration.Services;

public enum WorkspaceFileErrorCode
{
    None = 0,
    WorkspaceUnavailable,
    InvalidPath,
    PathTraversal,
    LinkedPath,
    NotFound,
    IsADirectory
}

public sealed record WorkspaceFileEntryInfo(
    string Name,
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

public sealed record WorkspaceDirectoryListingResult(
    bool Success,
    WorkspaceFileErrorCode ErrorCode,
    string? ErrorMessage,
    string Path,
    IReadOnlyList<WorkspaceFileEntryInfo> Entries,
    bool Truncated);

public sealed record WorkspaceFileContentResult(
    bool Success,
    WorkspaceFileErrorCode ErrorCode,
    string? ErrorMessage,
    string Path,
    string Content,
    long SizeBytes,
    bool Truncated,
    bool IsBinary,
    string Encoding);

public sealed record WorkspaceChangesResult(
    bool HasGitRepository,
    string? BaseRef,
    IReadOnlyList<GitFileChange> Files,
    string UnifiedDiff,
    bool Truncated);

public static class WorkspaceFileExplorerService
{
    public const int MaxDirectoryEntries = 500;
    public const int DefaultMaxFileContentBytes = 512 * 1024;
    public const int DefaultMaxDiffChars = 2 * 1024 * 1024;

    private static readonly HashSet<string> HiddenInternalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codexflow"
    };

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".class", ".db", ".dll",
        ".dylib", ".exe", ".gif", ".gz", ".ico", ".jar", ".jpeg", ".jpg", ".mp3",
        ".mp4", ".nupkg", ".pdb", ".png", ".so", ".sqlite", ".tar",
        ".ttf", ".wav", ".webp", ".woff", ".woff2", ".zip"
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static WorkspaceDirectoryListingResult ListDirectory(string? workspaceRoot, string? relativePath)
    {
        if (!TryResolveWorkspaceRelativePath(workspaceRoot, relativePath, allowEmpty: true, out var fullRoot, out var fullTarget, out var normalizedRelative, out var errCode, out var errMsg))
        {
            return new WorkspaceDirectoryListingResult(false, errCode, errMsg, string.Empty, [], false);
        }

        if (!Directory.Exists(fullTarget))
        {
            return new WorkspaceDirectoryListingResult(
                false,
                File.Exists(fullTarget) ? WorkspaceFileErrorCode.InvalidPath : WorkspaceFileErrorCode.NotFound,
                "Directory does not exist.",
                normalizedRelative,
                [],
                false);
        }

        var dirInfo = new DirectoryInfo(fullTarget);
        var entries = new List<WorkspaceFileEntryInfo>();
        var truncated = false;

        foreach (var child in dirInfo.EnumerateFileSystemInfos()
                     .Where(static e => !HiddenInternalNames.Contains(e.Name)
                                        && (e.Attributes & FileAttributes.ReparsePoint) == 0
                                        && e.LinkTarget is null)
                     .OrderBy(static e => (e.Attributes & FileAttributes.Directory) == 0)
                     .ThenBy(static e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entries.Count >= MaxDirectoryEntries)
            {
                truncated = true;
                break;
            }

            var isDir = (child.Attributes & FileAttributes.Directory) != 0;
            var rel = Path.GetRelativePath(fullRoot, child.FullName).Replace('\\', '/');
            var size = isDir ? 0L : ((FileInfo)child).Length;
            entries.Add(new WorkspaceFileEntryInfo(
                child.Name,
                rel,
                isDir,
                size,
                new DateTimeOffset(child.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return new WorkspaceDirectoryListingResult(true, WorkspaceFileErrorCode.None, null, normalizedRelative, entries, truncated);
    }

    public static async Task<WorkspaceFileContentResult> ReadFileContentAsync(
        string? workspaceRoot,
        string? relativePath,
        int maxBytes = DefaultMaxFileContentBytes,
        DocumentReaderRouter? documentRouter = null,
        CancellationToken ct = default)
    {
        if (!TryResolveWorkspaceRelativePath(workspaceRoot, relativePath, allowEmpty: false, out _, out var fullTarget, out var normalizedRelative, out var errCode, out var errMsg))
        {
            return new WorkspaceFileContentResult(false, errCode, errMsg, string.Empty, string.Empty, 0, false, false, "utf-8");
        }

        if (Directory.Exists(fullTarget))
        {
            return new WorkspaceFileContentResult(
                false,
                WorkspaceFileErrorCode.IsADirectory,
                "Target path is a directory, not a file.",
                normalizedRelative,
                string.Empty,
                0,
                false,
                false,
                "utf-8");
        }

        var fileInfo = new FileInfo(fullTarget);
        if (!fileInfo.Exists)
        {
            return new WorkspaceFileContentResult(
                false,
                WorkspaceFileErrorCode.NotFound,
                "File does not exist.",
                normalizedRelative,
                string.Empty,
                0,
                false,
                false,
                "utf-8");
        }

        var totalSize = fileInfo.Length;
        var ext = Path.GetExtension(fileInfo.Name);
        if (DocumentFileFormats.IsOfficeDocument(fileInfo.Name))
        {
            var router = documentRouter ?? DocumentReaderRouter.CreateDefault();
            var docOptions = new DocumentReadOptions(
                StartPage: 1,
                EndPage: 10,
                ExtractImages: false,
                MaxOutputChars: Math.Clamp(maxBytes, 8 * 1024, 256 * 1024),
                MaxPagesPerRead: 10);
            var docResult = await router.ReadAsync(fullTarget, docOptions, ct).ConfigureAwait(false);
            var previewContent = docResult.Success
                ? docResult.Text
                : docResult.ErrorMessage ?? "无法解析该办公文档内容。";
            return new WorkspaceFileContentResult(
                true,
                WorkspaceFileErrorCode.None,
                null,
                normalizedRelative,
                previewContent,
                totalSize,
                docResult.Truncated,
                IsBinary: false,
                docResult.Success ? "document-markdown" : "document-error");
        }

        if (KnownBinaryExtensions.Contains(ext))
        {
            return new WorkspaceFileContentResult(
                true,
                WorkspaceFileErrorCode.None,
                null,
                normalizedRelative,
                string.Empty,
                totalSize,
                false,
                IsBinary: true,
                "binary");
        }

        var readCap = Math.Max(1024, maxBytes);
        var buffer = new byte[readCap + 1];
        int bytesRead;
        await using (var fs = new FileStream(fullTarget, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
        }

        var truncated = bytesRead > readCap;
        var sliceLength = truncated ? readCap : bytesRead;
        var slice = buffer.AsSpan(0, sliceLength);

        if (slice.IndexOf((byte)0) >= 0)
        {
            return new WorkspaceFileContentResult(
                true,
                WorkspaceFileErrorCode.None,
                null,
                normalizedRelative,
                string.Empty,
                totalSize,
                false,
                IsBinary: true,
                "binary");
        }

        var content = Encoding.UTF8.GetString(slice);
        return new WorkspaceFileContentResult(
            true,
            WorkspaceFileErrorCode.None,
            null,
            normalizedRelative,
            content,
            totalSize,
            truncated,
            IsBinary: false,
            "utf-8");
    }

    public static async Task<WorkspaceChangesResult> GetChangesAsync(
        string? workspaceRoot,
        IGitService gitService,
        int maxDiffChars = DefaultMaxDiffChars,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gitService);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return new WorkspaceChangesResult(false, null, [], string.Empty, false);
        }

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var gitDir = Path.Combine(fullRoot, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            return new WorkspaceChangesResult(false, null, [], string.Empty, false);
        }

        var headHash = await gitService.GetHeadHashAsync(fullRoot).ConfigureAwait(false);
        // Empty repository with no commits yet uses Git's standard empty-tree SHA so added/untracked changes still appear.
        var baseRef = string.IsNullOrWhiteSpace(headHash)
            ? "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
            : "HEAD";

        var rawFiles = await gitService.GetWorkingTreeChangedFilesAsync(fullRoot, baseRef).ConfigureAwait(false);
        var files = rawFiles.Where(static f => !IsDiffExcludedPath(f.FilePath)).ToArray();

        var diff = await gitService.GetWorkingTreeDiffAsync(fullRoot, baseRef).ConfigureAwait(false);
        diff = FilterInternalMetadataDiffSections(diff);

        var cap = Math.Max(4096, maxDiffChars);
        var truncated = false;
        if (diff.Length > cap)
        {
            diff = string.Concat(diff.AsSpan(0, cap), "\n...[diff truncated]");
            truncated = true;
        }

        return new WorkspaceChangesResult(true, headHash ?? baseRef, files, diff, truncated);
    }

    private static bool IsInternalMetadataPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        foreach (var seg in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (HiddenInternalNames.Contains(seg))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDiffExcludedPath(string? relativePath)
    {
        if (IsInternalMetadataPath(relativePath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        foreach (var seg in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(seg, GatewayAttachmentService.AttachmentDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string FilterInternalMetadataDiffSections(string diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return diff;
        }

        var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var sb = new StringBuilder(diff.Length);
        var sectionLines = new List<string>();
        var sectionInternal = false;
        var inSectionHeader = false;

        void FlushSection()
        {
            if (sectionLines.Count == 0)
            {
                return;
            }

            if (!sectionInternal)
            {
                foreach (var sectionLine in sectionLines)
                {
                    sb.AppendLine(sectionLine);
                }
            }

            sectionLines.Clear();
            sectionInternal = false;
            inSectionHeader = false;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushSection();
                inSectionHeader = true;
                sectionInternal = DiffHeaderTouchesInternalMetadata(line);
                sectionLines.Add(line);
                continue;
            }

            if (inSectionHeader)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    inSectionHeader = false;
                }
                else if (SectionHeaderLineTouchesInternalMetadata(line))
                {
                    sectionInternal = true;
                }
            }

            sectionLines.Add(line);
        }

        FlushSection();
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static bool DiffHeaderTouchesInternalMetadata(string headerLine)
    {
        var rest = headerLine.AsSpan("diff --git ".Length).Trim();
        if (rest.IsEmpty)
        {
            return false;
        }

        // Handle quoted git headers: "a/..." "b/..." or mixed quoted/unquoted.
        if (rest.Contains('"'))
        {
            var tokens = SplitDiffGitQuotedHeader(rest.ToString());
            foreach (var token in tokens)
            {
                if (IsDiffExcludedPath(StripDiffPrefix(UnescapeGitPath(token))))
                {
                    return true;
                }
            }

            return false;
        }

        // Unquoted format: check every whitespace split boundary to support standard a/b,
        // mnemonic (i/w, c/o, etc.) and diff.noprefix=true headers safely.
        var str = rest.ToString();
        for (var idx = str.IndexOf(' '); idx >= 0; idx = str.IndexOf(' ', idx + 1))
        {
            var rawLhs = str[..idx].Trim();
            var rawRhs = str[(idx + 1)..].Trim();
            if (rawLhs.Length == 0 || rawRhs.Length == 0)
            {
                continue;
            }

            var lhs = StripDiffPrefix(rawLhs);
            var rhs = StripDiffPrefix(rawRhs);
            if (string.Equals(lhs, rhs, StringComparison.Ordinal)
                || (HasKnownDiffPrefix(rawLhs) && HasKnownDiffPrefix(rawRhs)))
            {
                if (IsDiffExcludedPath(lhs) || IsDiffExcludedPath(rhs))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasKnownDiffPrefix(string path)
        => path.Length >= 2 && path[1] == '/' && path[0] is 'a' or 'b' or 'c' or 'i' or 'o' or 'w';

    private static string StripDiffPrefix(string path)
        => HasKnownDiffPrefix(path) ? path[2..] : path;

    private static bool SectionHeaderLineTouchesInternalMetadata(string line)
    {
        string? candidate = null;
        if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            candidate = line[4..];
        }
        else if (line.StartsWith("rename from ", StringComparison.Ordinal))
        {
            candidate = line["rename from ".Length..];
        }
        else if (line.StartsWith("rename to ", StringComparison.Ordinal))
        {
            candidate = line["rename to ".Length..];
        }
        else if (line.StartsWith("copy from ", StringComparison.Ordinal))
        {
            candidate = line["copy from ".Length..];
        }
        else if (line.StartsWith("copy to ", StringComparison.Ordinal))
        {
            candidate = line["copy to ".Length..];
        }

        if (string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "/dev/null", StringComparison.Ordinal))
        {
            return false;
        }

        // Git may append a tab before timestamp on some diffs or surround with quotes.
        var tabIdx = candidate.IndexOf('\t');
        if (tabIdx >= 0)
        {
            candidate = candidate[..tabIdx];
        }

        var unescaped = UnescapeGitPath(candidate.Trim());
        var stripped = StripDiffPrefix(unescaped);
        return IsInternalMetadataPath(stripped);
    }

    private static string[] SplitDiffGitQuotedHeader(string rest)
    {
        var results = new List<string>(2);
        var i = 0;
        while (i < rest.Length)
        {
            while (i < rest.Length && rest[i] == ' ') i++;
            if (i >= rest.Length) break;

            if (rest[i] == '"')
            {
                var start = i;
                i++;
                while (i < rest.Length)
                {
                    if (rest[i] == '\\' && i + 1 < rest.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if (rest[i] == '"')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                results.Add(rest[start..i]);
            }
            else
            {
                var nextQuote = rest.IndexOf(" \"", i, StringComparison.Ordinal);
                var end = nextQuote >= 0 ? nextQuote : rest.Length;
                results.Add(rest[i..end]);
                i = end;
            }
        }

        return results.ToArray();
    }

    private static string UnescapeGitPath(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
        {
            var inner = token[1..^1];
            var bytes = new List<byte>(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    var next = inner[++i];
                    switch (next)
                    {
                        case '\\': bytes.Add((byte)'\\'); break;
                        case '"': bytes.Add((byte)'"'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case >= '0' and <= '7' when i + 2 < inner.Length
                                                    && inner[i + 1] is >= '0' and <= '7'
                                                    && inner[i + 2] is >= '0' and <= '7':
                            var octal = Convert.ToByte(inner.Substring(i, 3), 8);
                            bytes.Add(octal);
                            i += 2;
                            break;
                        default:
                            bytes.Add((byte)next);
                            break;
                    }
                }
                else
                {
                    var charLen = char.IsHighSurrogate(inner[i]) && i + 1 < inner.Length ? 2 : 1;
                    bytes.AddRange(Encoding.UTF8.GetBytes(inner.Substring(i, charLen)));
                    i += charLen - 1;
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        return token;
    }

    internal static bool TryResolveWorkspaceRelativePath(
        string? workspaceRoot,
        string? relativePath,
        bool allowEmpty,
        out string fullRoot,
        out string fullTarget,
        out string normalizedRelative,
        out WorkspaceFileErrorCode errorCode,
        out string? errorMessage)
    {
        fullRoot = string.Empty;
        fullTarget = string.Empty;
        normalizedRelative = string.Empty;
        errorCode = WorkspaceFileErrorCode.None;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            errorCode = WorkspaceFileErrorCode.WorkspaceUnavailable;
            errorMessage = "Session workspace directory does not exist.";
            return false;
        }

        fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));

        var raw = (relativePath ?? string.Empty).Trim();
        if (raw.Length == 0 || raw == ".")
        {
            if (!allowEmpty)
            {
                errorCode = WorkspaceFileErrorCode.InvalidPath;
                errorMessage = "File path is required.";
                return false;
            }

            fullTarget = fullRoot;
            normalizedRelative = string.Empty;
            return true;
        }

        if (raw.IndexOf('\0') >= 0 ||
            Path.IsPathRooted(raw) ||
            raw.StartsWith('/') ||
            raw.StartsWith('\\') ||
            (raw.Length >= 2 && raw[1] == ':'))
        {
            errorCode = WorkspaceFileErrorCode.PathTraversal;
            errorMessage = "Absolute paths and drive-prefixed paths are not allowed.";
            return false;
        }

        var segments = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "." || seg == "..")
            {
                errorCode = WorkspaceFileErrorCode.PathTraversal;
                errorMessage = "Relative traversal segments ('..') are not allowed.";
                return false;
            }

            // Windows canonicalization silently strips trailing dots and spaces from path segments
            // (e.g. ".git." -> ".git"), which must never bypass hidden internal metadata blocks.
            if (seg.EndsWith('.') || seg.EndsWith(' '))
            {
                errorCode = WorkspaceFileErrorCode.PathTraversal;
                errorMessage = "Trailing dots or spaces in path segments are not allowed.";
                return false;
            }

            if (HiddenInternalNames.Contains(seg))
            {
                errorCode = WorkspaceFileErrorCode.PathTraversal;
                errorMessage = "Access to internal metadata directories is not allowed.";
                return false;
            }
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, fullRoot, PathComparison) &&
            !candidate.StartsWith(prefix, PathComparison))
        {
            errorCode = WorkspaceFileErrorCode.PathTraversal;
            errorMessage = "Resolved path escapes the workspace root.";
            return false;
        }

        var canonicalRelative = Path.GetRelativePath(fullRoot, candidate).Replace('\\', '/');
        var canonicalSegments = canonicalRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var cseg in canonicalSegments)
        {
            if (cseg == "." || cseg == ".." || HiddenInternalNames.Contains(cseg))
            {
                errorCode = WorkspaceFileErrorCode.PathTraversal;
                errorMessage = "Access to internal metadata directories is not allowed.";
                return false;
            }
        }

        // Check every intermediate and final segment inside workspaceRoot for symlinks / junctions / reparse points.
        var current = fullRoot;
        foreach (var seg in canonicalSegments)
        {
            current = Path.Combine(current, seg);
            if (Directory.Exists(current))
            {
                var di = new DirectoryInfo(current);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null)
                {
                    errorCode = WorkspaceFileErrorCode.LinkedPath;
                    errorMessage = "Symbolic links and junctions inside the workspace cannot be traversed.";
                    return false;
                }
            }
            else if (File.Exists(current))
            {
                var fi = new FileInfo(current);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget is not null)
                {
                    errorCode = WorkspaceFileErrorCode.LinkedPath;
                    errorMessage = "Symbolic links inside the workspace cannot be read.";
                    return false;
                }
            }
        }

        fullTarget = candidate;
        normalizedRelative = string.Join('/', canonicalSegments);
        return true;
    }
}
