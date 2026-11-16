// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents.Tools;

public static class ToolPathResolver
{
    public const string PreferredProjectRootMetadataKey = "PreferredProjectRoot";
    public const string TrustedExternalProjectRootMetadataKey = "TrustedExternalProjectRoot";

    /// <summary>
    /// The host-bound workspace root of the current execution (ambient trusted context), or null outside such a context.
    /// When it exists it is the ONLY physical boundary for structured reads and writes: model-supplied workspace_path /
    /// project_root / absolute paths pointing anywhere else (e.g. the main repository while the job runs in a shadow
    /// worktree) are ignored or refused, never honored.
    /// </summary>
    /// <summary>
    /// True when a host-bound trusted execution context exists for this call, EVEN IF its root is no longer usable.
    /// "No ambient context" (plain library use) and "ambient context whose root is gone/invalid" are different states:
    /// the second one must refuse, never fall back to model-supplied roots.
    /// </summary>
    public static bool HasAmbientTrustedContext
        => new CodexFlow.Core.Execution.TrustedExecutionContextAccessor().Current is not null;

    public static string? TrustedWorkspaceRoot
    {
        get
        {
            var root = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor().Current?.WorkspaceRoot;
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? Path.GetFullPath(root) : null;
        }
    }

    /// <summary>
    /// Joins a model-supplied relative path onto the root and refuses anything that is rooted, drive/UNC qualified or
    /// escapes the root (Path.Combine silently returns the second argument when it is rooted).
    /// </summary>
    public static bool TryCombineWithinRoot(string root, string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var candidate = path.Trim();
        if (Path.IsPathRooted(candidate) && !IsWithinRoot(candidate, root))
        {
            return false;
        }

        var combined = Path.IsPathRooted(candidate) ? Path.GetFullPath(candidate) : Path.GetFullPath(Path.Combine(root, candidate));
        if (!IsSafeReadTarget(combined, root, out _))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }

