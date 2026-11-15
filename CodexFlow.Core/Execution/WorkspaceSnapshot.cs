// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Formats.Tar;
using System.Security.Cryptography;

namespace CodexFlow.Core.Execution;

public sealed record BaselineEntry(string Sha256, long Size);

public sealed class WorkspaceSnapshot
{
    public required string TarPath { get; init; }
    public required IReadOnlyDictionary<string, BaselineEntry> Baseline { get; init; }
    public int SkippedLinks { get; init; }
    public int SkippedExcluded { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>Tree id of the host commit this snapshot was built from (pinned snapshots only).</summary>
    public string? HostTreeId { get; init; }
}

public sealed class WorkspaceSnapshotException : Exception
{
    public WorkspaceSnapshotException()
        : this(ExecutionReasonCodes.IsolationUnavailable, "workspace snapshot failed")
    {
    }

    public WorkspaceSnapshotException(string message)
        : this(ExecutionReasonCodes.IsolationUnavailable, message)
    {
    }

    public WorkspaceSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = ExecutionReasonCodes.IsolationUnavailable;
    }

    public WorkspaceSnapshotException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Builds a private tar copy of a host worktree. Links/reparse points, ".git" (dir or pointer file), generated
/// output and credential-like files are never copied. Guest uid/gid is fixed so a non-root container user can write.
/// </summary>
public static class WorkspaceSnapshotBuilder
{
    public const int GuestUid = 10001;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj"
    };

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".env", ".env.local", ".env.production", ".npmrc", ".pypirc", "nuget.config.user"
    };

    public static WorkspaceSnapshot Build(string workspaceRoot, string tarPath, ExecutionResourceLimits limits, string guestRoot = "workspace")
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(guestRoot);
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Workspace root not found: {root}");
        }

        var baseline = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        long total = 0;
        var files = 0;
        var links = 0;
        var excluded = 0;

        using var fs = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var writer = new TarWriter(fs, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var prefix = string.Empty;
            foreach (var part in guestRoot.Split('/'))
            {
                prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                WriteDirectory(writer, prefix);
            }
            var pending = new Stack<(string Full, string Rel)>();
            pending.Push((root, string.Empty));
            while (pending.Count > 0)
            {
                var (dir, rel) = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Cannot enumerate {dir}: {ex.Message}");
                }

                foreach (var child in children)
                {
                    var childRel = rel.Length == 0 ? child.Name : rel + "/" + child.Name;
                    if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        links++;
                        continue;
                    }

                    if (!WorkspacePath.TryNormalizeStrict(childRel, out var norm, out _))
                    {
                        excluded++;
                        continue;
                    }

                    if (child is DirectoryInfo)
                    {
                        if (ExcludedDirectories.Contains(child.Name))
                        {
                            excluded++;
                            continue;
                        }

                        WriteDirectory(writer, guestRoot + "/" + norm);
                        pending.Push((child.FullName, norm));
                        continue;
                    }

                    if (ExcludedFiles.Contains(child.Name))
                    {
                        excluded++;
                        continue;
                    }

                    var info = (FileInfo)child;
                    if (++files > limits.MaxFiles || total + info.Length > limits.MaxWorkspaceBytes)
                    {
                        throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceTooLarge,
                            $"Workspace exceeds staging quota (files<={limits.MaxFiles}, bytes<={limits.MaxWorkspaceBytes}).");
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(info.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Cannot read {childRel}: {ex.Message}");
                    }

                    total += bytes.Length;
                    baseline[norm] = new BaselineEntry(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
                    var entry = new GnuTarEntry(TarEntryType.RegularFile, guestRoot + "/" + norm)
                    {
                        Mode = (UnixFileMode)Convert.ToInt32("644", 8),
                        Uid = GuestUid,
                        Gid = GuestUid,
                        DataStream = new MemoryStream(bytes)
                    };
                    writer.WriteEntry(entry);
                }
            }
        }

        return new WorkspaceSnapshot
        {
            TarPath = tarPath,
            Baseline = baseline,
            SkippedLinks = links,
            SkippedExcluded = excluded,
            TotalBytes = total
        };
    }

    private static void WriteDirectory(TarWriter writer, string name)
    {
        writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, name)
        {
            Mode = (UnixFileMode)Convert.ToInt32("755", 8),
            Uid = GuestUid,
            Gid = GuestUid
        });
    }
}
