// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace CodexFlow.Core.Agents;

/// <summary>
/// What is really in the top level of the project root, as names. A repository bound as the project root is "already there", so the model is told
/// not to clone it, and a model that then calls no tool has nothing real to say about it: recorded runs answered "I read the directory" without any
/// tool call and printed a tree (tests/, main.py, .env.example) that did not exist. With this list the host gives the layout a real source.
/// Bounded and read-only: names only, one level, nothing is opened.
/// </summary>
public static class ProjectRootListing
{
    /// <summary>The most entries listed; the rest are only counted.</summary>
    public const int MaxEntries = 60;

    /// <summary>A name is cut here: it is data from the repository, not text the host wrote.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The block for the prompt, or null when there is no root, it does not exist or it holds nothing.</summary>
    public static string? Describe(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        List<(string Name, bool IsDirectory)> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(Path.GetFullPath(root))
                .Select(static path => (Name: Path.GetFileName(path), IsDirectory: Directory.Exists(path)))
                .Where(static entry => entry.Name.Length > 0)
                .OrderByDescending(static entry => entry.IsDirectory)
                .ThenBy(static entry => entry.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var (name, isDirectory) in entries.Take(MaxEntries))
        {
            builder.Append("- ").Append(SafeName(name)).AppendLine(isDirectory ? "/" : string.Empty);
        }

        if (entries.Count > MaxEntries)
        {
            builder.AppendLine($"（另有 {entries.Count - MaxEntries} 项未列出）");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>A name without control characters (line breaks included) and no longer than <see cref="MaxNameLength"/>.</summary>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength + 1));
        var previousWasControl = false;
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                if (!previousWasControl)
                {
                    builder.Append(' ');   // a run of control characters is one space
                }

                previousWasControl = true;
                continue;
            }

            previousWasControl = false;
            builder.Append(c);
        }

        var cleaned = builder.ToString();
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] + "…" : cleaned;
    }
}
