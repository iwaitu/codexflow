// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in docs/plans/remediation-operations.md
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Contracts;
using NuGet.Versioning;

namespace CodexFlow.Services.ArtifactAudit;

public sealed record DependencyBaselineEntry(string Package, string Version, string AdvisoryId, string Reason);

/// <summary>
/// A reviewed, expiring list of pre-existing dependency advisories. It accepts only the exact package + version + advisory it names:
/// a new advisory for the same version, another version, or any Critical advisory is never covered.
/// </summary>
public sealed class DependencyBaseline
{
    public required string ReviewedBy { get; init; }
    public required DateTimeOffset ReviewedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<DependencyBaselineEntry> Entries { get; init; }

    private HashSet<string>? _keys;

    private static string Key(string package, string version, string advisoryId) =>
        string.Join('|', package.ToLowerInvariant(), NuGetVersion.Parse(version).ToNormalizedString(), advisoryId.ToLowerInvariant());

    /// <summary>Critical advisories are never baseline-able: accepting them would turn the baseline into a bypass.</summary>
    public bool Accepts(string package, string version, string advisoryId, int severity)
    {
        if (severity >= 3 || !NuGetVersion.TryParse(version, out _)) return false;
        _keys ??= Entries.Select(e => Key(e.Package, e.Version, e.AdvisoryId)).ToHashSet(StringComparer.Ordinal);
        return _keys.Contains(Key(package, version, advisoryId));
    }
}

public static class DependencyBaselineLoader
{
    public const int SupportedSchema = 1;
    /// <summary>A baseline is a deadline for fixing the debt, not a permanent exemption.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(180);
    private const int MaxEntries = 500;
    private const long MaxBytes = 512 * 1024;

    private static readonly Regex AdvisoryId = new(@"^[A-Za-z0-9._-]{3,80}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal) { "schema", "reviewedBy", "reviewedAtUtc", "expiresAtUtc", "entries" };
    private static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal) { "package", "version", "advisoryId", "reason" };

    /// <summary>Returns (null, null) when no baseline is configured; otherwise a valid baseline or an error message (never both).</summary>
    public static (DependencyBaseline? Baseline, string? Error) Load(string? path, string? expectedSha256, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, "The configured dependency baseline file does not exist.");
            if (info.Length > MaxBytes) return (null, $"The dependency baseline is larger than {MaxBytes} bytes.");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "The dependency baseline file cannot be read.");
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !string.Equals(sha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, "The dependency baseline does not match the pinned SHA-256; it was changed after review.");

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            return Parse(document.RootElement, sha, now);
        }
        catch (JsonException)
        {
            return (null, "The dependency baseline is not valid JSON.");
        }
    }

    private static (DependencyBaseline?, string?) Parse(JsonElement root, string sha, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return Fail("the document must be a JSON object");
        foreach (var property in root.EnumerateObject())
            if (!TopLevelKeys.Contains(property.Name)) return Fail($"unknown key '{Trim(property.Name)}'");

        if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != SupportedSchema)
            return Fail($"schema must be {SupportedSchema}");

        var reviewedBy = Text(root, "reviewedBy");
        if (string.IsNullOrWhiteSpace(reviewedBy) || reviewedBy.Length > 100) return Fail("reviewedBy is required (1-100 characters)");
        if (!Instant(root, "reviewedAtUtc", out var reviewedAt)) return Fail("reviewedAtUtc must be an ISO-8601 UTC timestamp");
        if (!Instant(root, "expiresAtUtc", out var expires)) return Fail("expiresAtUtc must be an ISO-8601 UTC timestamp");
        if (reviewedAt > now + TimeSpan.FromMinutes(5)) return Fail("reviewedAtUtc is in the future");
        if (expires <= now) return Fail($"the baseline expired at {expires:u}");
        if (expires <= reviewedAt || expires - reviewedAt > MaxLifetime) return Fail($"a baseline may be valid for at most {MaxLifetime.TotalDays:0} days after its review");

        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) return Fail("entries must be an array");
        var entries = new List<DependencyBaselineEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Fail("every entry must be an object");
            foreach (var property in item.EnumerateObject())
                if (!EntryKeys.Contains(property.Name)) return Fail($"unknown entry key '{Trim(property.Name)}'");

            var package = Text(item, "package");
            var packageVersion = Text(item, "version");
            var advisory = Text(item, "advisoryId");
            var reason = Text(item, "reason");
            if (string.IsNullOrWhiteSpace(package) || package.Length > 200 || package.Any(char.IsWhiteSpace)) return Fail("every entry needs a package id");
            if (!NuGetVersion.TryParse(packageVersion, out _) || packageVersion!.Any(c => c is '[' or ']' or '(' or ')' or ',' or '*'))
                return Fail($"entry '{Trim(package)}' needs one exact version (no ranges or wildcards)");
            if (string.IsNullOrWhiteSpace(advisory) || !AdvisoryId.IsMatch(advisory)) return Fail($"entry '{Trim(package)}' needs an advisoryId");
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) return Fail($"entry '{Trim(package)}' needs a reason (why the debt is tolerated and where it is tracked)");
            if (!seen.Add(string.Join('|', package.ToLowerInvariant(), NuGetVersion.Parse(packageVersion).ToNormalizedString(), advisory.ToLowerInvariant())))
                return Fail($"duplicate entry for '{Trim(package)}'");
            entries.Add(new DependencyBaselineEntry(package, packageVersion, advisory, reason));
            if (entries.Count > MaxEntries) return Fail($"more than {MaxEntries} entries");
        }

        if (entries.Count == 0) return Fail("a baseline needs at least one entry");
        return (new DependencyBaseline { ReviewedBy = reviewedBy, ReviewedAtUtc = reviewedAt, ExpiresAtUtc = expires, Sha256 = sha, Entries = entries }, null);

        static (DependencyBaseline?, string?) Fail(string reason) => (null, "The dependency baseline is not usable: " + reason + ".");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Instant(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        var text = Text(element, name);
        return text is not null && text.EndsWith('Z') &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static string Trim(string text) => AuditText.Sanitize(text, 60);
}

