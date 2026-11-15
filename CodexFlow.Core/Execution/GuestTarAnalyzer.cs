// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Formats.Tar;
using System.Security.Cryptography;

namespace CodexFlow.Core.Execution;

public enum GuestChangeKind
{
    Add,
    Modify,
    Delete,

    /// <summary>Directory creation (structured tools only; the guest tar analyzer never produces it).</summary>
    Mkdir
}

public sealed record GuestChange(string Path, GuestChangeKind Kind, string? StagedFile, string? Sha256, long Size);

public sealed record GuestTarAnalysis(
    IReadOnlyList<GuestChange> Changes,
    IReadOnlyList<ExportRejection> Rejections,
    int IgnoredGeneratedEntries);

/// <summary>
/// Analyses the guest's /workspace tar stream WITHOUT extracting it to the host file system. File contents are only
/// written to host-named (numbered) staging files after they pass every check; guest names never become host paths here.
/// </summary>
public static class GuestTarAnalyzer
{
    public const int MaxEvidenceFiles = 50;
    public const long MaxEvidenceFileBytes = 2L * 1024 * 1024;
    public const long MaxEvidenceTotalBytes = 16L * 1024 * 1024;
    private const long MaxSourceFileBytes = 16L * 1024 * 1024;

    public static GuestTarAnalysis Analyze(
        Stream tarStream,
        IReadOnlyDictionary<string, BaselineEntry> baseline,
        ExportScope scope,
        ExecutionResourceLimits limits,
        string stagingDirectory)
    {
        ArgumentNullException.ThrowIfNull(tarStream);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(limits);

        var changes = new List<GuestChange>();
        var rejections = new List<ExportRejection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baselineCi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in baseline.Keys)
        {
            baselineCi[key] = key;
        }

