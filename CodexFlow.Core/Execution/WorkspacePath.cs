// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Core.Execution;

/// <summary>Strict workspace-relative path rules shared by staging, export validation and publishing.</summary>
public static class WorkspacePath
{
    private static readonly HashSet<string> GeneratedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "TestResults", ".vs"
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Regex DriveLike = new(@"^[A-Za-z]:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxPathLength = 400;

    /// <summary>Best effort normalisation for scope declarations (not a safety check).</summary>
    public static string NormalizeLoose(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.Trim('/');
    }

    /// <summary>
    /// Strict check for a path that came from an untrusted source (tar entry, model). Backslashes, drive letters,
    /// UNC, absolute paths, "..", ADS colons, control chars, trailing dots/spaces and device names are all rejected.
    /// </summary>
    public static bool TryNormalizeStrict(string? raw, out string normalized, out string reason)
    {
        normalized = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            reason = "empty path";
            return false;
        }

        if (raw.Length > MaxPathLength)
        {
            reason = "path too long";
            return false;
        }

        if (raw.Contains('\\', StringComparison.Ordinal))
        {
            reason = "backslash in path";
            return false;
        }

        if (raw.StartsWith('/') || DriveLike.IsMatch(raw))
        {
            reason = "absolute or drive-qualified path";
            return false;
        }

        var trimmed = raw.EndsWith('/') ? raw.TrimEnd('/') : raw;
        var segments = trimmed.Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg.Length == 0)
            {
                reason = "empty path segment";
                return false;
            }

            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                reason = "parent traversal";
                return false;
            }

            if (seg.Any(ch => ch < 0x20 || ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|'))
            {
                reason = "illegal character in path segment";
                return false;
            }

            if (seg.EndsWith('.') || seg.EndsWith(' '))
            {
                reason = "trailing dot or space";
                return false;
            }

            var stem = seg.Contains('.', StringComparison.Ordinal) ? seg[..seg.IndexOf('.', StringComparison.Ordinal)] : seg;
            if (ReservedDeviceNames.Contains(stem))
            {
                reason = "reserved device name";
                return false;
            }

            kept.Add(seg);
        }

        if (kept.Count == 0)
        {
            reason = "empty path";
            return false;
        }

        normalized = string.Join('/', kept);
        return true;
    }

    public static bool IsGenerated(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return IsGeneratedCore(normalized);
    }

    private static bool IsGeneratedCore(string normalized)
        => normalized.Split('/').Any(GeneratedSegments.Contains);

    public static bool IsGeneratedSegment(string segment) => GeneratedSegments.Contains(segment);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ProtectedHostRoots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static void RegisterProtectedHostPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        try
        {
            var normalized = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ProtectedHostRoots[normalized] = 0;
        }
        catch (ArgumentException)
        {
        }
    }

    public static bool IsProtectedHostPath(string? candidateFullPath)
    {
        if (string.IsNullOrWhiteSpace(candidateFullPath))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidateFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return true;
        }

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var protectedRoot in ProtectedHostRoots.Keys)
        {
            if (string.Equals(full, protectedRoot, cmp) ||
                full.StartsWith(protectedRoot + Path.DirectorySeparatorChar, cmp))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsProtected(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return IsProtectedCore(normalized);
    }

    private static bool IsProtectedCore(string normalized)
        => normalized.Split('/').Any(s =>
            s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(".codexflow", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("host.token", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("control.token", StringComparison.OrdinalIgnoreCase));

    /// <summary>Glob supporting "**", "*" and "?" over normalized '/' paths. Case-insensitive.</summary>
    public static Regex GlobToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var g = NormalizeLoose(glob);
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < g.Length && g[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    }
}