    public static bool IsCredentialOrProtectedStatePath(string fullPath, string? baseRoot = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        if (CodexFlow.Core.Execution.WorkspacePath.IsProtectedHostPath(fullPath))
        {
            return true;
        }

        var fileName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (fileName.Equals("host.token", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("control.token", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".codexflow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = fullPath.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static seg => seg.Equals(".codexflow", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(baseRoot) && IsWithinRoot(fullPath, baseRoot))
        {
            var rel = Path.GetRelativePath(baseRoot, fullPath).Replace('\\', '/');
            if (rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static seg => seg.Equals(".codexflow", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Authoritative read/list/search guard used by every managed workspace reading and indexing tool:
    /// enforces containment within <paramref name="baseRoot"/>, blocks all <c>.codexflow</c> and host/control token paths,
    /// and resolves any intermediate or leaf symlink/junction/reparse point so aliases cannot escape the workspace or
    /// point into protected credential directories.
    /// </summary>
    public static bool IsSafeReadTarget(string fullPath, string baseRoot, out string reason)
    {
        reason = string.Empty;
        if (!IsWithinRoot(fullPath, baseRoot))
        {
            reason = "Path traversal not allowed.";
            return false;
        }

        // B1 (Round 3): Anchor link and protected-path checks to the ambient TrustedWorkspaceRoot whenever present
        // so narrowing `project_root` to a workspace junction/symlink alias can NEVER bypass ancestor/root link inspection!
        string boundaryRoot;
        if (HasAmbientTrustedContext)
        {
            if (TrustedWorkspaceRoot is not { } trusted || !IsWithinRoot(baseRoot, trusted))
            {
                reason = "Workspace root is outside the ambient trusted execution boundary.";
                return false;
            }

            boundaryRoot = Path.GetFullPath(trusted).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else
        {
            boundaryRoot = Path.GetFullPath(baseRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (IsCredentialOrProtectedStatePath(fullPath, boundaryRoot) ||
            IsCredentialOrProtectedStatePath(baseRoot, boundaryRoot))
        {
            reason = "Access to protected host credential or state path is denied.";
            return false;
        }

        var resolvedBoundaryRoot = CodexFlow.Core.Execution.Approval.AutoInWorkspaceEvaluator.ResolveFinalTargetPath(boundaryRoot) ?? boundaryRoot;
        if (IsCredentialOrProtectedStatePath(resolvedBoundaryRoot))
        {
            reason = "Workspace boundary root resolves to a protected host credential or state path.";
            return false;
        }

        // Check EVERY existing component from fullPath up to AND INCLUDING boundaryRoot (`>=`), plus the deepest existing ancestor's final target!
        var current = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var checkedDeepestExisting = false;
        while (current.Length >= boundaryRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if (!checkedDeepestExisting)
                    {
                        checkedDeepestExisting = true;
                        var deepestResolved = CodexFlow.Core.Execution.Approval.AutoInWorkspaceEvaluator.ResolveFinalTargetPath(current);
                        if (deepestResolved is null ||
                            !IsWithinRoot(deepestResolved, resolvedBoundaryRoot) ||
                            IsCredentialOrProtectedStatePath(deepestResolved, resolvedBoundaryRoot))
                        {
                            reason = "Target path resolves via link or alias outside workspace or into protected credentials.";
                            return false;
                        }
                    }

                    var attributes = File.GetAttributes(current);
                    FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                    {
                        var resolved = CodexFlow.Core.Execution.Approval.AutoInWorkspaceEvaluator.ResolveFinalTargetPath(current);
                        if (resolved is null ||
                            !IsWithinRoot(resolved, resolvedBoundaryRoot) ||
                            IsCredentialOrProtectedStatePath(resolved, resolvedBoundaryRoot))
                        {
                            reason = "Link, junction, or reparse point escaping workspace or targeting protected credentials is denied.";
                            return false;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    reason = "Unable to verify target path safety.";
                    return false;
                }
            }

            if (current.Length == boundaryRoot.Length)
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return true;
    }

    public static string ResolveBaseRoot(string? workspacePath, string? projectRoot)
    {
        if (HasAmbientTrustedContext)
        {
            // The trusted root vanished (e.g. the shadow worktree was removed): refuse, do NOT fall back to the model's roots.
            if (TrustedWorkspaceRoot is not { } trusted)
            {
                return string.Empty;
            }

            // A project_root narrower than the trusted root is accepted ONLY if it passes full link & credential safety against `trusted`.
            return !string.IsNullOrWhiteSpace(projectRoot) &&
                   Directory.Exists(projectRoot) &&
                   IsWithinRoot(projectRoot, trusted) &&
                   IsSafeReadTarget(projectRoot, trusted, out _)
                ? Path.GetFullPath(projectRoot)
                : trusted;
        }

        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            var fullProj = Path.GetFullPath(projectRoot);
            if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
            {
                var fullWs = Path.GetFullPath(workspacePath);
                if (IsWithinRoot(fullProj, fullWs) && !IsSafeReadTarget(fullProj, fullWs, out _))
                {
                    return string.Empty;
                }
            }

            return IsSafeReadTarget(fullProj, fullProj, out _) ? fullProj : string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            var fullWs = Path.GetFullPath(workspacePath);
            return IsSafeReadTarget(fullWs, fullWs, out _) ? fullWs : string.Empty;
        }

        return string.Empty;
    }

    public static string ResolveProjectRoot(
        string? workspacePath,
        string? projectRoot,
        string? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return ResolveProjectRoot(workspacePath, projectRoot, CodexSession.CreateProjectUri(projectUrl), metadata);
    }

    public static string ResolveProjectRoot(
        string? workspacePath,
        string? projectRoot,
        Uri? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (HasAmbientTrustedContext)
        {
            if (TrustedWorkspaceRoot is not { } trusted)
            {
                return string.Empty;
            }

            // Inside a trusted context every root is derived from the trusted root; the caller's workspace/project/metadata
            // roots (which can still point at the main repository) only select a nested repo when they lie inside it.
            if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot) && IsWithinRoot(projectRoot, trusted))
            {
                return Path.GetFullPath(projectRoot);
            }

            projectRoot = null;
            workspacePath = trusted;
            metadata = null;
        }

        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            return Path.GetFullPath(projectRoot);

        if (metadata != null &&
            metadata.TryGetValue(TrustedExternalProjectRootMetadataKey, out var trustedExternalRoot) &&
            !string.IsNullOrWhiteSpace(trustedExternalRoot) &&
            Directory.Exists(trustedExternalRoot))
        {
            return Path.GetFullPath(trustedExternalRoot);
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return string.Empty;

        var normalizedWorkspace = Path.GetFullPath(workspacePath);

        if (metadata != null &&
            metadata.TryGetValue(PreferredProjectRootMetadataKey, out var preferredRoot) &&
            !string.IsNullOrWhiteSpace(preferredRoot) &&
            Directory.Exists(preferredRoot))
        {
            var normalizedPreferred = Path.GetFullPath(preferredRoot);
            if (IsWithinRoot(normalizedPreferred, normalizedWorkspace) || string.Equals(normalizedPreferred, normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPreferred;
            }
        }

        if (Directory.Exists(Path.Combine(normalizedWorkspace, ".git")))
            return normalizedWorkspace;

        var candidateRepos = DiscoverGitRepositories(normalizedWorkspace).ToList();
        if (candidateRepos.Count == 0)
            return normalizedWorkspace;

        if (projectUrl != null)
        {
            var preferredName = TryGetRepoNameFromUrl(projectUrl);
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var matchingRepos = candidateRepos
                    .Where(path =>
                    {
                        var repoName = Path.GetFileName(path);
                        return repoName.Equals(preferredName, StringComparison.OrdinalIgnoreCase)
                               || repoName.StartsWith(preferredName + "-", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                    .ToList();

                if (matchingRepos.Count > 0)
                    return matchingRepos[0];
            }
        }

        if (candidateRepos.Count == 1)
            return candidateRepos[0];

        return candidateRepos
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
            .First();
    }

    public static string NormalizeDuplicateRepoPrefix(string? path, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized is ".")
            return ".";

        foreach (var prefix in GetRepoPrefixCandidates(baseRoot))
        {
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return ".";

            if (normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return normalized[(prefix.Length + 1)..];
        }

        return normalized;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// True when <paramref name="fullPath"/> is the root itself or lies below it. Containment is decided on whole path
    /// segments: "C:/ws-evil" is NOT inside "C:/ws" (a plain prefix test would accept it).
    /// </summary>
    public static bool IsWithinRoot(string fullPath, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(baseRoot))
            return false;

        var full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(baseRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, PathComparison)
               || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// Real write-point check used by every workspace-writing tool: inside the root, no link/junction/reparse point on
    /// any existing component below the root (links could redirect the write outside the worktree), and no git metadata
    /// (".git" directory or pointer file, ".gitmodules"). Call it again immediately before the write (see
    /// <see cref="WriteAllTextSafeAsync"/>) to narrow the check-to-use window.
    /// </summary>
    /// <summary>
    /// True for a model-supplied absolute path that names a place OUTSIDE the trusted root (e.g. the main repository while
    /// the job runs in a shadow worktree). A leading slash on a workspace-relative path ("/src/a.cs") stays tolerated by the
    /// write tools, but only when its directory does not really exist on the host: "/home/x/main/src/a.cs" must be refused,
    /// not silently re-rooted under the shadow. Drive-qualified paths are always foreign unless inside the root.
    /// </summary>
    public static bool IsForeignAbsolutePath(string? raw, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathRooted(raw.Trim()))
        {
            return false;
        }

        try
        {
            var candidate = raw.Trim();
            var full = Path.GetFullPath(candidate);
            if (IsWithinRoot(full, baseRoot))
            {
                return false;
            }

            if (candidate.Length >= 2 && candidate[1] == ':')
            {
                return true;
            }

            var parent = Path.GetDirectoryName(full);
            return parent is not null && Directory.Exists(parent);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    public static bool IsSafeWriteTarget(string fullPath, string baseRoot, out string reason)
    {
        reason = string.Empty;
        if (!IsWithinRoot(fullPath, baseRoot))
        {
            reason = "path escapes the workspace root";
            return false;
        }

        var root = Path.GetFullPath(baseRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length <= root.Length)
        {
            return true;
        }

        var relative = full[(root.Length + 1)..];
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".codexflow", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("host.token", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("control.token", StringComparison.OrdinalIgnoreCase))
            {
                reason = "protected metadata or credential path is not writable";
                return false;
            }
        }

        if (CodexFlow.Core.Execution.WorkspacePath.IsProtectedHostPath(full))
        {
            reason = "protected host credential path is not writable";
            return false;
        }

        var current = full;
        while (current.Length > root.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                var info = new FileInfo(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    reason = "a link, junction or reparse point on the target path is not allowed";
                    return false;
                }
            }

            current = Path.GetDirectoryName(current) ?? root;
        }

        return true;
    }

    /// <summary>Reads a text file once and returns its text together with the SHA-256 of the exact bytes (the version read).</summary>
    public static (string Text, string Sha256) ReadTextWithVersion(string fullPath)
    {
        if (IsCredentialOrProtectedStatePath(fullPath, TrustedWorkspaceRoot))
        {
            throw new UnauthorizedAccessException($"Refusing to read protected host credential or state file: {Path.GetFileName(fullPath)}");
        }

        var bytes = File.ReadAllBytes(fullPath);
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (reader.ReadToEnd(), CodexFlow.Core.Execution.WorkspaceFileWriter.Sha256Hex(bytes));
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static bool StartsWithUtf8Bom(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Utf8Bom);

    /// <summary>True when the existing file begins with a UTF-8 BOM. Missing or unreadable files count as "no BOM".</summary>
    private static bool FileStartsWithUtf8Bom(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[3];
            return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3 && StartsWithUtf8Bom(head);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>SHA-256 of the file's current bytes, or null when it does not exist.</summary>
    public static string? TryReadVersion(string fullPath)
        => File.Exists(fullPath) ? CodexFlow.Core.Execution.WorkspaceFileWriter.Sha256Hex(File.ReadAllBytes(fullPath)) : null;

    private static void Require(CodexFlow.Core.Execution.WorkspaceWriteResult result, string path)
    {
        if (!result.Success)
        {
            throw new UnauthorizedAccessException($"Write refused [{result.ReasonCode}]: {result.Message} ({path})");
        }
    }

    /// <summary>
    /// Final write of text. <paramref name="expectedSha256"/> is the version the caller READ (null = the file must not exist).
    /// The write goes through <see cref="CodexFlow.Core.Execution.WorkspaceFileWriter"/> (pinned/no-follow path, compare-and-swap),
    /// so a concurrent change, a swapped directory/link, a path outside <paramref name="baseRoot"/> or git metadata is refused.
    /// </summary>
    public static Task WriteAllTextVerifiedAsync(string fullPath, string baseRoot, string content, string? expectedSha256, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
        // ReadTextWithVersion consumes a UTF-8 BOM, so the text handed in here never carries it. Without this, editing any
        // line of a BOM file silently rewrote line 1 (an unrelated diff in every agent commit). A new file gets no BOM.
        if (FileStartsWithUtf8Bom(fullPath) && !StartsWithUtf8Bom(bytes))
        {
            bytes = [.. Utf8Bom, .. bytes];
        }

        Require(CodexFlow.Core.Execution.WorkspaceFileWriter.WriteFile(baseRoot, fullPath, bytes, expectedSha256), fullPath);
        return Task.CompletedTask;
    }

    /// <summary>write_file contract: blind full overwrite of whatever is there NOW (still CAS-protected between read and write).</summary>
    public static Task WriteAllTextSafeAsync(string fullPath, string baseRoot, string content, CancellationToken ct)
        => WriteAllTextVerifiedAsync(fullPath, baseRoot, content, TryReadVersion(fullPath), ct);

    public static void DeleteFileVerified(string fullPath, string baseRoot, string expectedSha256)
        => Require(CodexFlow.Core.Execution.WorkspaceFileWriter.DeleteFile(baseRoot, fullPath, expectedSha256), fullPath);

    public static void EnsureDirectoryVerified(string fullPath, string baseRoot)
        => Require(CodexFlow.Core.Execution.WorkspaceFileWriter.EnsureDirectory(baseRoot, fullPath), fullPath);

    public static string NormalizeDuplicateRepoPrefixInPatch(string patchContent, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(patchContent))
            return patchContent;

        var result = patchContent;
        foreach (var repoName in GetRepoPrefixCandidates(baseRoot))
        {
            var escapedRepo = Regex.Escape(repoName);

            // Codex patch format: *** Update/Add/Delete File: CleanApp/...
            result = Regex.Replace(
                result,
                $@"(?m)^(\*\*\* (?:Update|Add|Delete) File:\s+){escapedRepo}/",
                "$1");

            // Unified diff headers
            result = Regex.Replace(result, $@"(?m)^(---\s+(?:a/)?)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(\+\+\+\s+(?:b/)?)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(diff --git\s+a/)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(diff --git\s+a/[^\s]+\s+b/)" + escapedRepo + "/", "$1");
        }

        return result;
    }

    private static IEnumerable<string> GetRepoPrefixCandidates(string baseRoot)
    {
        var normalizedRoot = Path.GetFullPath(baseRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var leaf = Path.GetFileName(normalizedRoot);
        if (!string.IsNullOrWhiteSpace(leaf) && seen.Add(leaf))
            yield return leaf;

        var dir = new DirectoryInfo(normalizedRoot);
        // shadow worktree layout: .../shadows/{repoName}/{taskId}
        if (dir.Parent is not null &&
            dir.Parent.Parent is not null &&
            dir.Parent.Parent.Name.Equals("shadows", StringComparison.OrdinalIgnoreCase))
        {
            var repoName = dir.Parent.Name;
            if (!string.IsNullOrWhiteSpace(repoName) && seen.Add(repoName))
                yield return repoName;
        }
    }

    private static IEnumerable<string> DiscoverGitRepositories(string workspacePath)
    {
        var workspaceFull = Path.GetFullPath(workspacePath);
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((new DirectoryInfo(workspaceFull), 0));

        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".next", "dist", "build", ".venv", "shadows"
        };

        const int maxDepth = 3;
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var current = dir.FullName;

            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                yield return current;
                continue;
            }

            if (depth >= maxDepth)
                continue;

            DirectoryInfo[] children;
            try { children = dir.GetDirectories(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (ignoredNames.Contains(child.Name))
                    continue;

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static string? TryGetRepoNameFromUrl(Uri? projectUrl)
    {
        if (projectUrl == null)
            return null;

        var trimmed = projectUrl.ToString().Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var slashIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (slashIndex < 0 || slashIndex == trimmed.Length - 1)
            return null;

        var name = trimmed[(slashIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