        var ignored = 0;
        long stagedBytes = 0;
        var counter = 0;
        stagingDirectory = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        using var reader = new TarReader(tarStream, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
            {
                continue;
            }

            var raw = entry.Name;
            if (raw is "workspace" or "workspace/" or "./workspace" || raw == "./")
            {
                continue;
            }

            if (!raw.StartsWith("workspace/", StringComparison.Ordinal))
            {
                rejections.Add(new ExportRejection(raw, ExecutionReasonCodes.ExportRejectedUnsafePath, "entry outside /workspace"));
                continue;
            }

            var rel = raw["workspace/".Length..];
            if (rel.Contains("..", StringComparison.Ordinal) && rel.Split('/').Contains(".."))
            {
                rejections.Add(new ExportRejection(raw, ExecutionReasonCodes.ExportRejectedUnsafePath, "parent traversal"));
                continue;
            }

            if (rel.Contains('\\', StringComparison.Ordinal))
            {
                rejections.Add(new ExportRejection(raw, ExecutionReasonCodes.ExportRejectedUnsafePath, "backslash in path"));
                continue;
            }

            // Generated output (bin/obj/node_modules/TestResults) stays private to the guest and is never exported.
            if (rel.TrimEnd('/').Split('/').Any(WorkspacePath.IsGeneratedSegment))
            {
                ignored++;
                continue;
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                if (!WorkspacePath.TryNormalizeStrict(rel, out var dirNorm, out var dirReason))
                {
                    rejections.Add(new ExportRejection(raw, ExecutionReasonCodes.ExportRejectedUnsafePath, dirReason));
                }
                else if (WorkspacePath.IsProtected(dirNorm))
                {
                    rejections.Add(new ExportRejection(dirNorm, ExecutionReasonCodes.ExportRejectedProtectedPath, "protected directory created"));
                }

                continue;
            }

            if (!WorkspacePath.TryNormalizeStrict(rel, out var path, out var reason))
            {
                rejections.Add(new ExportRejection(raw, ExecutionReasonCodes.ExportRejectedUnsafePath, reason));
                continue;
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedLink, "link entries are never exported"));
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedSpecialFile, $"special file type {entry.EntryType}"));
                continue;
            }

            if (WorkspacePath.IsProtected(path))
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedProtectedPath, "protected path"));
                continue;
            }

            if (!seen.Add(path))
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedUnsafePath, "case-insensitive duplicate path"));
                continue;
            }

            baselineCi.TryGetValue(path, out var baselineKey);
            if (baselineKey is not null && !string.Equals(baselineKey, path, StringComparison.Ordinal))
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedUnsafePath, "case-changing path collides with existing file"));
                continue;
            }

            var length = entry.Length;
            var data = entry.DataStream;
            string sha;
            string? staged = null;
            if (data is null)
            {
                sha = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty));
            }
            else
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var cap = Math.Min(MaxSourceFileBytes, limits.MaxExportBytes);
                var keep = baselineKey is null || baseline[baselineKey].Size != length || length <= cap;
                var buffer = new byte[81920];
                FileStream? sink = null;
                try
                {
                    // Stage only if it can still turn out to be an exportable change; decided after hashing.
                    if (length <= cap && stagedBytes + length <= limits.MaxExportBytes)
                    {
                        staged = Path.Combine(stagingDirectory, $"c{counter++}.bin");
                        sink = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }

                    int read;
                    while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        sink?.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    sink?.Dispose();
                }

                _ = keep;
                sha = Convert.ToHexString(hasher.GetHashAndReset());
            }

            if (baselineKey is not null && string.Equals(baseline[baselineKey].Sha256, sha, StringComparison.Ordinal))
            {
                DeleteQuiet(staged);
                continue;
            }

            if (staged is null)
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedQuota, "changed file exceeds export size quota"));
                continue;
            }

            stagedBytes += length;
            changes.Add(new GuestChange(path, baselineKey is null ? GuestChangeKind.Add : GuestChangeKind.Modify, staged, sha, length));
            if (changes.Count > limits.MaxExportFiles)
            {
                rejections.Add(new ExportRejection(path, ExecutionReasonCodes.ExportRejectedQuota, "too many changed files"));
                break;
            }
        }

        foreach (var kv in baseline)
        {
            if (!seen.Contains(kv.Key))
            {
                changes.Add(new GuestChange(kv.Key, GuestChangeKind.Delete, null, null, 0));
            }
        }

        foreach (var change in changes)
        {
            if (!scope.Allows(change.Path))
            {
                rejections.Add(new ExportRejection(change.Path, ExecutionReasonCodes.ExportRejectedOutOfScope, $"{change.Kind} outside authorized scope"));
            }
        }

        return new GuestTarAnalysis(changes, rejections, ignored);
    }

    /// <summary>Bounded read of regular files matching globs; links and special files are skipped.</summary>
    public static IReadOnlyList<EvidenceFile> ReadEvidence(Stream tarStream, IReadOnlyList<string> globs)
    {
        ArgumentNullException.ThrowIfNull(tarStream);
        ArgumentNullException.ThrowIfNull(globs);
        var patterns = globs.Select(WorkspacePath.GlobToRegex).ToList();
        var result = new List<EvidenceFile>();
        long total = 0;
        using var reader = new TarReader(tarStream, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                !entry.Name.StartsWith("workspace/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!WorkspacePath.TryNormalizeStrict(entry.Name["workspace/".Length..], out var path, out _) ||
                WorkspacePath.IsProtected(path) ||
                !patterns.Any(p => p.IsMatch(path)))
            {
                continue;
            }

            if (result.Count >= MaxEvidenceFiles || entry.Length > MaxEvidenceFileBytes || total + entry.Length > MaxEvidenceTotalBytes)
            {
                continue;
            }

            using var ms = new MemoryStream();
            entry.DataStream?.CopyTo(ms);
            var bytes = ms.ToArray();
            total += bytes.Length;
            result.Add(new EvidenceFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), bytes));
        }

        return result;
    }

    private static void DeleteQuiet(string? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // staging dir is removed with the session anyway
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
