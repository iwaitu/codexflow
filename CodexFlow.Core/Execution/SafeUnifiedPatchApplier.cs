// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodexFlow.Core.Execution;

public sealed record PatchApplyResult(bool Success, string ReasonCode, string Message, IReadOnlyList<string> ChangedPaths);

/// <summary>
/// Applies a unified diff to a worktree WITHOUT running git against the worktree. The affected files are copied into a
/// private temp directory, <c>git apply</c> runs there (no repository, hardened environment, no links/submodules/binary
/// patches accepted), and the result is published as ONE all-or-nothing batch through the identity-checked writer: every
/// modified/deleted file must still be the version that was read, every added file must still be absent, and the write
/// goes through the pinned/no-follow path of <see cref="SafeTreeWriter"/>. A failed or conflicting patch changes nothing,
/// so there is nothing to "revert" with host git afterwards.
/// </summary>
public static class SafeUnifiedPatchApplier
{
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxFiles = 500;

    public static Task<PatchApplyResult> ApplyAsync(string workspaceRoot, string patch, CancellationToken cancellationToken)
        => ApplyAsync(workspaceRoot, patch, ct: cancellationToken);

    public static async Task<PatchApplyResult> ApplyAsync(string workspaceRoot, string patch, string gitExecutable = "git", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(patch);
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            return Fail(ExecutionReasonCodes.WorkspaceNotFound, "workspace root does not exist");
        }

        var normalizedPatch = patch.EndsWith((char)10) ? patch : patch + (char)10;
        if (!TryCollectPaths(normalizedPatch, out var paths, out var parseError))
        {
            return Fail(ExecutionReasonCodes.InvalidRequest, parseError);
        }

        if (paths.Count == 0 || paths.Count > MaxFiles)
        {
            return Fail(ExecutionReasonCodes.InvalidRequest, paths.Count == 0 ? "the patch names no files" : "the patch touches too many files");
        }

        var temp = Path.Combine(Path.GetTempPath(), "cf-patch-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(temp, "w");
        var patchFile = Path.Combine(temp, "p.diff");
        Directory.CreateDirectory(work);
        try
        {
            // 1. private copies of the files the patch can touch, with the identity of the version that was read
            var before = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rel in paths)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!Agents.Tools.ToolPathResolver.IsSafeWriteTarget(full, root, out var unsafeReason))
                {
                    return Fail(ExecutionReasonCodes.ExportRejectedUnsafePath, $"{rel}: {unsafeReason}");
                }

                if (!File.Exists(full))
                {
                    continue;
                }

                if (new FileInfo(full).Length > MaxFileBytes)
                {
                    return Fail(ExecutionReasonCodes.ExportRejectedQuota, $"{rel}: file too large to patch");
                }

