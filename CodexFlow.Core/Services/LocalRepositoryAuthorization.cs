// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;

namespace CodexFlow.Core.Services;

/// <summary>Why a local repository root was refused. <see cref="None"/> means it is allowed.</summary>
public enum LocalRepositoryDenial
{
    None = 0,
    NotRooted,
    NotFound,
    LinkedPath,
    OutsideSessionAndAllowedRoots,
    InvalidConfiguration
}

/// <summary>The outcome of a local repository authorization check, with a stable reason and a human readable message.</summary>
public sealed record LocalRepositoryDecision(bool Allowed, LocalRepositoryDenial Reason, string? NormalizedRoot, string Message)
{
    public static LocalRepositoryDecision Allow(string root) => new(true, LocalRepositoryDenial.None, root, "allowed");

    public static LocalRepositoryDecision Deny(LocalRepositoryDenial reason, string message) => new(false, reason, null, message);
}

/// <summary>The configured shared roots, split into usable entries and entries that make the configuration invalid.</summary>
public sealed record AllowedLocalRepositoryRoots(IReadOnlyList<string> Roots, IReadOnlyList<string> InvalidEntries)
{
    public bool IsValid => InvalidEntries.Count == 0;
}

/// <summary>
/// The single policy deciding which local (file URI / filesystem) repositories a session may use.
/// <list type="bullet">
/// <item>Default (nothing configured): only the current session's own root and its sub-directories. Another session's or user's
/// directory is never allowed, no matter that it exists on the same host.</item>
/// <item><c>Workspace:AllowedLocalRepositoryRoots</c>: an administrator may additionally name shared roots; the root itself and
/// its sub-directories are then allowed. This is an explicit allow-list, never inferred from the request.</item>
/// <item>Paths are fully normalized, must be rooted and existing, and neither the repository nor any of its ancestors (nor a
/// configured root or its ancestors) may be a symbolic link / junction / reparse point.</item>
/// <item>An invalid configuration (relative, empty, wildcard, a filesystem root, or a linked entry) fails closed: the configured
/// roots then allow nothing; the session-root rule is unaffected because configuration can neither widen nor narrow it.</item>
/// </list>
/// Callers: the Gateway before it binds a user supplied local repository to a session, and the artifact verification service
/// after it resolved the repository root.
/// </summary>
public static class LocalRepositoryAuthorization
{
    public const string ConfigurationKey = "Workspace:AllowedLocalRepositoryRoots";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Reads the configured allow-list (an array under <see cref="ConfigurationKey"/>). Missing configuration = empty, valid.</summary>
    public static AllowedLocalRepositoryRoots ReadAllowedRoots(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return new AllowedLocalRepositoryRoots([], []);
        }

        var raw = configuration.GetSection(ConfigurationKey).GetChildren().Select(child => child.Value).ToList();
        return ValidateAllowedRoots(raw);
    }

    /// <summary>Validates raw configured entries. Any invalid entry makes the whole list unusable for <see cref="Evaluate"/>.</summary>
    public static AllowedLocalRepositoryRoots ValidateAllowedRoots(IEnumerable<string?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var roots = new List<string>();
        var invalid = new List<string>();
        foreach (var entry in entries)
        {
            if (!TryNormalizeConfiguredRoot(entry, out var normalized))
            {
                invalid.Add(entry ?? "<null>");
                continue;
            }

            roots.Add(normalized);
        }

        return new AllowedLocalRepositoryRoots(roots, invalid);
    }

    public static bool IsAllowed(string repositoryRoot, string sessionRoot, IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        return Evaluate(repositoryRoot, sessionRoot, allowedRoots).Allowed;
    }

    public static LocalRepositoryDecision Evaluate(string repositoryRoot, string sessionRoot, IConfiguration? configuration)
        => Evaluate(repositoryRoot, sessionRoot, ReadAllowedRoots(configuration));

    public static LocalRepositoryDecision Evaluate(string repositoryRoot, string sessionRoot, IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        return Evaluate(repositoryRoot, sessionRoot, ValidateAllowedRoots(allowedRoots));
    }

    public static LocalRepositoryDecision Evaluate(string repositoryRoot, string sessionRoot, AllowedLocalRepositoryRoots allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathRooted(repositoryRoot))
        {
            return LocalRepositoryDecision.Deny(LocalRepositoryDenial.NotRooted, "The local repository path must be an absolute path.");
        }

        string fullRepository;
        try
        {
            fullRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LocalRepositoryDecision.Deny(LocalRepositoryDenial.NotRooted, "The local repository path is not a valid path.");
        }

        if (!Directory.Exists(fullRepository))
        {
            return LocalRepositoryDecision.Deny(LocalRepositoryDenial.NotFound, "The local repository directory does not exist.");
        }

        if (HasLinkInChain(fullRepository))
        {
            return LocalRepositoryDecision.Deny(
                LocalRepositoryDenial.LinkedPath,
                "The local repository path (or one of its parent directories) is a symbolic link or junction, which is not allowed.");
        }

        if (TryNormalizeSessionRoot(sessionRoot, out var fullSession) && IsUnderOrEqual(fullRepository, fullSession))
        {
            return LocalRepositoryDecision.Allow(fullRepository);
        }

        if (!allowed.IsValid)
        {
            return LocalRepositoryDecision.Deny(
                LocalRepositoryDenial.InvalidConfiguration,
                $"{ConfigurationKey} contains invalid entries, so no shared local repository root is allowed until it is fixed.");
        }

        if (allowed.Roots.Any(root => IsUnderOrEqual(fullRepository, root)))
        {
            return LocalRepositoryDecision.Allow(fullRepository);
        }

        return LocalRepositoryDecision.Deny(
            LocalRepositoryDenial.OutsideSessionAndAllowedRoots,
            $"The local repository is outside this session's workspace and is not under any root listed in {ConfigurationKey}.");
    }

    private static bool TryNormalizeSessionRoot(string? sessionRoot, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionRoot) || !Path.IsPathRooted(sessionRoot))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // A filesystem root as "session root" would authorize the whole disk.
        return !IsFilesystemRoot(normalized);
    }

    private static bool TryNormalizeConfiguredRoot(string? entry, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(entry) || entry.IndexOfAny(['*', '?', '<', '>', '|', '\0']) >= 0 || !Path.IsPathRooted(entry))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (IsFilesystemRoot(normalized))
        {
            return false; // "C:\" or "/" would allow every repository on the host
        }

        // A configured root that is (or sits below) a link would let the target be swapped; reject instead of following it.
        return !Directory.Exists(normalized) || !HasLinkInChain(normalized);
    }

    private static bool IsFilesystemRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(Path.TrimEndingDirectorySeparator(root), fullPath, PathComparison);
    }

    private static bool IsUnderOrEqual(string child, string parent)
        => string.Equals(child, parent, PathComparison) ||
           child.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>True when the directory or any of its ancestors is a symbolic link / junction / reparse point.</summary>
    public static bool ContainsLink(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return HasLinkInChain(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    private static bool HasLinkInChain(string fullPath)
    {
        for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }
}
