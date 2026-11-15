// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using CodexFlow.Core.Models;
using CodexFlow.Services.ArtifactAudit;
using NuGet.Versioning;

namespace CodexFlow.Services.Background;

/// <param name="Package">Package id.</param>
/// <param name="Project">Project file (relative, forward slashes) that still references it below the target.</param>
/// <param name="Expected">The version the host chose and fetched.</param>
/// <param name="Actual">The version the project now declares.</param>
public sealed record UnmetPackage(string Package, string Project, string Expected, string Actual);

/// <summary>
/// The host decides which version a vulnerable direct package moves to and fetches exactly that. A task that finishes the dependency change must
/// leave every such package at its version in the projects that reference it. Read from the project files as XML (never from text): a version
/// the file does not state outright (an unresolvable property) is not judged, a package the project does not reference is skipped (it is
/// transitive there), and a version ABOVE the target is the restore preflight's business, not this contract's.
/// </summary>
internal static class PackageVersionContract
{
    public static IReadOnlyList<UnmetPackage> Unmet(string root, IReadOnlyList<UpgradeTarget> targets)
    {
        var unmet = new List<UnmetPackage>();
        foreach (var target in targets)
        {
            if (!NuGetVersion.TryParse(target.Version, out var expected))
            {
                continue;
            }

            foreach (var project in target.Projects)
            {
                var relative = project.Replace('\\', '/').TrimStart('/');
                var path = Path.Combine(root, relative);
                if (!File.Exists(path))
                {
                    continue;
                }

                var declared = DeclaredVersion(root, path, target.Package);
                if (declared is null)
                {
                    continue;
                }

                if (VersionRange.TryParse(declared, out var range) && range.MinVersion is { } min && min < expected)
                {
                    unmet.Add(new UnmetPackage(target.Package, relative, target.Version, declared));
                }
            }
        }

        return unmet;
    }

    /// <summary>
    /// The contract is checked once, on the task that finishes the plan's code changes: no later code task that is not finished yet. Analysis
    /// tasks (explore/verify) after it do not count.
    /// </summary>
    public static bool IsFinalCodeTask(string? currentTaskId, IReadOnlyList<CodexTask> plan)
    {
        var index = -1;
        for (var i = 0; i < plan.Count; i++)
        {
            if (string.Equals(plan[i].Id, currentTaskId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        return !plan.Skip(index + 1).Any(task =>
            !CodexTaskClassifier.IsAnalysisTask(task) &&
            task.Status is CodexTaskStatus.Pending or CodexTaskStatus.Planning or CodexTaskStatus.Executing or CodexTaskStatus.BlockedByDependency);
    }

    // The declared version of the package, or null when it is not referenced or cannot be read as a plain version.
    private static string? DeclaredVersion(string root, string projectPath, string package)
    {
        var project = Load(projectPath);
        if (project is null)
        {
            return null;
        }

        var reference = project.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PackageReference" &&
                                 string.Equals((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"), package, StringComparison.OrdinalIgnoreCase));
        if (reference is null)
        {
            return null;
        }

        var raw = (string?)reference.Attribute("VersionOverride")
                  ?? (string?)reference.Attribute("Version")
                  ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName is "Version" or "VersionOverride")?.Value;
        XDocument? central = null;
        if (raw is null)
        {
            central = LoadNearest(root, projectPath, "Directory.Packages.props");
            raw = central?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "PackageVersion" &&
                                     string.Equals((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"), package, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();
        if (raw.StartsWith("$(", StringComparison.Ordinal) && raw.EndsWith(')'))
        {
            var name = raw[2..^1];
            central ??= LoadNearest(root, projectPath, "Directory.Packages.props");
            raw = (PropertyValue(project, name) ?? (central is null ? null : PropertyValue(central, name)))?.Trim();
        }

        return string.IsNullOrWhiteSpace(raw) || raw.Contains("$(", StringComparison.Ordinal) ? null : raw;
    }

    private static string? PropertyValue(XDocument document, string name)
        => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value;

    private static XDocument? LoadNearest(string root, string projectPath, string fileName)
    {
        var fullRoot = Path.GetFullPath(root);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }

            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }

    private static XDocument? Load(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
