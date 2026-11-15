// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;

namespace CodexFlow.Services.ArtifactAudit.Ecosystems;

/// <summary>One advisory entry that affects one locked package version.</summary>
public sealed record OsvMatch(LockedPackage Package, OsvAdvisory Advisory, OsvAffected Affected, bool Accepted);

/// <summary>
/// How to fix the vulnerable npm / PyPI package versions of a project, from the advisory data alone (the same idea as
/// <see cref="DependencyRemediationPlanner"/> for NuGet). An advisory range that contains the version says where it ends:
/// <c>fixed: X</c> means X is the first unaffected version, <c>last_affected: X</c> means anything greater is unaffected, and a range
/// nothing closes has no known fix. All advisories of one package version are combined, so the direction satisfies every one of them.
/// </summary>
public static class OsvRemediationPlanner
{
    public static IReadOnlyList<ArtifactDependencyRemediation> Plan(string ecosystem, IEnumerable<OsvMatch> matches, IVersionComparer comparer) =>
        matches.GroupBy(m => (m.Package.Name, m.Package.Version))
            .OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.Version, StringComparer.Ordinal)
            .Select(g => Build(ecosystem, g, comparer)).ToList();

    private static ArtifactDependencyRemediation Build(string ecosystem, IGrouping<(string Name, string Version), OsvMatch> group, IVersionComparer comparer)
    {
        string? atLeast = null;
        string? beyond = null;
        var noKnownFix = false;
        foreach (var match in group)
        {
            var end = ClosingBoundary(match.Affected, group.Key.Version, comparer);
            if (end is null) noKnownFix = true;
            else if (end.Kind == "last_affected") beyond = Max(beyond, end.Version, comparer);
            else atLeast = Max(atLeast, end.Version, comparer);
        }

        var action = noKnownFix ? "NoKnownFix"
            : beyond is not null && (atLeast is null || comparer.Compare(beyond, atLeast) >= 0) ? "UpgradeBeyond"
            : "UpgradeToAtLeast";
        return new ArtifactDependencyRemediation
        {
            Ecosystem = ecosystem,
            Package = group.Key.Name,
            CurrentVersion = group.Key.Version,
            Transitive = group.All(m => m.Package.Transitive),
            Projects = group.Select(m => m.Package.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            AdvisoryIds = group.Select(m => m.Advisory.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            HighestSeverity = group.OrderByDescending(m => m.Advisory.Severity).First().Advisory.SeverityName,
            MinimumFixedVersion = action == "UpgradeToAtLeast" ? atLeast : null,
            MustExceedVersion = action == "UpgradeBeyond" ? beyond : null,
            Action = action,
            BaselineAccepted = group.All(m => m.Accepted)
        };
    }

    /// <summary>
    /// The event that ends the affected range the version is in. A version the advisory only lists by name (no range contains it) takes
    /// the highest <c>fixed</c> boundary the advisory names; null when nothing says where the affected versions end.
    /// </summary>
    internal static OsvEvent? ClosingBoundary(OsvAffected affected, string version, IVersionComparer comparer)
    {
        OsvEvent? highestFixed = null;
        foreach (var range in affected.Ranges)
        {
            if (!string.Equals(range.Type, "SEMVER", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(range.Type, "ECOSYSTEM", StringComparison.OrdinalIgnoreCase)) continue;

            var ordered = range.Events.Where(e => e.Kind is "introduced" or "fixed" or "last_affected").ToList();
            ordered.Sort((a, b) => a.Version == "0" ? (b.Version == "0" ? 0 : -1) : b.Version == "0" ? 1 : comparer.Compare(a.Version, b.Version) ?? 0);

            string? introduced = null;
            foreach (var ev in ordered)
            {
                if (ev.Kind == "introduced")
                {
                    introduced ??= ev.Version;
                    continue;
                }

                if (ev.Kind == "fixed" && (highestFixed is null || comparer.Compare(ev.Version, highestFixed.Version) > 0)) highestFixed = ev;
                if (introduced is null) continue;
                var afterStart = introduced == "0" ? 1 : comparer.Compare(version, introduced);
                var againstEnd = comparer.Compare(version, ev.Version);
                if (afterStart >= 0 && (ev.Kind == "last_affected" ? againstEnd <= 0 : againstEnd < 0)) return ev;
                introduced = null;
            }

            // Inside a range nothing closes: every later version is affected too.
            if (introduced is not null && (introduced == "0" || comparer.Compare(version, introduced) >= 0)) return null;
        }

        return highestFixed;
    }

    private static string Max(string? current, string candidate, IVersionComparer comparer) =>
        current is null || comparer.Compare(candidate, current) > 0 ? candidate : current;
}