/// <summary>
/// Advisories that already existed at the start of the plan's chain (found by scanning the chain's base commit) and that the approving
/// user accepted together with the plan. Matches on the exact package + version + advisory: an advisory that appears only at the
/// final commit, a bumped version, or any Critical advisory is never covered.
/// </summary>
public sealed class ExistingDependencyDebt
{
    private readonly HashSet<string> _keys;

    private ExistingDependencyDebt(string baseCommit, string approvedBy, DateTimeOffset approvedAtUtc, HashSet<string> keys)
    {
        BaseCommit = baseCommit;
        ApprovedBy = approvedBy;
        ApprovedAtUtc = approvedAtUtc;
        _keys = keys;
    }

    public string BaseCommit { get; }
    public string ApprovedBy { get; }
    public DateTimeOffset ApprovedAtUtc { get; }
    public int Count => _keys.Count;

    private static string Key(string package, string version, string advisoryId) =>
        string.Join('|', package.ToLowerInvariant(), NuGet.Versioning.NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version, advisoryId.ToLowerInvariant());

    /// <summary>Builds the debt from a scan of the chain's base commit. Critical advisories are left out: they must be fixed.</summary>
    public static ExistingDependencyDebt FromReport(DependencyScanReport baseReport, string approvedBy, DateTimeOffset approvedAtUtc)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in baseReport.Vulnerabilities)
        {
            if (string.Equals(finding.Severity, "Critical", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(finding.Package) || string.IsNullOrWhiteSpace(finding.PackageVersion) || string.IsNullOrWhiteSpace(finding.AdvisoryId)) continue;
            keys.Add(Key(finding.Package, finding.PackageVersion, finding.AdvisoryId));
        }

        return new ExistingDependencyDebt(baseReport.Commit, approvedBy, approvedAtUtc, keys);
    }

    public bool Accepts(string package, string version, string advisoryId, int severity) =>
        severity < 3 && _keys.Contains(Key(package, version, advisoryId));
}

/// <summary>One advisory that matched one resolved package in one project.</summary>
public sealed record VulnerabilityMatch(ResolvedPackage Package, NuGetAdvisory Advisory, ArtifactAuditFinding Finding, bool BaselineAccepted);

/// <summary>Turns advisory matches into fix plans using only the advisory ranges (no network, nothing from the repository).</summary>
public static class DependencyRemediationPlanner
{
    public static IReadOnlyList<ArtifactDependencyRemediation> Plan(IEnumerable<VulnerabilityMatch> matches) =>
        matches.GroupBy(m => (Id: m.Package.Id.ToLowerInvariant(), m.Package.Version))
            .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.Version, StringComparer.Ordinal)
            .Select(Build).ToList();

    private static ArtifactDependencyRemediation Build(IGrouping<(string Id, string Version), VulnerabilityMatch> group)
    {
        NuGetVersion? atLeast = null;     // advisory range ends exclusively at X: X is the first unaffected version
        NuGetVersion? beyond = null;      // advisory range ends inclusively at X: anything greater is unaffected
        var noKnownFix = false;
        foreach (var match in group)
        {
            if (!VersionRange.TryParse(match.Advisory.VersionRange, out var range) || !range.HasUpperBound || range.MaxVersion is null)
            {
                noKnownFix = true;
                continue;
            }

            if (range.IsMaxInclusive) beyond = Max(beyond, range.MaxVersion);
            else atLeast = Max(atLeast, range.MaxVersion);
        }

        var action = noKnownFix ? "NoKnownFix" : beyond is not null && (atLeast is null || beyond >= atLeast) ? "UpgradeBeyond" : "UpgradeToAtLeast";
        // When both bounds exist the stricter one wins: "at least 13.0.3" already exceeds "beyond 12.0.3".
        var minimum = action == "UpgradeToAtLeast" ? atLeast : null;
        var mustExceed = action == "UpgradeBeyond" ? beyond : null;
        var first = group.First();
        return new ArtifactDependencyRemediation
        {
            Package = first.Package.Id,
            CurrentVersion = first.Package.Version,
            Transitive = group.All(m => m.Package.Transitive),
            Projects = group.Select(m => m.Finding.Location).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            AdvisoryIds = group.Select(m => m.Advisory.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            HighestSeverity = group.OrderByDescending(m => m.Advisory.Severity).First().Advisory.SeverityName,
            MinimumFixedVersion = minimum?.ToNormalizedString(),
            MustExceedVersion = mustExceed?.ToNormalizedString(),
            Action = action,
            BaselineAccepted = group.All(m => m.BaselineAccepted)
        };
    }

    private static NuGetVersion Max(NuGetVersion? current, NuGetVersion candidate) => current is null || candidate > current ? candidate : current;
}
