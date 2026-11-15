// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;

namespace CodexFlow.Services.ArtifactAudit;

/// <summary>Result of the one-shot snapshot listing (project files, other-ecosystem manifests, nuget.config, leftovers of a used snapshot).</summary>
public sealed record SnapshotInventory(
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<string> NuGetConfigs,
    IReadOnlyList<string> UnsupportedManifests,
    IReadOnlyList<string> LegacyPackagesConfigs,
    IReadOnlyList<string> UsedSnapshotMarkers,
    IReadOnlyList<string> UnsafePaths)
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] OtherEcosystemNames =
    [
        "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "pipfile", "pipfile.lock", "pyproject.toml", "poetry.lock",
        "pom.xml", "build.gradle", "build.gradle.kts", "go.mod", "cargo.toml", "cargo.lock", "gemfile", "gemfile.lock", "composer.json", "composer.lock"
    ];

    /// <summary>Guest <c>find</c> arguments. Names only; no content is interpreted by the guest shell (argv, no shell).</summary>
    public static IReadOnlyList<string> FindArgv() =>
    [
        "find", ".", "-name", "node_modules", "-prune", "-o", "-type", "f", "(",
        "-iname", "*.csproj", "-o", "-iname", "*.fsproj", "-o", "-iname", "*.vbproj", "-o",
        "-iname", "nuget.config", "-o", "-iname", "packages.config", "-o", "-iname", "project.assets.json", "-o", "-iname", "*.trx", "-o",
        "-iname", "package.json", "-o", "-iname", "package-lock.json", "-o", "-iname", "yarn.lock", "-o", "-iname", "pnpm-lock.yaml", "-o",
        "-iname", "pipfile", "-o", "-iname", "pipfile.lock", "-o", "-iname", "pyproject.toml", "-o", "-iname", "poetry.lock", "-o",
        "-iname", "pom.xml", "-o", "-iname", "build.gradle", "-o", "-iname", "build.gradle.kts", "-o", "-iname", "go.mod", "-o",
        "-iname", "cargo.toml", "-o", "-iname", "cargo.lock", "-o", "-iname", "gemfile", "-o", "-iname", "gemfile.lock", "-o",
        "-iname", "composer.json", "-o", "-iname", "composer.lock", "-o", "-iname", "requirements*.txt",
        ")", "-print"
    ];

    public static SnapshotInventory Parse(string findOutput)
    {
        var projects = new List<string>();
        var configs = new List<string>();
        var others = new List<string>();
        var legacy = new List<string>();
        var used = new List<string>();
        var unsafePaths = new List<string>();
        foreach (var raw in findOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var path = AuditText.NormalizeRelativePath(line);
            if (path is null || path.StartsWith('-'))
            {
                unsafePaths.Add(AuditText.Sanitize(line, 200));
                continue;
            }
            var name = path[(path.LastIndexOf('/') + 1)..];
            var lower = name.ToLowerInvariant();
            if (lower == "project.assets.json" || path.Contains(".codex-verification-results/", StringComparison.Ordinal)) used.Add(path);
            else if (lower == "nuget.config") configs.Add(path);
            else if (lower == "packages.config") legacy.Add(path);
            else if (ProjectExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal))) projects.Add(path);
            else if (OtherEcosystemNames.Contains(lower) || (lower.StartsWith("requirements", StringComparison.Ordinal) && lower.EndsWith(".txt", StringComparison.Ordinal))) others.Add(path);
        }
        return new SnapshotInventory(projects.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList(), configs, others, legacy, used, unsafePaths);
    }
}

public sealed record ResolvedPackage(string Id, string Version, bool Transitive, string Project, string Framework);

