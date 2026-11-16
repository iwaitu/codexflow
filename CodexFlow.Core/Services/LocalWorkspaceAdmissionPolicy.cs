// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Configuration;

namespace CodexFlow.Core.Services;

public enum WorkspaceAdmissionDenial
{
    None = 0,
    InvalidPath,
    NotFound,
    NotADirectory,
    LinkedPath,
    FilesystemRoot,
    SystemDirectory,
    UserHomeDirectory,
    ProtectedCodexFlowDirectory
}

public sealed record WorkspaceAdmissionDecision(
    bool Allowed,
    WorkspaceAdmissionDenial Reason,
    string? CanonicalPath,
    string Message)
{
    public static WorkspaceAdmissionDecision Allow(string canonicalPath)
        => new(true, WorkspaceAdmissionDenial.None, canonicalPath, "allowed");

    public static WorkspaceAdmissionDecision Deny(WorkspaceAdmissionDenial reason, string message)
        => new(false, reason, null, message);
}

public sealed record WorkspaceAdmissionEnvironment(
    string? DataDirectory = null,
    string? GlobalDirectory = null,
    string? UserHomeDirectory = null,
    IReadOnlyList<string>? SystemDirectories = null);

/// <summary>
/// Enforces P3-3 admission rules when a user opens an arbitrary local directory as a workspace.
/// Resolves <c>..</c>, symbolic links, and junctions before evaluating all forbidden locations:
/// filesystem roots, OS system directories, the user home directory itself, and any path equal to,
/// an ancestor of, or a descendant of <c>Local:DataDirectory</c> or <c>~/.codexflow</c>.
/// </summary>
public static class LocalWorkspaceAdmissionPolicy
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static WorkspaceAdmissionDecision Evaluate(string? rawPath, WorkspaceAdmissionEnvironment? env = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath) ||
            rawPath.IndexOfAny(['*', '?', '<', '>', '|', '\0']) >= 0 ||
            !Path.IsPathRooted(rawPath))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.InvalidPath,
                "Workspace path must be a valid absolute directory path.");
        }

        string fullInput;
        try
        {
            fullInput = TrimSeparator(Path.GetFullPath(rawPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.InvalidPath,
                "Workspace path is not syntactically valid.");
        }

        if (File.Exists(fullInput))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.NotADirectory,
                "Workspace path points to a file, not a directory.");
        }

        if (!Directory.Exists(fullInput))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.NotFound,
                "Workspace directory does not exist.");
        }

        // Resolve all symlinks / junctions along the chain and also detect link-based escapes into protected dirs.
        if (!TryResolvePhysicalChain(fullInput, out var canonicalPath, out var containsLink, out var linkError))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.LinkedPath,
                linkError ?? "Workspace path could not be safely resolved.");
        }

        // Check forbidden targets against the fully resolved canonical path first so symlinked/junctioned targets
        // pointing into forbidden directories always receive a clear forbidden-target or link refusal.
        var targetCheck = EvaluateCanonicalTarget(canonicalPath, env);
        if (!targetCheck.Allowed)
        {
            return targetCheck;
        }

        if (containsLink)
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.LinkedPath,
                "Workspace path or one of its parent directories is a symbolic link or junction, which is not allowed.");
        }

        return WorkspaceAdmissionDecision.Allow(canonicalPath);
    }

    private static WorkspaceAdmissionDecision EvaluateCanonicalTarget(
        string canonicalPath,
        WorkspaceAdmissionEnvironment? env)
    {
        if (IsFilesystemRoot(canonicalPath))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.FilesystemRoot,
                "Filesystem roots and drive roots cannot be opened as a workspace.");
        }

        var userHome = NormalizeExistingOrPotentialPath(
            env?.UserHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (userHome is not null && string.Equals(canonicalPath, userHome, PathComparison))
        {
            return WorkspaceAdmissionDecision.Deny(
                WorkspaceAdmissionDenial.UserHomeDirectory,
                "The user home directory itself cannot be opened as a workspace; choose a project subdirectory instead.");
        }

        var protectedRoots = new List<string>();
        var globalDir = NormalizeExistingOrPotentialPath(env?.GlobalDirectory ?? CodexFlowPaths.GlobalDirectory());
        if (globalDir is not null)
        {
            protectedRoots.Add(globalDir);
        }

        var dataDir = NormalizeExistingOrPotentialPath(env?.DataDirectory);
        if (dataDir is not null)
        {
            protectedRoots.Add(dataDir);
        }

        foreach (var protectedRoot in protectedRoots)
        {
            if (IsUnderOrEqual(canonicalPath, protectedRoot) || IsUnderOrEqual(protectedRoot, canonicalPath))
            {
                return WorkspaceAdmissionDecision.Deny(
                    WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory,
                    "CodexFlow data/configuration directories and their ancestors or descendants cannot be opened as a workspace.");
            }
        }

        var systemDirs = env?.SystemDirectories ?? GetDefaultSystemDirectories();
        foreach (var rawSysDir in systemDirs)
        {
            var sysDir = NormalizeExistingOrPotentialPath(rawSysDir);
            if (sysDir is null || IsFilesystemRoot(sysDir))
            {
                continue;
            }

            if (IsUnderOrEqual(canonicalPath, sysDir))
            {
                return WorkspaceAdmissionDecision.Deny(
                    WorkspaceAdmissionDenial.SystemDirectory,
                    "Operating system directories cannot be opened as a workspace.");
            }
        }

        return WorkspaceAdmissionDecision.Allow(canonicalPath);
    }

    private static string[] GetDefaultSystemDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("SystemRoot"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };

            return candidates
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Select(static s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return
        [
            "/etc",
            "/usr",
            "/bin",
            "/sbin",
            "/lib",
            "/lib64",
            "/var",
            "/System",
            "/Library",
            "/private/etc",
            "/private/var"
        ];
    }

    private static bool TryResolvePhysicalChain(
        string fullPath,
        out string canonicalPath,
        out bool containsLink,
        out string? error)
    {
        canonicalPath = fullPath;
        containsLink = false;
        error = null;

        try
        {
            // Collect ancestors from root to leaf to resolve any intermediate link segments cleanly.
            var stack = new Stack<DirectoryInfo>();
            for (var dir = new DirectoryInfo(fullPath); dir is not null; dir = dir.Parent)
            {
                stack.Push(dir);
            }

            foreach (var dir in stack)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || dir.LinkTarget is not null)
                {
                    containsLink = true;
                }
            }

            var resolved = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true);
            if (resolved is not null)
            {
                containsLink = true;
                canonicalPath = TrimSeparator(Path.GetFullPath(resolved.FullName));
            }
            else if (containsLink)
            {
                // An intermediate ancestor segment is a link (e.g. junction/dir -> protected/sub).
                var resolvedLeaf = ResolveChainStepByStep(fullPath);
                if (resolvedLeaf is null)
                {
                    error = "Symbolic link or junction chain could not be resolved.";
                    return false;
                }

                canonicalPath = resolvedLeaf;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Failed to resolve workspace directory links: {ex.Message}";
            return false;
        }
    }

    private static string? ResolveChainStepByStep(string fullPath, bool allowNonExistentTail = false)
    {
        var segments = new List<string>();
        var current = new DirectoryInfo(fullPath);
        while (current.Parent is not null)
        {
            segments.Add(current.Name);
            current = current.Parent;
        }

        segments.Reverse();
        var resolvedCurrent = TrimSeparator(current.FullName);

        for (var i = 0; i < segments.Count; i++)
        {
            var next = Path.Combine(resolvedCurrent, segments[i]);
            if (!Directory.Exists(next) && !File.Exists(next))
            {
                if (!allowNonExistentTail)
                {
                    return null;
                }

                for (var j = i; j < segments.Count; j++)
                {
                    resolvedCurrent = Path.Combine(resolvedCurrent, segments[j]);
                }

                return TrimSeparator(Path.GetFullPath(resolvedCurrent));
            }

            var info = new DirectoryInfo(next);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            {
                var target = Directory.ResolveLinkTarget(next, returnFinalTarget: true);
                if (target is null)
                {
                    return null;
                }

                resolvedCurrent = TrimSeparator(Path.GetFullPath(target.FullName));
            }
            else
            {
                resolvedCurrent = TrimSeparator(Path.GetFullPath(next));
            }
        }

        return resolvedCurrent;
    }

    private static string? NormalizeExistingOrPotentialPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = TrimSeparator(Path.GetFullPath(path));
            return ResolveChainStepByStep(full, allowNonExistentTail: true) ?? full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsFilesystemRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        return string.Equals(TrimSeparator(root), fullPath, PathComparison);
    }

    private static bool IsUnderOrEqual(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, PathComparison))
        {
            return true;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static string TrimSeparator(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
