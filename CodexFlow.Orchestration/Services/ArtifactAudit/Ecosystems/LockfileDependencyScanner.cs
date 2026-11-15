// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in docs/dependency-baseline.md
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;

namespace CodexFlow.Services.ArtifactAudit.Ecosystems;

/// <summary>What an ecosystem found in the snapshot: the packages it could read, risks it could not cover, or a reason it cannot go on.</summary>
public sealed record LockfileRead(IReadOnlyList<LockedPackage> Packages, IReadOnlyList<ArtifactAuditFinding> Risks, string? Error);

/// <summary>The ecosystem-specific half of a lockfile-based dependency scan: which files it owns, how to read them, how its versions order.</summary>
public interface ILockfileEcosystem
{
    /// <summary>The ecosystem name as OSV spells it ("npm", "PyPI").</summary>
    string OsvEcosystem { get; }

    /// <summary>Tool name recorded in the audit result.</summary>
    string ToolName { get; }

    IVersionComparer Comparer { get; }

    /// <summary>Manifests of the snapshot this ecosystem takes responsibility for; the other scanners then do not report them as unsupported.</summary>
    IReadOnlyList<string> ClaimedManifests(SnapshotInventory inventory);

    Task<LockfileRead> ReadAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct);
}

/// <summary>
/// Dependency gate for an ecosystem that fixes its versions in a file (npm, Python): read the locked packages from the pristine snapshot,
/// match them against the host-managed OSV advisories, and fail closed everywhere — stale or missing advisory data, a lockfile it cannot
/// read, a version or range it cannot compare. It never runs project code, so there is nothing to tamper with.
/// </summary>
public sealed class LockfileDependencyScanner(
    ILockfileEcosystem ecosystem, ArtifactAuditOptions options, IOsvSnapshotProvider advisories, TimeProvider time)
{
    private const string Layer = "dependencies";

    public ILockfileEcosystem Ecosystem => ecosystem;

    /// <summary>
    /// What to do about data the gate cannot use. The npm and PyPI data is not fetched while a scan runs: a script downloads and reduces it, and the
    /// gate refuses it once it is older than <c>Advisory:MaxAge</c> (48 hours). A message that only says "older than the allowed 2 days" leaves the
    /// person with nothing to do, so the message carries the command.
    /// </summary>
    private string RefreshHint()
    {
        var directory = string.IsNullOrWhiteSpace(options.OsvAdvisoryDirectory) ? "<the directory set as ArtifactAudit:OsvAdvisoryDirectory>" : options.OsvAdvisoryDirectory;
        return $"Refresh it with: python scripts/e2e/prepare-osv-advisories.py --out \"{directory}\" --ecosystems {ecosystem.OsvEcosystem}";
    }

    public async Task<AuditLayerOutcome> ScanAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct, ExistingDependencyDebt? existingDebt = null)
    {
        var findings = new List<ArtifactAuditFinding>();
        OsvSnapshotResult advisory;
        try
        {
            advisory = await advisories.GetSnapshotAsync(ecosystem.OsvEcosystem, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryUnavailable, "Advisory data provider failed: " + AuditText.Sanitize(ex.Message, 200), string.Empty, findings);
        }

        if (advisory.Status != AdvisoryStatus.Ok || advisory.Snapshot is null)
        {
            var reason = advisory.Status switch { AdvisoryStatus.Stale => ArtifactAuditReasons.AdvisoryStale, AdvisoryStatus.Invalid => ArtifactAuditReasons.AdvisoryInvalid, _ => ArtifactAuditReasons.AdvisoryUnavailable };
            return Out(ArtifactAuditState.Error, reason, $"{ecosystem.OsvEcosystem} vulnerability data not usable: " + AuditText.Sanitize(advisory.Message, 300) + " " + RefreshHint(), string.Empty, findings);
        }

        var snapshot = advisory.Snapshot;
        var dataVersion = snapshot.Version;
        var now = time.GetUtcNow();
        var adv = options.Advisory;
        if (snapshot.Packages.Count < adv.MinimumPackageEntries)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryInvalid, "Vulnerability data failed the minimum coverage check.", dataVersion, findings);
        if (snapshot.RetrievedAtUtc > now + TimeSpan.FromMinutes(5) || now - snapshot.RetrievedAtUtc > adv.MaxAge)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryStale, $"Vulnerability data was retrieved {snapshot.RetrievedAtUtc:u}, older than the allowed {adv.MaxAge}. {RefreshHint()}", dataVersion, findings);
        if (now - snapshot.SourceUpdatedUtc > adv.MaxSourceAge)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryStale, $"The advisory feed itself was last updated {snapshot.SourceUpdatedUtc:u}, older than the allowed {adv.MaxSourceAge}. {RefreshHint()}", dataVersion, findings);

        var (baseline, baselineError) = DependencyBaselineLoader.Load(options.DependencyBaseline.Path, options.DependencyBaseline.ExpectedSha256, now);
        if (baselineError is not null)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.BaselineInvalid, baselineError, dataVersion, findings);
        if (baseline is not null) dataVersion += "; baseline=sha256:" + baseline.Sha256[..16];

        if (inventory.UnsafePaths.Count > 0)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, $"{inventory.UnsafePaths.Count} manifest path(s) have unsupported characters and cannot be scanned.", dataVersion, findings);

        var read = await ecosystem.ReadAsync(session, inventory, ct).ConfigureAwait(false);
        if (read.Error is not null)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, read.Error, dataVersion, findings);
        var needsReview = read.Risks.Count > 0;
        findings.AddRange(read.Risks);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var debt = new List<ArtifactAuditFinding>();
        var vulnerabilities = new List<(ArtifactAuditFinding Finding, bool Accepted, bool Blocking)>();
        var matches = new List<OsvMatch>();
        foreach (var pkg in read.Packages.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version, StringComparer.Ordinal))
        {
            if (!snapshot.Packages.TryGetValue(pkg.Name, out var forPackage)) continue;
            foreach (var a in forPackage)
            {
                foreach (var affected in a.Affected.Where(x => string.Equals(x.Package, pkg.Name, StringComparison.Ordinal)))
                {
                    var hit = OsvMatcher.Affects(affected, pkg.Version, ecosystem.Comparer);
                    if (hit is null)
                        return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryInvalid,
                            $"Cannot evaluate advisory '{AuditText.Sanitize(a.Id, 60)}' against {AuditText.Sanitize(pkg.Name, 80)} {AuditText.Sanitize(pkg.Version, 40)}; the scan fails closed.", dataVersion, findings);
                    if (hit != true || !seen.Add($"{pkg.Name}|{pkg.Version}|{a.Id}|{pkg.Source}")) continue;

                    var finding = new ArtifactAuditFinding(
                        AuditText.Fingerprint(Layer, ecosystem.OsvEcosystem, "vuln", pkg.Name, pkg.Version, a.Id, pkg.Source),
                        a.SeverityName, pkg.Source,
                        $"{AuditText.Sanitize(pkg.Name, 100)} {AuditText.Sanitize(pkg.Version, 50)} ({(pkg.Transitive ? "transitive" : "direct")}, {ecosystem.OsvEcosystem}) is affected by {AuditText.Sanitize(a.Id, 80)} ({a.SeverityName}).")
                    {
                        Layer = Layer, Category = "Vulnerability", Package = pkg.Name, PackageVersion = pkg.Version, AdvisoryId = a.Id
                    };
                    var severity = Math.Min(a.Severity, 3);
                    var accepted = baseline?.Accepts(pkg.Name, pkg.Version, a.Id, severity) == true || existingDebt?.Accepts(pkg.Name, pkg.Version, a.Id, severity) == true;
                    vulnerabilities.Add((finding, accepted, a.IsBlocking));
                    matches.Add(new OsvMatch(pkg, a, affected, accepted));
                }
            }
        }

        debt.AddRange(vulnerabilities.Where(v => v.Accepted).Select(v => v.Finding));
        findings.AddRange(vulnerabilities.Where(v => !v.Accepted).Select(v => v.Finding));
        var blocking = vulnerabilities.Any(v => !v.Accepted && v.Blocking);

        var distinct = read.Packages.Select(p => p.Name + "@" + p.Version).Distinct(StringComparer.Ordinal).Count();
        var coverage = $"{distinct} distinct locked {ecosystem.OsvEcosystem} package version(s) checked";
        var debtNote = debt.Count == 0 ? string.Empty : $" {debt.Count} advisory finding(s) accepted as existing debt.";

        AuditLayerOutcome Result(ArtifactAuditState state, string reason, string message) =>
            Out(state, reason, message, dataVersion, findings) with
            {
                BaselineDebt = debt,
                Remediation = OsvRemediationPlanner.Plan(ecosystem.OsvEcosystem, matches, ecosystem.Comparer)
            };

        if (blocking)
            return Result(ArtifactAuditState.Fail, ArtifactAuditReasons.VulnerableHigh, $"High/Critical vulnerable dependencies found; {coverage}.{debtNote}");
        if (needsReview || findings.Count > 0)
        {
            var reason = findings.Any(f => f.Category == "Vulnerability") ? ArtifactAuditReasons.VulnerableLow : ArtifactAuditReasons.UnsupportedEcosystem;
            return Result(ArtifactAuditState.NeedsReview, reason, $"Existing lower-severity or uncovered dependency risk requires a human decision; {coverage}.{debtNote}");
        }

        return Result(ArtifactAuditState.Pass, ArtifactAuditReasons.Clean,
            debt.Count > 0 ? $"No unaccepted vulnerable dependencies; {coverage}.{debtNote}" : $"No known vulnerable dependencies; {coverage}.");
    }

    private AuditLayerOutcome Out(ArtifactAuditState state, string reason, string message, string data, IReadOnlyList<ArtifactAuditFinding> findings) =>
        new(Layer, state, reason, message, ecosystem.ToolName, string.Empty, data, findings);
}