/// <summary>Strict reader of <c>dotnet list package --include-transitive --format json</c>. Anything unexpected is a coverage error.</summary>
public static class DotnetListPackageParser
{
    public static bool TryParse(string json, out List<ResolvedPackage> packages, out string error)
    {
        packages = [];
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() == 0)
            {
                error = "no projects in dotnet list output";
                return false;
            }
            foreach (var problemsKey in new[] { "problems", "errors", "logs" })
            {
                if (root.TryGetProperty(problemsKey, out var problems) && problems.ValueKind == JsonValueKind.Array && problems.GetArrayLength() > 0)
                {
                    error = $"dotnet list reported {problemsKey}";
                    return false;
                }
            }
            foreach (var project in projects.EnumerateArray())
            {
                var path = project.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
                if (path.Length == 0) { error = "project entry has no path"; return false; }
                if (path.StartsWith("/workspace/", StringComparison.Ordinal)) path = path["/workspace/".Length..];
                if (project.TryGetProperty("problems", out var pp) && pp.ValueKind == JsonValueKind.Array && pp.GetArrayLength() > 0) { error = $"project '{AuditText.Sanitize(path, 120)}' reported problems"; return false; }
                if (!project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array || frameworks.GetArrayLength() == 0)
                {
                    error = $"project '{AuditText.Sanitize(path, 120)}' has no resolved frameworks (not restored?)";
                    return false;
                }
                foreach (var fw in frameworks.EnumerateArray())
                {
                    var framework = fw.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : string.Empty;
                    if (framework.Length == 0) { error = "framework entry has no name"; return false; }
                    foreach (var (group, transitive) in new[] { ("topLevelPackages", false), ("transitivePackages", true) })
                    {
                        if (!fw.TryGetProperty(group, out var list)) continue;
                        if (list.ValueKind != JsonValueKind.Array) { error = $"{group} is malformed"; return false; }
                        foreach (var pkg in list.EnumerateArray())
                        {
                            var id = pkg.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                            var version = pkg.TryGetProperty("resolvedVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version)) { error = "package entry without id/resolved version (unresolved package)"; return false; }
                            packages.Add(new ResolvedPackage(id, version, transitive, path, framework));
                        }
                    }
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "dotnet list output is not valid JSON: " + ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Dependency layer. The guest stays offline: packages come only from the image/read-only restore cache. The vulnerability
/// data is a host-managed, validated snapshot of the official NuGet VulnerabilityInfo feed, matched on the host against
/// the exact resolved package versions (direct and transitive) that restore produced for the fixed snapshot.
/// </summary>
public sealed class NuGetDependencyScanner(ArtifactAuditOptions options, INuGetAdvisorySnapshotProvider advisories, TimeProvider time)
{
    public const string ToolName = "nuget-advisory-offline";
    private const string Layer = "dependencies";

    /// <param name="existingDebt">
    /// Advisories present at the chain's base commit that the approving user accepted with the plan. They are reported as accepted debt
    /// (never as findings); everything the change introduces still blocks.
    /// </param>
    public async Task<AuditLayerOutcome> ScanAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct, ExistingDependencyDebt? existingDebt = null)
    {
        var findings = new List<ArtifactAuditFinding>();
        var notes = new List<string>();
        var sdk = await session.RunAsync(new(["dotnet", "--version"], Timeout: TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);
        var sdkVersion = AuditText.CommandSucceeded(sdk) ? AuditText.Sanitize(sdk.Stdout, 40) : string.Empty;
        var tool = sdkVersion.Length > 0 ? "dotnet-sdk " + sdkVersion : string.Empty;

        // 1. Advisory data first: with stale/absent data nothing below can ever produce Pass.
        AdvisorySnapshotResult advisory;
        try
        {
            advisory = await advisories.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryUnavailable, "Advisory data provider timed out.", tool, string.Empty, findings);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryUnavailable, "Advisory data provider failed: " + AuditText.Sanitize(ex.Message, 200), tool, string.Empty, findings);
        }

        if (advisory.Status != AdvisoryStatus.Ok || advisory.Snapshot is null)
        {
            var reason = advisory.Status switch { AdvisoryStatus.Stale => ArtifactAuditReasons.AdvisoryStale, AdvisoryStatus.Invalid => ArtifactAuditReasons.AdvisoryInvalid, _ => ArtifactAuditReasons.AdvisoryUnavailable };
            return Out(ArtifactAuditState.Error, reason, "Vulnerability data not usable: " + AuditText.Sanitize(advisory.Message, 300), tool, string.Empty, findings);
        }

        var snapshot = advisory.Snapshot;
        var dataVersion = snapshot.Version;
        var now = time.GetUtcNow();
        var adv = options.Advisory;
        if (snapshot.Packages.Count < adv.MinimumPackageEntries || string.IsNullOrWhiteSpace(snapshot.Sha256))
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryInvalid, "Vulnerability data failed the minimum coverage check.", tool, dataVersion, findings);
        if (snapshot.RetrievedAtUtc > now + TimeSpan.FromMinutes(5) || now - snapshot.RetrievedAtUtc > adv.MaxAge)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryStale, $"Vulnerability data was retrieved {snapshot.RetrievedAtUtc:u}, older than the allowed {adv.MaxAge}.", tool, dataVersion, findings);
        if (now - snapshot.SourceUpdatedUtc > adv.MaxSourceAge)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryStale, $"The advisory feed itself was last updated {snapshot.SourceUpdatedUtc:u}, older than the allowed {adv.MaxSourceAge}.", tool, dataVersion, findings);

