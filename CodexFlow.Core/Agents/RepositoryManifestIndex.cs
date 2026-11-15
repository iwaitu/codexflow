// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace CodexFlow.Core.Agents;

/// <summary>
/// The manifest files that exist in a repository (project files, package manifests, solution files). The step that turns a planning draft into
/// tasks never saw the repository, so what a task declared as its outputs was whatever the model imagined; given this list it chooses from files
/// that are there. Bounded and read-only: it lists names, nothing is opened.
/// </summary>
public static class RepositoryManifestIndex
{
    /// <summary>The most files listed; the rest are only counted.</summary>
    public const int MaxEntries = 60;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "target", "__pycache__", ".venv", "venv", ".tmp", ".omc", ".claude"
    };

    private static readonly string[] ExactNames =
    [
        "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "requirements.txt", "pyproject.toml", "Pipfile", "go.mod", "Cargo.toml",
        "pom.xml", "build.gradle", "Directory.Packages.props", "Directory.Build.props", "global.json", "NuGet.config", "nuget.config"
    ];

    private static readonly string[] Extensions = [".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"];

    /// <summary>Relative paths (forward slashes, sorted) of the manifests under <paramref name="root"/>, at most <see cref="MaxEntries"/>.</summary>
    public static IReadOnlyList<string> List(string? root) => Collect(root).Take(MaxEntries).ToArray();

    /// <summary>The block the projection prompt carries, or null when there is no repository or it holds no manifest.</summary>
    public static string? Describe(string? root)
    {
        var all = Collect(root);
        if (all.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var path in all.Take(MaxEntries))
        {
            builder.Append("- ").AppendLine(path);
        }

        if (all.Count > MaxEntries)
        {
            builder.AppendLine($"（另有 {all.Count - MaxEntries} 个未列出）");
        }

        return builder.ToString().TrimEnd();
    }

    private static List<string> Collect(string? root)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return found;
        }

        var start = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(start);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    if (ExactNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                        Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
                        (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(Path.GetRelativePath(start, file).Replace('\\', '/'));
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder only makes the list shorter.
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
