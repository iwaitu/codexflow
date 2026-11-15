// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Builds the private snapshot from an immutable git commit: <c>git ls-tree -r -z</c> for the file list and
/// <c>git cat-file --batch</c> for the raw blob bytes, each blob re-hashed on the host and compared with its object id.
/// There is no checkout and no <c>git archive</c>, so smudge/clean filters, hooks, export-ignore/export-subst,
/// worktree attributes and eol conversion can never alter the content, and replace refs are disabled so the object
/// graph cannot be redirected. Links (120000), submodules (160000), special modes, unsafe or duplicate paths and
/// quota overruns refuse the snapshot rather than being dropped, because verifying "commit X" must cover all of X.
/// </summary>
public static class PinnedCommitSnapshotBuilder
{
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsFullCommitId(string? value) => !string.IsNullOrEmpty(value) && FullSha.IsMatch(value);

    public static WorkspaceSnapshot Build(string repositoryPath, string commitId, string tarPath, ExecutionResourceLimits limits, string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);
        ArgumentNullException.ThrowIfNull(commitId);
        ArgumentNullException.ThrowIfNull(tarPath);
        ArgumentNullException.ThrowIfNull(limits);
        if (!IsFullCommitId(commitId))
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.InvalidTrustedContext, "BaseCommit must be a full commit object id when pinning is requested.");
        }

        var repo = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(repo))
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Repository path not found: {repo}");
        }

        var resolved = RunGit(gitExecutable, repo, ["rev-parse", "--verify", "--end-of-options", commitId + "^{commit}"], out var verifyErr, out var code);
        if (code != 0 || !string.Equals(resolved.Trim(), commitId, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Commit {commitId} is not available in repository: {verifyErr.Trim()}");
        }

        var hostTree = RunGit(gitExecutable, repo, ["rev-parse", "--verify", "--end-of-options", commitId + "^{tree}"], out _, out var treeCode).Trim();
        var entries = ListTree(gitExecutable, repo, commitId);
        var baseline = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        if (entries.Count > limits.MaxFiles)
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceTooLarge, "commit exceeds staging file quota");
        }

        foreach (var e in entries)
        {
            if (e.Mode is not ("100644" or "100755") || e.Type != "blob")
            {
                var reason = e.Mode == "120000" ? ExecutionReasonCodes.ExportRejectedLink : ExecutionReasonCodes.ExportRejectedSpecialFile;
                throw new WorkspaceSnapshotException(reason, $"commit contains unsupported entry (mode {e.Mode}, type {e.Type}): {e.Path}");
            }

            if (!WorkspacePath.TryNormalizeStrict(e.Path, out var norm, out var why) || !string.Equals(norm, e.Path, StringComparison.Ordinal))
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.ExportRejectedUnsafePath, $"unsafe path in commit '{e.Path}': {why}");
            }

            if (!seen.Add(norm))
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.ExportRejectedUnsafePath, $"case-insensitive duplicate path in commit: {norm}");
            }
        }

        using var outFs = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new TarWriter(outFs, TarEntryFormat.Gnu, leaveOpen: true);
        WriteDirectory(writer, "workspace");

        long total = 0;
        foreach (var (entry, bytes) in ReadBlobs(gitExecutable, repo, entries, limits))
        {
            total += bytes.Length;
            if (total > limits.MaxWorkspaceBytes)
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceTooLarge, "commit exceeds staging byte quota");
            }

            var parts = entry.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var dir = string.Join('/', parts.Take(i));
                if (directories.Add(dir))
                {
                    WriteDirectory(writer, "workspace/" + dir);
                }
            }

            baseline[entry.Path] = new BaselineEntry(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "workspace/" + entry.Path)
            {
                Mode = (UnixFileMode)Convert.ToInt32(entry.Mode == "100755" ? "755" : "644", 8),
                Uid = WorkspaceSnapshotBuilder.GuestUid,
                Gid = WorkspaceSnapshotBuilder.GuestUid,
                DataStream = new MemoryStream(bytes)
            });
        }

        return new WorkspaceSnapshot { TarPath = tarPath, Baseline = baseline, TotalBytes = total, HostTreeId = treeCode == 0 ? hostTree : null };
    }

    private sealed record TreeEntry(string Mode, string Type, string Oid, string Path);

    private static List<TreeEntry> ListTree(string git, string repo, string commit)
    {
        var raw = RunGitBytes(git, repo, ["ls-tree", "-r", "-z", "--full-tree", "--end-of-options", commit], out var err, out var code);
        if (code != 0)
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, "git ls-tree failed: " + err.Trim());
        }

        var result = new List<TreeEntry>();
        foreach (var record in Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            var meta = tab > 0 ? record[..tab].Split(' ') : [];
            if (meta.Length != 3)
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.ExportRejectedUnsafePath, "unparseable ls-tree record");
            }

            result.Add(new TreeEntry(meta[0], meta[1], meta[2], record[(tab + 1)..]));
        }

        return result;
    }

    private static IEnumerable<(TreeEntry Entry, byte[] Bytes)> ReadBlobs(string git, string repo, List<TreeEntry> entries, ExecutionResourceLimits limits)
    {
        if (entries.Count == 0)
        {
            yield break;
        }

        var psi = NewGitStartInfo(git, repo, ["cat-file", "--batch"]);
        psi.RedirectStandardInput = true;
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.IsolationUnavailable, "git is not available: " + ex.Message);
        }

        var stderr = p.StandardError.ReadToEndAsync();
        var feeder = Task.Run(() =>
        {
            try
            {
                foreach (var e in entries)
                {
                    p.StandardInput.Write(e.Oid + "\n");
                }

                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // reader side failed; surfaced below
            }
        });

        var stdout = p.StandardOutput.BaseStream;
        try
        {
            foreach (var entry in entries)
            {
                var header = ReadLine(stdout);
                var parts = header.Split(' ');
                if (parts.Length != 3 || parts[1] != "blob" || !long.TryParse(parts[2], out var size) || size < 0 ||
                    !string.Equals(parts[0], entry.Oid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"unexpected cat-file answer for {entry.Path}: {header}");
                }

                if (size > limits.MaxWorkspaceBytes)
                {
                    throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceTooLarge, $"blob too large: {entry.Path}");
                }

                var bytes = new byte[size];
                stdout.ReadExactly(bytes);
                if (stdout.ReadByte() != '\n')
                {
                    throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, "cat-file framing error");
                }

                VerifyObjectId(entry, bytes);
                yield return (entry, bytes);
            }
        }
        finally
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // already gone
            }

            feeder.GetAwaiter().GetResult();
            p.WaitForExit();
            _ = stderr.GetAwaiter().GetResult();
        }
    }

    private static void VerifyObjectId(TreeEntry entry, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var all = new byte[header.Length + content.Length];
        header.CopyTo(all, 0);
        content.CopyTo(all, header.Length);
#pragma warning disable CA5350 // SHA-1 is git's own object-id algorithm; used only to re-derive the id of the object we read, not as a security primitive here.
        var actual = entry.Oid.Length == 40 ? Convert.ToHexString(SHA1.HashData(all)) : Convert.ToHexString(SHA256.HashData(all));
#pragma warning restore CA5350
        if (!string.Equals(actual, entry.Oid, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"object content does not match its id for {entry.Path}");
        }
    }

    private static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n')
            {
                return sb.ToString();
            }

            sb.Append((char)b);
            if (sb.Length > 512)
            {
                break;
            }
        }

        throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, "cat-file ended unexpectedly");
    }

    private static void WriteDirectory(TarWriter writer, string name)
        => writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, name)
        {
            Mode = (UnixFileMode)Convert.ToInt32("755", 8),
            Uid = WorkspaceSnapshotBuilder.GuestUid,
            Gid = WorkspaceSnapshotBuilder.GuestUid
        });

    private static ProcessStartInfo NewGitStartInfo(string git, string repo, string[] args)
    {
        var nul = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var psi = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = nul;
        psi.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        psi.Environment["GIT_NO_LAZY_FETCH"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment.Remove("GIT_ALTERNATE_OBJECT_DIRECTORIES");
        psi.Environment.Remove("GIT_REPLACE_REF_BASE");
        foreach (var a in new[] { "--no-replace-objects", "-C", repo, "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + nul, "-c", "protocol.allow=never" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static string RunGit(string git, string repo, string[] args, out string stderr, out int exitCode)
        => Encoding.UTF8.GetString(RunGitBytes(git, repo, args, out stderr, out exitCode));

    private static byte[] RunGitBytes(string git, string repo, string[] args, out string stderr, out int exitCode)
    {
        using var p = new Process { StartInfo = NewGitStartInfo(git, repo, args) };
        try
        {
            p.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            stderr = ex.Message;
            exitCode = -1;
            return [];
        }

        var errTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        stderr = errTask.GetAwaiter().GetResult();
        exitCode = p.ExitCode;
        return ms.ToArray();
    }
}