        // Reviewed baseline of pre-existing advisories (optional). A configured but unusable baseline is an Error: never ignored, never widened.
        var (baseline, baselineError) = DependencyBaselineLoader.Load(options.DependencyBaseline.Path, options.DependencyBaseline.ExpectedSha256, now);
        if (baselineError is not null)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.BaselineInvalid, baselineError, tool, dataVersion, findings);
        if (baseline is not null)
            dataVersion += "; baseline=sha256:" + baseline.Sha256[..16];

        // 2. Coverage preconditions that make a Pass impossible.
        if (inventory.UnsafePaths.Count > 0)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, $"{inventory.UnsafePaths.Count} project/manifest path(s) have unsupported characters and cannot be scanned.", tool, dataVersion, findings);
        if (inventory.ProjectFiles.Count > options.MaxProjects)
            return Out(ArtifactAuditState.NeedsReview, ArtifactAuditReasons.CoverageIncomplete, $"{inventory.ProjectFiles.Count} projects exceed the scan limit of {options.MaxProjects}.", tool, dataVersion, findings);

        var needsReview = false; // non-vulnerability risk (unsupported ecosystem, non-standard source); baseline debt never sets it
        foreach (var manifest in inventory.UnsupportedManifests)
        {
            needsReview = true;
            findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "ecosystem", manifest), "Unknown", manifest,
                "Dependency manifest of an ecosystem this gate cannot scan (NuGet only); not covered by any vulnerability check.")
            { Layer = Layer, Category = "UnsupportedEcosystem" });
        }
        foreach (var legacy in inventory.LegacyPackagesConfigs)
        {
            needsReview = true;
            findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "packages.config", legacy), "Unknown", legacy,
                "Legacy packages.config dependencies are not covered by the restore-based scan.")
            { Layer = Layer, Category = "UnsupportedEcosystem" });
        }

        // 3. Package sources: the guest has no network, but a repository feed could still smuggle fake package identities.
        foreach (var config in inventory.NuGetConfigs)
        {
            var cat = await session.RunAsync(new(["cat", "--", "./" + config], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
            var sources = AuditText.CommandSucceeded(cat) ? NonStandardSources(cat.Stdout) : null;
            if (sources is null || sources.Count > 0)
            {
                needsReview = true;
                findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "source", config), "Unknown", config,
                    sources is null ? "nuget.config could not be read or parsed." : "nuget.config declares package sources other than the official nuget.org feed: " + AuditText.Sanitize(string.Join(", ", sources), 200))
                { Layer = Layer, Category = ArtifactAuditReasons.NonStandardPackageSource });
            }
        }

        // 3b. Freeze the restore-relevant manifest on the pristine tree and flag source/location overrides in project files.
        var manifestBefore = await ManifestAsync(session, ct).ConfigureAwait(false);
        if (manifestBefore is null)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, "Could not fingerprint the project/props/targets/lock-file manifest.", tool, dataVersion, findings);
        var markers = await session.RunAsync(new(NonStandardMarkerGrepArgv(), Timeout: TimeSpan.FromMinutes(1)), ct).ConfigureAwait(false);
        if (markers.Decision.IsAllowed && markers.ExitCode == 0 && markers.Stdout.Trim().Length > 0)
        {
            needsReview = true;
            foreach (var file in markers.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => AuditText.NormalizeRelativePath(l.Trim())).OfType<string>().Take(20))
                findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "restore-override", file), "Unknown", file,
                    "Project file sets NuGet restore source/location properties; restore ignores them (trusted source is enforced) but this is a non-standard package-source attempt.")
                { Layer = Layer, Category = ArtifactAuditReasons.NonStandardPackageSource });
        }
        else if (!markers.Decision.IsAllowed || markers.ExitCode != 1)
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, "Scanning project files for restore-source overrides failed.", tool, dataVersion, findings);
        var cachePresent = (await session.RunAsync(new(["test", "-d", "/restore-cache"], Timeout: TimeSpan.FromSeconds(20)), ct).ConfigureAwait(false)).ExitCode == 0;
        // Packages the host fetched for this session (security upgrades the base cache lacks): a second read-only fallback folder.
        var overlayPresent = (await session.RunAsync(new(["test", "-d", "/restore-cache-extra"], Timeout: TimeSpan.FromSeconds(20)), ct).ConfigureAwait(false)).ExitCode == 0;
        // "%3B", not ";": on an MSBuild command line a bare ";" separates properties, so the second folder became an invalid switch (MSB1006).
        var fallbackFolders = string.Join("%3B", new[] { cachePresent ? "/restore-cache" : null, overlayPresent ? "/restore-cache-extra" : null }.OfType<string>());

        // 3c. NuGet-resolved MSBuild SDKs (<Sdk Name=.. Version=../>, Sdk="Name/Version") are resolved from the global packages folder only
        // (not from fallback folders), so seed exactly those packages from the trusted read-only cache. Project text only selects
        // which cache folders to copy (ids/versions are strictly validated); a missing package makes restore fail honestly.
        if (cachePresent)
        {
            var seeds = new SortedSet<(string Id, string Version)>();
            foreach (var project in inventory.ProjectFiles)
            {
                var text = await session.RunAsync(new(["cat", "--", "./" + project], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
                if (!AuditText.CommandSucceeded(text)) continue;
                foreach (var seed in NuGetSdkReferences(text.Stdout)) seeds.Add(seed);
            }
            foreach (var (id, version) in seeds.Take(20))
            {
                var lower = id.ToLowerInvariant();
                var target = TrustedPackagesPath + "/" + lower;
                await session.RunAsync(new(["mkdir", "-p", "--", target], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
                await session.RunAsync(new(["cp", "-a", "--", "/restore-cache/" + lower + "/" + version.ToLowerInvariant(), target + "/"], Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
            }
        }

        // 4. Restore (offline, trusted source/locations enforced as global properties) + exact resolved inventory per project.
        var resolved = new List<ResolvedPackage>();
        // Any failure after project code ran is first checked for tampering, so the reason is the root cause.
        async Task<AuditLayerOutcome?> TamperedAsync()
        {
            var again = await ManifestAsync(session, ct).ConfigureAwait(false);
            return again is not null && string.Equals(manifestBefore, again, StringComparison.Ordinal) ? null
                : Out(ArtifactAuditState.Error, ArtifactAuditReasons.ManifestChanged, "Project/props/targets/lock files changed (or could not be re-read) during restore; the resolved package list is not trustworthy evidence.", tool, dataVersion, findings);
        }

        foreach (var project in inventory.ProjectFiles)
        {
            string[] RestoreArgv(params string[] extra) => ["dotnet", "restore", "./" + project, "--source", OfficialSource, "-p:NuGetAudit=false",
                "-p:RestorePackagesPath=" + TrustedPackagesPath, "-p:RestoreAdditionalProjectSources=", "-p:RestoreFallbackFolders=",
                "-p:RestoreAdditionalProjectFallbackFolders=" + fallbackFolders, "-p:RestoreIgnoreFailedSources=true", "--nologo", "--verbosity", "quiet", .. extra];
            var restore = await session.RunAsync(new(RestoreArgv(), Timeout: options.RestoreTimeout), ct).ConfigureAwait(false);
            if (!AuditText.CommandSucceeded(restore))
            {
                // "Unable to load the service index" names nothing. Restoring once more while ignoring the unreachable source reports what NuGet
                // could not find locally, which is what whoever has to fix the project needs to read.
                var hint = string.Empty;
                if (restore.Stdout.Contains("NU1301", StringComparison.Ordinal) || restore.Stderr.Contains("NU1301", StringComparison.Ordinal))
                {
                    var diagnostic = await session.RunAsync(new(RestoreArgv("--ignore-failed-sources"), Timeout: options.RestoreTimeout), ct).ConfigureAwait(false);
                    hint = NuGetRestoreDiagnostics.DescribeUnavailableLowerBounds(diagnostic.Stdout + " " + diagnostic.Stderr);
                }

                return await TamperedAsync() ?? Out(ArtifactAuditState.Error, ArtifactAuditReasons.RestoreFailed, $"Offline restore failed for '{AuditText.Sanitize(project, 160)}' ({restore.Status}/{restore.Decision.ReasonCode}/exit {restore.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}): {AuditText.Redact(Tail(restore.Stdout + " " + restore.Stderr), 240)}{hint}", tool, dataVersion, findings);
            }
            var list = await session.RunAsync(new(["dotnet", "list", "./" + project, "package", "--include-transitive", "--no-restore", "--format", "json"], Timeout: options.ListPackageTimeout), ct).ConfigureAwait(false);
            if (!AuditText.CommandSucceeded(list))
                return await TamperedAsync() ?? Out(ArtifactAuditState.Error, ArtifactAuditReasons.CoverageIncomplete, $"Could not list resolved packages for '{AuditText.Sanitize(project, 160)}' ({list.Status}/exit {list.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}).", tool, dataVersion, findings);
            if (!DotnetListPackageParser.TryParse(list.Stdout, out var packages, out var parseError))
                return await TamperedAsync() ?? Out(ArtifactAuditState.Error, ArtifactAuditReasons.MalformedOutput, $"{AuditText.Sanitize(parseError, 200)} (project '{AuditText.Sanitize(project, 160)}').", tool, dataVersion, findings);
            resolved.AddRange(packages);
        }

        // The package list above was produced by running project-controlled MSBuild; it is only credible if the manifest it was derived from is unchanged.
        var manifestAfter = await ManifestAsync(session, ct).ConfigureAwait(false);
        if (manifestAfter is null || !string.Equals(manifestBefore, manifestAfter, StringComparison.Ordinal))
            return Out(ArtifactAuditState.Error, ArtifactAuditReasons.ManifestChanged, "Project/props/targets/lock files changed (or could not be re-read) during restore; the resolved package list is not trustworthy evidence.", tool, dataVersion, findings);

        // 5. Match every resolved package against the snapshot. Unparsable versions/ranges fail closed.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<VulnerabilityMatch>();
        foreach (var pkg in resolved.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal))
        {
            if (!snapshot.Packages.TryGetValue(pkg.Id.ToLowerInvariant(), out var advisoriesForPackage)) continue;
            foreach (var a in advisoriesForPackage)
            {
                var affected = NuGetAdvisoryMatcher.Affects(a, pkg.Version);
                if (affected is null)
                    return Out(ArtifactAuditState.Error, ArtifactAuditReasons.AdvisoryInvalid, $"Cannot evaluate advisory range '{AuditText.Sanitize(a.VersionRange, 60)}' against {AuditText.Sanitize(pkg.Id, 80)} {AuditText.Sanitize(pkg.Version, 40)}.", tool, dataVersion, findings);
                if (affected != true || !seen.Add($"{pkg.Id.ToLowerInvariant()}|{pkg.Version}|{a.Url}|{pkg.Project}")) continue;
                var finding = new ArtifactAuditFinding(
                    AuditText.Fingerprint(Layer, "vuln", pkg.Id.ToLowerInvariant(), pkg.Version, a.Id, pkg.Project),
                    a.SeverityName, pkg.Project,
                    $"{AuditText.Sanitize(pkg.Id, 100)} {AuditText.Sanitize(pkg.Version, 50)} ({(pkg.Transitive ? "transitive" : "direct")}, {AuditText.Sanitize(pkg.Framework, 40)}) is affected by {AuditText.Sanitize(a.Id, 80)} [{a.SeverityName}], vulnerable range {AuditText.Sanitize(a.VersionRange, 60)}; advisory {AuditText.Sanitize(a.Url, 160)}.")
                {
                    Layer = Layer, Category = "Vulnerability", Package = pkg.Id, PackageVersion = pkg.Version, AdvisoryId = a.Id
                };
                matches.Add(new VulnerabilityMatch(pkg, a, finding,
                    baseline?.Accepts(pkg.Id, pkg.Version, a.Id, a.Severity) == true || existingDebt?.Accepts(pkg.Id, pkg.Version, a.Id, a.Severity) == true));
            }
        }

        // Accepted debt stays visible (BaselineDebt + remediation plan) but leaves Findings, which must be empty for a Pass.
        var debt = matches.Where(m => m.BaselineAccepted).Select(m => m.Finding).ToList();
        findings.AddRange(matches.Where(m => !m.BaselineAccepted).Select(m => m.Finding));
        var blocking = matches.Any(m => !m.BaselineAccepted && m.Advisory.IsBlocking);
        var remediation = DependencyRemediationPlanner.Plan(matches);

        var distinctPackages = resolved.Select(p => p.Id.ToLowerInvariant() + "@" + p.Version).Distinct().Count();
        var coverage = $"{inventory.ProjectFiles.Count} project(s), {distinctPackages} distinct resolved package version(s) checked";
        var planDebt = existingDebt is null
            ? []
            : matches.Where(m => m.BaselineAccepted && baseline?.Accepts(m.Package.Id, m.Package.Version, m.Advisory.Id, m.Advisory.Severity) != true).Select(m => m.Finding).ToList();
        var fileDebt = debt.Count - planDebt.Count;
        var debtNote = string.Concat(
            baseline is null || fileDebt == 0 ? string.Empty
                : $" {fileDebt} advisory finding(s) accepted as baseline debt (reviewed by {AuditText.Sanitize(baseline.ReviewedBy, 60)}, expires {baseline.ExpiresAtUtc:u}, sha256:{baseline.Sha256[..16]}).",
            existingDebt is null || planDebt.Count == 0 ? string.Empty
                : $" {planDebt.Count} advisory finding(s) on {planDebt.Select(f => f.Package + "@" + f.PackageVersion).Distinct().Count()} package version(s) already existed at chain base {existingDebt.BaseCommit[..7]} and were accepted with the plan approved by {AuditText.Sanitize(existingDebt.ApprovedBy, 60)}.");
        if (planDebt.Count > 0) dataVersion += "; existing-debt-base=" + existingDebt!.BaseCommit[..7];
        var planNote = remediation.Count == 0 ? string.Empty : $" Remediation plan: {remediation.Count} package version(s).";

        AuditLayerOutcome Result(ArtifactAuditState state, string reason, string message) =>
            Out(state, reason, message, tool, dataVersion, findings) with { BaselineDebt = debt, Remediation = remediation };

        if (blocking)
            return Result(ArtifactAuditState.Fail, ArtifactAuditReasons.VulnerableHigh, $"High/Critical vulnerable dependencies found; {coverage}.{planNote}{debtNote}");
        if (needsReview || findings.Count > 0)
        {
            var reason = findings.Any(f => f.Category == "Vulnerability") ? ArtifactAuditReasons.VulnerableLow
                : findings.Any(f => f.Category == ArtifactAuditReasons.NonStandardPackageSource) ? ArtifactAuditReasons.NonStandardPackageSource : ArtifactAuditReasons.UnsupportedEcosystem;
            return Result(ArtifactAuditState.NeedsReview, reason, $"Existing lower-severity or uncovered dependency risk requires a human decision; {coverage}.{planNote}{debtNote}");
        }

        return Result(ArtifactAuditState.Pass, ArtifactAuditReasons.Clean,
            debt.Count > 0 ? $"No unaccepted vulnerable dependencies; {coverage}.{debtNote}{planNote}" : $"No known vulnerable dependencies; {coverage}.");
    }

    private static readonly System.Text.RegularExpressions.Regex SdkElement = new(
        @"<Sdk\s+Name=""(?<id>[A-Za-z0-9][A-Za-z0-9._-]{0,127})""\s+Version=""(?<v>[0-9][0-9A-Za-z.+-]{0,63})""|Sdk=""(?<id2>[A-Za-z0-9][A-Za-z0-9._-]{0,127})/(?<v2>[0-9][0-9A-Za-z.+-]{0,63})""",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>NuGet-resolved MSBuild SDK references of a project file (built-in Microsoft.NET.Sdk* excluded).</summary>
    public static IEnumerable<(string Id, string Version)> NuGetSdkReferences(string projectText)
    {
        System.Text.RegularExpressions.MatchCollection matches;
        try { matches = SdkElement.Matches(projectText); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { yield break; }
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var id = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
            var v = m.Groups["v"].Success ? m.Groups["v"].Value : m.Groups["v2"].Value;
            if (id.StartsWith("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (id, v);
        }
    }

    public const string OfficialSource = "https://api.nuget.org/v3/index.json";
    private const string TrustedPackagesPath = "/tmp/home/.nuget/packages";

    /// <summary>Every file that can change what restore resolves (projects, props/targets, lock files, configs); obj/node_modules excluded.</summary>
    public static IReadOnlyList<string> ManifestArgv() =>
    [
        "find", ".", "(", "-name", "node_modules", "-o", "-name", "obj", ")", "-prune", "-o", "-type", "f", "(",
        "-iname", "*.csproj", "-o", "-iname", "*.fsproj", "-o", "-iname", "*.vbproj", "-o", "-iname", "*.props", "-o", "-iname", "*.targets", "-o",
        "-iname", "*.sln", "-o", "-iname", "*.slnx", "-o", "-iname", "packages.lock.json", "-o", "-iname", "nuget.config", "-o",
        "-iname", "global.json", "-o", "-iname", "*.rsp", "-o", "-iname", "*.nuspec", ")", "-exec", "sha256sum", "--", "{}", "+"
    ];

    public static IReadOnlyList<string> NonStandardMarkerGrepArgv() =>
    [
        "grep", "-rIlE", "--exclude-dir=node_modules", "--exclude-dir=obj",
        "--include=*.props", "--include=*.targets", "--include=*.csproj", "--include=*.fsproj", "--include=*.vbproj", "--include=*.rsp",
        "-e", "RestoreSources|RestoreAdditionalProjectSources|RestoreFallbackFolders|RestoreAdditionalProjectFallbackFolders|RestorePackagesPath|NuGetPackageRoot|RestoreConfigFile|RestoreLockedMode|PackageSourceCredential|RestoreUseStaticGraphEvaluation",
        "."
    ];

    private static async Task<string?> ManifestAsync(IIsolatedExecutionSession session, CancellationToken ct)
    {
        var r = await session.RunAsync(new(ManifestArgv(), Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
        if (!AuditText.CommandSucceeded(r)) return null;
        return string.Join('\n', r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).OrderBy(l => l, StringComparer.Ordinal));
    }

    private static string Tail(string text) => text.Length <= 400 ? text : text[^400..];

    /// <summary>Source values other than the official feed. Null when the file cannot be parsed safely.</summary>
    public static List<string>? NonStandardSources(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            var doc = XDocument.Load(reader);
            var result = new List<string>();
            foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "packageSources"))
            {
                var value = add.Attribute("value")?.Value?.Trim() ?? string.Empty;
                if (!string.Equals(value.TrimEnd('/'), "https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase)) result.Add(value.Length == 0 ? "(empty)" : value);
            }
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "add" && (e.Parent?.Name.LocalName == "fallbackPackageFolders" ||
                         (e.Parent?.Name.LocalName == "config" && e.Attribute("key")?.Value is { } k && (k.Equals("globalPackagesFolder", StringComparison.OrdinalIgnoreCase) || k.Equals("repositoryPath", StringComparison.OrdinalIgnoreCase))))))
                result.Add("folder:" + (e.Attribute("value")?.Value ?? "(empty)"));
            return result;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static AuditLayerOutcome Out(ArtifactAuditState state, string reason, string message, string tool, string data, IReadOnlyList<ArtifactAuditFinding> findings) =>
        new(Layer, state, reason, message, ToolName, tool, data, findings);
}