/// <summary>npm: <c>package-lock.json</c> next to every <c>package.json</c>; only lockfileVersion 2 and 3.</summary>
public sealed class NpmEcosystem : ILockfileEcosystem
{
    public string OsvEcosystem => "npm";
    public string ToolName => "osv-npm-offline";
    public IVersionComparer Comparer => SemVerComparer.Instance;

    public IReadOnlyList<string> ClaimedManifests(SnapshotInventory inventory)
        => inventory.UnsupportedManifests.Where(p => Name(p) is "package.json" or "package-lock.json").ToList();

    public async Task<LockfileRead> ReadAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct)
    {
        var claimed = ClaimedManifests(inventory);
        var lockfiles = claimed.Where(p => Name(p) == "package-lock.json").ToList();
        var packages = new List<LockedPackage>();
        var risks = new List<ArtifactAuditFinding>();

        // A package.json without a lockfile next to it fixes nothing: the installed versions are whatever resolves at install time.
        foreach (var manifest in claimed.Where(p => Name(p) == "package.json"))
        {
            var directory = Directory(manifest);
            if (!lockfiles.Contains(directory.Length == 0 ? "package-lock.json" : directory + "/package-lock.json", StringComparer.Ordinal))
                risks.Add(Risk(manifest, "package.json has no package-lock.json next to it; the resolved package versions are unknown and not covered by any vulnerability check."));
        }

        foreach (var lockfile in lockfiles)
        {
            var text = await session.RunAsync(new(["cat", "--", "./" + lockfile], Timeout: TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);
            if (!AuditText.CommandSucceeded(text))
                return new LockfileRead([], [], $"Could not read '{AuditText.Sanitize(lockfile, 160)}' ({text.Status}).");
            if (!NpmLockfile.TryParse(text.Stdout, lockfile, out var parsed, out var error))
                return new LockfileRead([], [], $"{AuditText.Sanitize(lockfile, 160)}: {error}");
            packages.AddRange(parsed);
        }

        return new LockfileRead(packages, risks, null);
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
    private static string Directory(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static ArtifactAuditFinding Risk(string path, string evidence) =>
        new(AuditText.Fingerprint("dependencies", "npm", "unlocked", path), "Unknown", path, evidence)
        { Layer = "dependencies", Category = ArtifactAuditReasons.UnsupportedEcosystem };
}

/// <summary>Python: exact pins in <c>requirements*.txt</c>; anything left open, and a <c>pyproject.toml</c> with no pins beside it, is reported as uncovered.</summary>
public sealed class PythonEcosystem : ILockfileEcosystem
{
    public string OsvEcosystem => "PyPI";
    public string ToolName => "osv-pypi-offline";
    public IVersionComparer Comparer => Pep440Comparer.Instance;

    public IReadOnlyList<string> ClaimedManifests(SnapshotInventory inventory)
        => inventory.UnsupportedManifests.Where(p => IsRequirements(Name(p)) || Name(p) == "pyproject.toml").ToList();

    public async Task<LockfileRead> ReadAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct)
    {
        var claimed = ClaimedManifests(inventory);
        var requirementFiles = claimed.Where(p => IsRequirements(Name(p))).ToList();
        var packages = new List<LockedPackage>();
        var risks = new List<ArtifactAuditFinding>();

        foreach (var pyproject in claimed.Where(p => Name(p) == "pyproject.toml"))
        {
            var directory = Directory(pyproject);
            if (!requirementFiles.Any(r => Directory(r) == directory))
                risks.Add(Risk(pyproject, "pyproject.toml has no pinned requirements file next to it; the resolved package versions are unknown and not covered by any vulnerability check."));
        }

        foreach (var file in requirementFiles)
        {
            var text = await session.RunAsync(new(["cat", "--", "./" + file], Timeout: TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);
            if (!AuditText.CommandSucceeded(text))
                return new LockfileRead([], [], $"Could not read '{AuditText.Sanitize(file, 160)}' ({text.Status}).");
            var read = RequirementsFile.Parse(text.Stdout, file);
            packages.AddRange(read.Pins);
            if (read.Open.Count > 0)
                risks.Add(Risk(file, $"{read.Open.Count} requirement line(s) are not exact pins or pull in other files (for example '{AuditText.Sanitize(read.Open[0], 60)}'); their versions are unknown and not covered by any vulnerability check."));
        }

        return new LockfileRead(packages, risks, null);
    }

    private static bool IsRequirements(string name) => name.StartsWith("requirements", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal);
    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
    private static string Directory(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static ArtifactAuditFinding Risk(string path, string evidence) =>
        new(AuditText.Fingerprint("dependencies", "pypi", "unpinned", path), "Unknown", path, evidence)
        { Layer = "dependencies", Category = ArtifactAuditReasons.UnsupportedEcosystem };
}