                var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                var copy = Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await File.WriteAllBytesAsync(copy, bytes, ct).ConfigureAwait(false);
                before[rel] = WorkspaceFileWriter.Sha256Hex(bytes);
            }

            await File.WriteAllTextAsync(patchFile, normalizedPatch, new UTF8Encoding(false), ct).ConfigureAwait(false);

            // 2. git apply in the private directory (outside any repository)
            var apply = await RunGitApplyAsync(gitExecutable, work, temp, patchFile, ct).ConfigureAwait(false);
            if (apply.ExitCode != 0)
            {
                return Fail(ExecutionReasonCodes.ExportFailed, "git apply failed: " + Trim(apply.Error));
            }

            // 3. read the result back; anything unexpected (extra files, links) fails closed
            var changes = new List<WorkspaceChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(work, file).Replace(Path.DirectorySeparatorChar, '/');
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return Fail(ExecutionReasonCodes.ExportRejectedLink, $"{rel}: the patch produced a link");
                }

                if (Directory.Exists(file))
                {
                    continue;
                }

                if (!paths.Contains(rel))
                {
                    return Fail(ExecutionReasonCodes.ExportRejectedOutOfScope, $"{rel}: the patch produced a file it did not declare");
                }

                seen.Add(rel);
                var after = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                var afterSha = WorkspaceFileWriter.Sha256Hex(after);
                if (before.TryGetValue(rel, out var beforeSha))
                {
                    if (!string.Equals(beforeSha, afterSha, StringComparison.Ordinal))
                    {
                        changes.Add(new WorkspaceChange(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), after, beforeSha, Delete: false));
                    }
                }
                else
                {
                    changes.Add(new WorkspaceChange(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), after, null, Delete: false));
                }
            }

            foreach (var (rel, beforeSha) in before)
            {
                if (!seen.Contains(rel))
                {
                    changes.Add(new WorkspaceChange(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), null, beforeSha, Delete: true));
                }
            }

            if (changes.Count == 0)
            {
                return new PatchApplyResult(true, ExecutionReasonCodes.Allowed, "the patch changed nothing", []);
            }

            // 4. one all-or-nothing, identity-checked publish
            var published = WorkspaceFileWriter.ApplyBatch(root, changes);
            return published.Success
                ? new PatchApplyResult(true, ExecutionReasonCodes.Allowed, "applied", changes.Select(c => Path.GetRelativePath(root, c.FullPath).Replace(Path.DirectorySeparatorChar, '/')).ToList())
                : Fail(published.ReasonCode, published.Message);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    internal static bool TryCollectPaths(string patch, out HashSet<string> paths, out string error)
    {
        var collected = new HashSet<string>(StringComparer.Ordinal);
        var failure = CollectPaths(patch, collected);
        paths = collected;
        error = failure ?? string.Empty;
        return failure is null;
    }

    /// <summary>Returns null on success, otherwise the reason the patch is refused.</summary>
    private static string? CollectPaths(string patch, HashSet<string> paths)
    {
        string? error = null;

        bool Add(string raw, string what)
        {
            if (raw.StartsWith('"') || raw.Contains((char)92, StringComparison.Ordinal))
            {
                error = $"unsupported quoted/escaped path in {what}";
                return false;
            }

            if (!WorkspacePath.TryNormalizeStrict(raw, out var normalized, out var why) || WorkspacePath.IsProtected(normalized))
            {
                error = $"path '{raw}' in {what} is not allowed: {(string.IsNullOrEmpty(why) ? "protected path" : why)}";
                return false;
            }

            paths.Add(normalized);
            return true;
        }

        foreach (var rawLine in patch.Split((char)10))
        {
            var line = rawLine.TrimEnd((char)13);
            if (line.StartsWith("GIT binary patch", StringComparison.Ordinal) || line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                return "binary patches are not supported";
            }

            if (line.StartsWith("new file mode 12", StringComparison.Ordinal) || line.StartsWith("old mode 12", StringComparison.Ordinal) ||
                line.StartsWith("new mode 12", StringComparison.Ordinal) || line.StartsWith("deleted file mode 12", StringComparison.Ordinal) ||
                line.Contains(" mode 160000", StringComparison.Ordinal))
            {
                return "symlink and submodule changes are not supported";
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var rest = line["diff --git ".Length..];
                var split = rest.LastIndexOf(" b/", StringComparison.Ordinal);
                if (!rest.StartsWith("a/", StringComparison.Ordinal) || split < 2)
                {
                    return "unsupported 'diff --git' header";
                }

                if (!Add(rest[2..split], "diff header") || !Add(rest[(split + 3)..], "diff header"))
                {
                    return error;
                }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var value = line[4..];
                var tab = value.IndexOf((char)9, StringComparison.Ordinal);
                if (tab >= 0)
                {
                    value = value[..tab];
                }

                if (value == "/dev/null")
                {
                    continue;
                }

                if (!(value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)))
                {
                    return "file headers must use the a/ b/ prefix";
                }

                if (!Add(value[2..], "file header"))
                {
                    return error;
                }
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("rename to ", StringComparison.Ordinal) ||
                     line.StartsWith("copy from ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                if (!Add(line[(line.IndexOf(' ', line.IndexOf(' ', StringComparison.Ordinal) + 1) + 1)..], "rename/copy header"))
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Error)> RunGitApplyAsync(string git, string workDir, string ceiling, string patchFile, CancellationToken ct)
    {
        var nul = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var psi = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = nul;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CEILING_DIRECTORIES"] = ceiling;
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_EXTERNAL_DIFF" })
        {
            psi.Environment.Remove(name);
        }

        foreach (var a in new[] { "-c", "core.hooksPath=" + nul, "-c", "core.fsmonitor=false", "-c", "core.autocrlf=false", "-c", "protocol.allow=never", "apply", "--recount", "--whitespace=nowarn", patchFile })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (-1, "git is not available: " + ex.Message);
        }

        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            throw;
        }

        return (process.ExitCode, await stderr.ConfigureAwait(false));
    }

    private static PatchApplyResult Fail(string reason, string message) => new(false, reason, message, []);

    private static string Trim(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim();

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
