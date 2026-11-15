// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text;
using CodexFlow.Contracts;

namespace CodexFlow.Core.Services;

/// <summary>Checks trusted worker manifests against Git objects, without running repository code.</summary>
public sealed class GitArtifactInspector
{
    private static readonly string[] SafeGitOptions = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.quotepath=false", "-c", "diff.renames=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null")];
    public static string ManifestHash(ForgeArtifactManifest manifest) =>
        ArtifactQualityGate.ComputeManifestHash(manifest);

    public async Task<string> GetRepositoryIdentityAsync(string path, CancellationToken ct = default)
    {
        var common = await GitAsync(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct).ConfigureAwait(false);
        var full = Path.GetFullPath(common.Trim());
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public async Task<string?> ValidateAsync(ForgeArtifactManifest manifest, string authorizedRepository,
        string userId, string sessionId, string jobId, int attempt,
        IReadOnlyList<ForgeArtifactManifest> requiredParents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requiredParents);
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.ArtifactId) ||
            string.IsNullOrWhiteSpace(manifest.ScopeHash) || manifest.CreatedAtUtc == default ||
            manifest.ParentArtifactIds is null || manifest.ChangedPaths is null ||
            !IsCommit(manifest.BaseCommit) || !IsCommit(manifest.HeadCommit)) return "LegacyUnverified";
        if (!string.Equals(manifest.UserId, userId, StringComparison.Ordinal) ||
            !string.Equals(manifest.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(manifest.JobId, jobId, StringComparison.Ordinal) || manifest.Attempt != attempt)
            return "ArtifactOwnershipMismatch";
        if (!Directory.Exists(manifest.WorktreePath)) return "ArtifactMissing";
        try
        {
            var trustedIdentity = await GetRepositoryIdentityAsync(authorizedRepository, ct).ConfigureAwait(false);
            if (!string.Equals(manifest.RepositoryIdentity, trustedIdentity, StringComparison.Ordinal) ||
                !string.Equals(await GetRepositoryIdentityAsync(manifest.WorktreePath, ct).ConfigureAwait(false), trustedIdentity, StringComparison.Ordinal))
                return "RepositoryIdentityMismatch";
            var top = (await GitAsync(manifest.WorktreePath, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim();
            if (!string.Equals(Path.GetFullPath(top), Path.GetFullPath(manifest.WorktreePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return "ArtifactRootMismatch";
            var head = (await GitAsync(manifest.WorktreePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
            if (!string.Equals(head, manifest.HeadCommit, StringComparison.Ordinal)) return "ArtifactHeadChanged";
            if (!string.IsNullOrWhiteSpace(await GitAsync(manifest.WorktreePath,
                    ["status", "--porcelain=v1", "--untracked-files=all"], ct).ConfigureAwait(false))) return "ArtifactDirty";
            await GitAsync(manifest.WorktreePath, ["merge-base", "--is-ancestor", manifest.BaseCommit, head], ct).ConfigureAwait(false);
            foreach (var parent in requiredParents)
            {
                if (!IsCommit(parent.HeadCommit) || !IsCommit(parent.BaseCommit) || parent.ChangedPaths is null ||
                    !string.Equals(parent.RepositoryIdentity, trustedIdentity, StringComparison.Ordinal) ||
                    !string.Equals(parent.SessionId, sessionId, StringComparison.Ordinal) ||
                    !string.Equals(parent.UserId, userId, StringComparison.Ordinal) ||
                    !manifest.ParentArtifactIds.Contains(parent.ArtifactId, StringComparer.Ordinal))
                    return "ArtifactParentMismatch";
                await GitAsync(manifest.WorktreePath, ["merge-base", "--is-ancestor", parent.HeadCommit, head], ct).ConfigureAwait(false);
                await GitAsync(manifest.WorktreePath, ["merge-base", "--is-ancestor", parent.BaseCommit, parent.HeadCommit], ct).ConfigureAwait(false);
                var parentChanges = (await GitAsync(manifest.WorktreePath,
                        ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", parent.BaseCommit, parent.HeadCommit, "--"], ct).ConfigureAwait(false))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
                if (!parentChanges.SequenceEqual(parent.ChangedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    return "ArtifactParentChangesMismatch";
            }
            var changes = (await GitAsync(manifest.WorktreePath,
                    ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", manifest.BaseCommit, head, "--"], ct).ConfigureAwait(false))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
            if (!changes.SequenceEqual(manifest.ChangedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return "ArtifactChangesMismatch";
            return await ValidateChainCoverageAsync(manifest, head, requiredParents, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return "ArtifactGitVerificationFailed";
        }
    }

    /// <summary>
    /// Every path changed between the chain's starting commit and the final HEAD must be named by some artifact of the chain.
    /// The model audit reads the final content of exactly <c>parents ∪ manifest</c> ChangedPaths, so a commit that landed
    /// between two task artifacts (a manual commit, a leftover of a failed job, ...) would otherwise be verified and shipped
    /// without anyone reviewing it, although each artifact's own diff checks out. Works for linear chains and for
    /// integration commits that merge parallel branches. History before the chain's starting commit is not attributable
    /// to any task and is deliberately out of scope.
    /// </summary>
    private async Task<string?> ValidateChainCoverageAsync(ForgeArtifactManifest manifest, string head,
        IReadOnlyList<ForgeArtifactManifest> requiredParents, CancellationToken ct)
    {
        if (requiredParents.Count == 0) return null; // a single artifact's own diff was already proven equal to ChangedPaths

        var artifacts = requiredParents.Append(manifest).ToArray();
        var chainBase = await FindChainBaseAsync(manifest.WorktreePath, artifacts, ct).ConfigureAwait(false);

        // No artifact's base precedes all the others: the pieces are not one lineage, so coverage cannot be established.
        if (chainBase is null) return "ArtifactChainDiscontinuity";

        var covered = artifacts.SelectMany(artifact => artifact.ChangedPaths).ToHashSet(StringComparer.Ordinal);
        var total = (await GitAsync(manifest.WorktreePath,
                ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", chainBase, head, "--"], ct).ConfigureAwait(false))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return total.Any(path => !covered.Contains(path)) ? "ArtifactChainGap" : null;
    }

    /// <summary>
    /// The commit the chain starts from: the base of the artifact whose base precedes (is an ancestor of) every other artifact's base.
    /// Null when no such base exists, i.e. the artifacts are not one lineage.
    /// </summary>
    public async Task<string?> FindChainBaseAsync(string worktreePath, IReadOnlyList<ForgeArtifactManifest> artifacts, CancellationToken ct = default)
    {
        foreach (var candidate in artifacts.Select(artifact => artifact.BaseCommit).Distinct(StringComparer.Ordinal))
        {
            var startsTheChain = true;
            foreach (var other in artifacts.Where(artifact => !string.Equals(artifact.BaseCommit, candidate, StringComparison.Ordinal)))
            {
                if (!await IsAncestorAsync(worktreePath, candidate, other.BaseCommit, ct).ConfigureAwait(false))
                {
                    startsTheChain = false;
                    break;
                }
            }

            if (startsTheChain) return candidate;
        }

        return null;
    }

    public async Task<bool> IsAncestorAsync(string repository, string ancestor, string descendant, CancellationToken ct = default)
    {
        try
        {
            await GitAsync(repository, ["merge-base", "--is-ancestor", ancestor, descendant], ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // "git merge-base --is-ancestor" exits 1 when it is not
        }
    }

    /// <summary>A worktree as registered in a repository's own metadata (<c>git worktree list --porcelain -z</c>).</summary>
    public sealed record RegisteredWorktree(string Path, string? Branch, bool Prunable, bool Locked);

    /// <summary>
    /// Looks <paramref name="worktreePath"/> up in the registry of the repository at <paramref name="repositoryRoot"/>. The registry
    /// lives inside that repository's <c>.git</c> (written by <c>git worktree add</c>), so a directory with a hand-written
    /// <c>.git</c> file that merely points at the repository is not in it. Returns null when the path is not a registered worktree.
    /// </summary>
    public async Task<RegisteredWorktree?> FindRegisteredWorktreeAsync(
        string repositoryRoot, string worktreePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var output = await GitAsync(repositoryRoot, ["worktree", "list", "--porcelain", "-z"], ct).ConfigureAwait(false);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var wanted = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(worktreePath));

        string? path = null;
        string? branch = null;
        var prunable = false;
        var locked = false;

        RegisteredWorktree? Flush()
        {
            if (path is null)
            {
                return null;
            }

            var candidate = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            return string.Equals(candidate, wanted, comparison) ? new RegisteredWorktree(candidate, branch, prunable, locked) : null;
        }

        // -z: every attribute ends with NUL and every record with an additional empty field.
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (Flush() is { } hit)
                {
                    return hit;
                }

                path = null;
                branch = null;
                prunable = false;
                locked = false;
            }
            else if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = field["worktree ".Length..];
            }
            else if (field.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = field["branch ".Length..];
            }
            else if (field.StartsWith("prunable", StringComparison.Ordinal))
            {
                prunable = true;
            }
            else if (field.StartsWith("locked", StringComparison.Ordinal))
            {
                locked = true;
            }
        }

        return Flush();
    }

    /// <summary>The text a deleted (or renamed-away) path had just before it was removed, read from Git objects.</summary>
    /// <param name="Path">Repository-relative path, as listed in the artifact manifest.</param>
    /// <param name="Content">File text immediately before the deletion.</param>
    /// <param name="DeletedInCommit">The commit in the head's history that removed the path.</param>
    public sealed record DeletedFilePreState(string Path, string Content, string DeletedInCommit);

    /// <summary>
    /// For a path that no longer exists at <paramref name="headCommit"/>, returns what it contained right before the commit that
    /// removed it. A rename is a deletion of the old path plus an addition of the new one, so this also covers renamed-away paths.
    /// Reads Git objects only (no checkout, no repository code). Returns null when the path still exists at head, was never a
    /// regular file in the head's history, or anything cannot be read: callers must treat null as "no evidence", never as "nothing
    /// was deleted".
    /// </summary>
    public async Task<DeletedFilePreState?> TryReadDeletedFilePreStateAsync(
        string worktreePath, string headCommit, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !IsCommit(headCommit) ||
            string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('\n')) return null;
        try
        {
            // Literal pathspecs: a manifest path such as "*.cs" or ":(top)x" must never be interpreted as a pattern.
            var atHead = await GitAsync(worktreePath, ["--literal-pathspecs", "ls-tree", "-z", headCommit, "--", path], ct).ConfigureAwait(false);
            if (atHead.Length > 0) return null; // still present at head: not a deletion

            var deletedIn = (await GitAsync(worktreePath,
                ["--literal-pathspecs", "log", "-n", "1", "--diff-filter=D", "--no-renames", "--no-ext-diff", "--format=%H", headCommit, "--", path], ct)
                .ConfigureAwait(false)).Trim();
            if (!IsCommit(deletedIn)) return null;

            var before = await GitAsync(worktreePath, ["--literal-pathspecs", "ls-tree", "-z", deletedIn + "^", "--", path], ct).ConfigureAwait(false);
            if (!TryParseBlobEntry(before, path, out var objectId)) return null;
            var content = await GitAsync(worktreePath, ["cat-file", "blob", objectId], ct).ConfigureAwait(false);
            return new DeletedFilePreState(path, content, deletedIn);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines (in the head version of a file) that the commits from <paramref name="baseCommit"/> to <paramref name="headCommit"/> added or
    /// modified, keyed by repository-relative path; a pure deletion is the position where the text used to be. Files the change did not touch,
    /// deleted files and names git has to quote are not in the result, which is how a caller tells "unknown" from "touched". Reads Git objects
    /// only. Returns null when anything cannot be read: callers must then treat every line as changed, never as unchanged.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ChangedLineRange>>?> GetChangedLineRangesAsync(
        string worktreePath, string baseCommit, string headCommit, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(worktreePath) || !IsCommit(baseCommit) || !IsCommit(headCommit) || paths.Count == 0 ||
            paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('\n'))) return null;
        try
        {
            var arguments = new List<string> { "--literal-pathspecs", "diff", "-U0", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", baseCommit, headCommit, "--" };
            arguments.AddRange(paths);
            return ParseChangedLineRanges(await GitAsync(worktreePath, arguments, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Pure parser of a <c>git diff -U0</c> text; see <see cref="GetChangedLineRangesAsync"/> for the meaning of an absent path.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ChangedLineRange>> ParseChangedLineRanges(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);
        var result = new Dictionary<string, List<ChangedLineRange>>(StringComparer.Ordinal);
        List<ChangedLineRange>? current = null;
        StringBuilder? before = null; // removed lines of the hunk that is open, i.e. what its range read before the change

        void CloseHunk()
        {
            if (current is { Count: > 0 } && before is { Length: > 0 })
                current[^1] = current[^1] with { Before = before.ToString().TrimEnd('\n') };
            before = null;
        }

        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                CloseHunk();
                current = null;
                continue;
            }

            if (before is null && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var target = line[4..].TrimEnd('\r');
                // /dev/null = deleted file; a leading quote = git escaped the name. Neither can be located reliably: leave the path out.
                current = target.StartsWith("b/", StringComparison.Ordinal) ? result.TryAdd(target[2..], []) ? result[target[2..]] : null : null;
                continue;
            }

            if (before is not null && line.StartsWith('-') && before.Length < ChangedLineRange.MaxBeforeChars)
            {
                before.Append(line.AsSpan(1).TrimEnd('\r')).Append('\n');
                continue;
            }

            if (current is null || !line.StartsWith("@@ ", StringComparison.Ordinal)) continue;
            CloseHunk();
            var plus = line.IndexOf(" +", StringComparison.Ordinal);
            if (plus < 0) continue;
            var spec = line[(plus + 2)..];
            var space = spec.IndexOf(' ', StringComparison.Ordinal);
            if (space >= 0) spec = spec[..space];
            var parts = spec.Split(',');
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start)) continue;
            var count = 1;
            if (parts.Length > 1 && !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)) continue;
            current.Add(count == 0 ? new ChangedLineRange(Math.Max(start, 1), start + 1) : new ChangedLineRange(start, start + count - 1));
            before = new StringBuilder();
        }

        CloseHunk();
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ChangedLineRange>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Parses exactly one <c>git ls-tree -z</c> entry and accepts it only when it is a regular blob at exactly <paramref name="path"/>.</summary>
    private static bool TryParseBlobEntry(string lsTreeOutput, string path, out string objectId)
    {
        objectId = string.Empty;
        var entries = lsTreeOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length != 1) return false;
        var tab = entries[0].IndexOf('\t');
        if (tab < 0) return false;
        var fields = entries[0][..tab].Split(' ');
        if (fields.Length != 3 || fields[1] != "blob" || !IsCommit(fields[2]) ||
            !string.Equals(entries[0][(tab + 1)..], path, StringComparison.Ordinal)) return false;
        objectId = fields[2];
        return true;
    }

    private static bool IsCommit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> GitAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var arg in SafeGitOptions.Concat(arguments))
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Git artifact inspection failed.");
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new IOException("Git inspection output limit exceeded.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

/// <summary>An inclusive line range of a file's head version that a change touched.</summary>
public sealed record ChangedLineRange(int Start, int End)
{
    /// <summary>Upper bound of <see cref="Before"/>; a longer removed text is cut, which only means less context for the reviewer.</summary>
    public const int MaxBeforeChars = 4000;

    /// <summary>
    /// What these lines read before the change (the removed lines of the hunk, as git reports them), or null when the hunk only added lines.
    /// Context for the model audit: without it a weakness that was already on a line cannot be told from one the change put there.
    /// It does not take part in equality: a range is identified by its position.
    /// </summary>
    public string? Before { get; init; }

    public bool Equals(ChangedLineRange? other) => other is not null && Start == other.Start && End == other.End;

    public override int GetHashCode() => HashCode.Combine(Start, End);

    /// <summary>True when <paramref name="start"/>..<paramref name="end"/> overlaps a range or lies within <paramref name="margin"/> lines of it.</summary>
    public static bool Touches(IReadOnlyList<ChangedLineRange> ranges, int start, int end, int margin) =>
        ranges.Any(range => start <= range.End + margin && end >= range.Start - margin);
}
