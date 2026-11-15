// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in docs/dependency-baseline.md
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace CodexFlow.Services.ArtifactAudit;

public enum DependencyUpgradeStatus
{
    /// <summary>Policy approves it: the smallest fixed version, within the same major (and the same minor for 0.x).</summary>
    AutoApproved,

    /// <summary>A fixed version exists but moves across a major version: only the user can approve it.</summary>
    NeedsApproval,

    /// <summary>No published stable version satisfies every advisory, or the advisory has no fix.</summary>
    NoCandidate
}

/// <param name="Package">Package id.</param>
/// <param name="CurrentVersions">Every version of the package currently resolved (it can differ per project).</param>
/// <param name="TargetVersion">The host-chosen version, null for <see cref="DependencyUpgradeStatus.NoCandidate"/>.</param>
public sealed record DependencyUpgradeCandidate(
    string Package,
    IReadOnlyList<string> CurrentVersions,
    string? TargetVersion,
    DependencyUpgradeStatus Status,
    string Reason,
    bool Transitive,
    IReadOnlyList<string> Projects);

/// <summary>
/// Chooses the version a vulnerable package may move to. The model never chooses: it only edits the project to the version the host
/// fetched, so the version it can restore is the version the host decided on (smallest published stable version that EVERY advisory of
/// the package accepts and that is above everything currently resolved).
/// </summary>
public static class DependencyUpgradePlanner
{
    public static IReadOnlyList<DependencyUpgradeCandidate> Plan(
        IEnumerable<ArtifactDependencyRemediation> remediation,
        IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> advisories,
        Func<string, IReadOnlyList<string>> publishedVersions)
    {
        return remediation
            .GroupBy(r => r.Package, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => PlanPackage(group.Key, group.ToList(), advisories, publishedVersions))
            .ToList();
    }

    private static DependencyUpgradeCandidate PlanPackage(
        string package,
        IReadOnlyList<ArtifactDependencyRemediation> needs,
        IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> advisories,
        Func<string, IReadOnlyList<string>> publishedVersions)
    {
        var currentTexts = needs.Select(n => n.CurrentVersion).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var projects = needs.SelectMany(n => n.Projects).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var transitive = needs.All(n => n.Transitive);

        DependencyUpgradeCandidate None(string reason) => new(package, currentTexts, null, DependencyUpgradeStatus.NoCandidate, reason, transitive, projects);

        var currents = currentTexts.Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null).ToList();
        if (currents.Any(v => v is null)) return None("无法解析当前版本。");
        if (needs.Any(n => n.Action == "NoKnownFix")) return None("公告没有已知修复版本：替换或移除该依赖。");

        var highest = currents.Max()!;
        var atLeast = Max(needs.Select(n => n.MinimumFixedVersion));
        var exceed = Max(needs.Select(n => n.MustExceedVersion));
        var known = advisories.TryGetValue(package.ToLowerInvariant(), out var list) ? list : [];

        var target = publishedVersions(package)
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .OfType<NuGetVersion>()
            .Where(v => !v.IsPrerelease && v > highest && (atLeast is null || v >= atLeast) && (exceed is null || v > exceed))
            // Affected by ANY advisory of the package, or an advisory range that cannot be evaluated, disqualifies the version.
            .Where(v => known.All(a => NuGetAdvisoryMatcher.Affects(a, v.ToNormalizedString()) == false))
            .OrderBy(v => v)
            .FirstOrDefault();
        if (target is null) return None("没有已发布的稳定版本同时满足该包的全部公告。");

        var withinCompatibleRange = currents.All(c => target.Major == c!.Major && (c.Major != 0 || target.Minor == c.Minor));
        return new DependencyUpgradeCandidate(
            package, currentTexts, target.ToNormalizedString(),
            withinCompatibleRange ? DependencyUpgradeStatus.AutoApproved : DependencyUpgradeStatus.NeedsApproval,
            withinCompatibleRange ? "同主版本内的最小修复版本，按策略自动批准。" : "需要跨主版本（0.x 为次版本）升级，可能引入不兼容改动，需要用户批准。",
            transitive, projects);
    }

    private static NuGetVersion? Max(IEnumerable<string?> versions) =>
        versions.Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null).OfType<NuGetVersion>().DefaultIfEmpty().Max();
}

public interface INuGetVersionCatalog
{
    /// <summary>Every published version id of the package; empty for anything unknown, malformed or unreachable. Never throws for transport problems.</summary>
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct);

    /// <summary>
    /// The minimum version each dependency of one published package version asks for (from its nuspec, for the framework this host builds
    /// for); empty when unknown, unreachable or the package has no dependencies. Never throws for transport problems.
    /// </summary>
    Task<IReadOnlyList<PackageDependencyMinimum>> GetDependencyMinimumsAsync(string packageId, string version, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PackageDependencyMinimum>>([]);
}

/// <summary>A dependency of a package and the lowest version of it the package accepts.</summary>
public sealed record PackageDependencyMinimum(string Id, NuGetVersion Minimum);

/// <summary>Reads https://api.nuget.org/v3-flatcontainer/{id}/index.json: fixed host, a strictly validated id, bounded answer.</summary>
public sealed class NuGetFlatContainerVersionCatalog(HttpClient http) : INuGetVersionCatalog
{
    private const string BaseUrl = "https://api.nuget.org/v3-flatcontainer/";
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxVersions = 5000;
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValidPackageId(string? id) => id is not null && PackageId.IsMatch(id);

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        if (!IsValidPackageId(packageId)) return [];
        try
        {
            using var response = await http.GetAsync(BaseUrl + packageId.ToLowerInvariant() + "/index.json", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return [];
                buffer.Write(chunk, 0, read);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) return [];

            return versions.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .Where(v => v.Length is > 0 and <= 64)
                .Take(MaxVersions)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return [];
        }
    }

    private const int MaxNuspecBytes = 1024 * 1024;

    // Frameworks a package's dependency groups are searched in, best first; the group without a framework is the last resort.
    private static readonly string[] FrameworkPreference =
        ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0"];

    public async Task<IReadOnlyList<PackageDependencyMinimum>> GetDependencyMinimumsAsync(string packageId, string version, CancellationToken ct)
    {
        if (!IsValidPackageId(packageId) || version.Length is 0 or > 64 || !NuGetVersion.TryParse(version, out var parsedVersion)) return [];
        try
        {
            var id = packageId.ToLowerInvariant();
            var normalized = parsedVersion.ToNormalizedString().ToLowerInvariant();
            using var response = await http.GetAsync($"{BaseUrl}{id}/{normalized}/{id}.nuspec", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxNuspecBytes) return [];
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var reader = System.Xml.XmlReader.Create(buffer, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            return ParseDependencyMinimums(System.Xml.Linq.XDocument.Load(reader));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or OperationCanceledException or IOException)
        {
            return [];
        }
    }

    /// <summary>The dependencies of the best matching dependency group of a nuspec, each with the lowest version it accepts.</summary>
    public static IReadOnlyList<PackageDependencyMinimum> ParseDependencyMinimums(System.Xml.Linq.XDocument nuspec)
    {
        var groups = nuspec.Descendants().Where(e => e.Name.LocalName == "dependencies")
            .SelectMany(d => d.Elements().Where(e => e.Name.LocalName == "group")).ToList();
        var flat = nuspec.Descendants().Where(e => e.Name.LocalName == "dependencies")
            .SelectMany(d => d.Elements().Where(e => e.Name.LocalName == "dependency")).ToList();

        IEnumerable<System.Xml.Linq.XElement> chosen = flat;
        if (groups.Count > 0)
        {
            System.Xml.Linq.XElement? best = null;
            foreach (var framework in FrameworkPreference)
            {
                best = groups.FirstOrDefault(g => string.Equals(NormalizeFramework(g.Attribute("targetFramework")?.Value), framework, StringComparison.OrdinalIgnoreCase));
                if (best is not null) break;
            }

            best ??= groups.FirstOrDefault(g => string.IsNullOrWhiteSpace(g.Attribute("targetFramework")?.Value));
            chosen = best is null ? [] : best.Elements().Where(e => e.Name.LocalName == "dependency");
        }

        var result = new List<PackageDependencyMinimum>();
        foreach (var dependency in chosen)
        {
            var id = dependency.Attribute("id")?.Value;
            var range = dependency.Attribute("version")?.Value;
            if (!IsValidPackageId(id) || string.IsNullOrWhiteSpace(range) || !VersionRange.TryParse(range, out var parsed) || parsed.MinVersion is null) continue;
            result.Add(new PackageDependencyMinimum(id!, parsed.MinVersion));
        }

        return result;
    }

    // Nuspecs write a group's framework as "net8.0" or in long form (".NETCoreApp8.0", ".NETStandard2.0", ".NETCoreApp,Version=v8.0"): one name for all.
    private static string NormalizeFramework(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().Replace(",Version=v", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase)) return "netstandard" + text[12..];
        if (text.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var version = text[11..];
            // .NET 5 and later are "netN.0"; .NET Core 3.1 and earlier are "netcoreappN.N".
            return int.TryParse(version.Split('.')[0], out var major) && major >= 5 ? "net" + version : "netcoreapp" + version;
        }

        return text;
    }
}


/// <summary>
/// Makes the chosen targets agree with each other. Every target is the smallest version that fixes its own advisories, chosen on its own, so
/// one target can ask for more than another was given: Aspire.Hosting.AppHost 13.6.5 needs MessagePack 2.5.307 or newer while MessagePack was
/// chosen as 2.5.303. Restoring that mix is a package downgrade (NU1605), which cannot be restored offline, and the worker gets a task it cannot
/// finish. A target that is too low for a sibling target is raised to the smallest version that is both high enough and acceptable to every
/// advisory of the package, until nothing changes; one that cannot be raised is left as it is and fails honestly in the restore.
/// </summary>
public static class DependencyUpgradeConsistency
{
    private const int MaxRounds = 6;
    private const int MaxDepth = 6;
    private const int MaxNuspecReads = 120;

    public static async Task<IReadOnlyList<DependencyUpgradeCandidate>> RaiseAsync(
        IReadOnlyList<DependencyUpgradeCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> advisories,
        Func<string, IReadOnlyList<string>> publishedVersions,
        INuGetVersionCatalog catalog,
        CancellationToken ct)
    {
        var list = candidates.ToList();
        var asked = new Dictionary<string, IReadOnlyList<PackageDependencyMinimum>>(StringComparer.OrdinalIgnoreCase);

        async Task<IReadOnlyList<PackageDependencyMinimum>> MinimumsOfAsync(string package, string version)
        {
            var key = package + "@" + version;
            if (asked.TryGetValue(key, out var known)) return known;
            // The reads are bounded: a deep or wide graph stops being followed rather than being walked without end.
            if (asked.Count >= MaxNuspecReads) return [];
            var read = await catalog.GetDependencyMinimumsAsync(package, version, ct).ConfigureAwait(false);
            asked[key] = read;
            return read;
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var changed = false;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].TargetVersion is not { } target) continue;

                // Everything the target pulls in, followed through packages that are not targets themselves (the chain that broke was
                // Aspire.Hosting.AppHost -> OpenTelemetry.Exporter... -> OpenTelemetry -> OpenTelemetry.Api, with only the last a target).
                // A package is followed at the lowest version asked for, which is the version NuGet resolves it to.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { list[i].Package + "@" + target };
                var queue = new Queue<(PackageDependencyMinimum Need, int Depth)>();
                foreach (var need in await MinimumsOfAsync(list[i].Package, target).ConfigureAwait(false)) queue.Enqueue((need, 1));

                while (queue.Count > 0)
                {
                    var (need, depth) = queue.Dequeue();
                    var j = list.FindIndex(c => string.Equals(c.Package, need.Id, StringComparison.OrdinalIgnoreCase));
                    var version = need.Minimum;
                    if (j >= 0 && list[j].TargetVersion is { } chosen && NuGetVersion.TryParse(chosen, out var chosenVersion))
                    {
                        if (chosenVersion < need.Minimum)
                        {
                            var raised = Raise(list[j], need.Minimum, chosenVersion, list[i], advisories, publishedVersions);
                            if (raised is not null)
                            {
                                list[j] = raised;
                                changed = true;
                                chosenVersion = NuGetVersion.Parse(raised.TargetVersion!);
                            }
                        }

                        // A target is not followed through: its own requirements are checked when its turn comes.
                        continue;
                    }

                    if (depth >= MaxDepth || !seen.Add(need.Id + "@" + version.ToNormalizedString())) continue;
                    foreach (var next in await MinimumsOfAsync(need.Id, version.ToNormalizedString()).ConfigureAwait(false)) queue.Enqueue((next, depth + 1));
                }
            }

            if (!changed) break;
        }

        return list;
    }

    private static DependencyUpgradeCandidate? Raise(
        DependencyUpgradeCandidate candidate, NuGetVersion minimum, NuGetVersion chosen, DependencyUpgradeCandidate because,
        IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> advisories, Func<string, IReadOnlyList<string>> publishedVersions)
    {
        var known = advisories.TryGetValue(candidate.Package.ToLowerInvariant(), out var list) ? list : [];
        var target = publishedVersions(candidate.Package)
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .OfType<NuGetVersion>()
            .Where(v => !v.IsPrerelease && v >= minimum && v > chosen)
            .Where(v => known.All(a => NuGetAdvisoryMatcher.Affects(a, v.ToNormalizedString()) == false))
            .OrderBy(v => v)
            .FirstOrDefault();
        if (target is null) return null;

        var currents = candidate.CurrentVersions.Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null).ToList();
        var withinCompatibleRange = currents.All(c => c is not null && target.Major == c.Major && (c.Major != 0 || target.Minor == c.Minor));
        return candidate with
        {
            TargetVersion = target.ToNormalizedString(),
            Status = withinCompatibleRange ? DependencyUpgradeStatus.AutoApproved : DependencyUpgradeStatus.NeedsApproval,
            Reason = $"{candidate.Reason} 已提高到 {target.ToNormalizedString()}：{because.Package} {because.TargetVersion} 要求它不低于 {minimum.ToNormalizedString()}，否则无法一起还原。"
        };
    }
}

// ───────────────────────────── the overlay: fetching the packages with the reviewed cache builder ──────────────

/// <summary>Host configuration of the overlay builder. Without <see cref="Root"/> and <see cref="PrepareScript"/> the feature is off.</summary>
public sealed class RestoreOverlayOptions
{
    /// <summary>The same directory as <c>Isolation:RestoreOverlayRoot</c>: per-session overlays live below it.</summary>
    public string? Root { get; set; }

    /// <summary>Path of the reviewed cache builder (scripts/e2e/prepare-restore-cache.py): pinned image, nuget.org only, source verification, manifest.</summary>
    public string? PrepareScript { get; set; }

    public string Python { get; set; } = "python";
    public string TargetFramework { get; set; } = "net10.0";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record RestoreOverlayRunRequest(string Python, string Script, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout);

public sealed record RestoreOverlayRunResult(int ExitCode, string Output);

public interface IRestoreOverlayRunner
{
    Task<RestoreOverlayRunResult> RunAsync(RestoreOverlayRunRequest request, CancellationToken ct);
}

/// <summary>Runs the reviewed cache builder as a child process. Its only inputs are the three environment variables the script documents.</summary>
public sealed class ProcessRestoreOverlayRunner : IRestoreOverlayRunner
{
    public async Task<RestoreOverlayRunResult> RunAsync(RestoreOverlayRunRequest request, CancellationToken ct)
    {
        var start = new ProcessStartInfo(request.Python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-I"); // isolated mode: no user site, no PYTHON* variables, no current directory on the path
        start.ArgumentList.Add(request.Script);
        foreach (var (key, value) in request.Environment) start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false)).Trim();
            return new RestoreOverlayRunResult(process.ExitCode, text.Length > 4000 ? text[^4000..] : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RestoreOverlayRunResult(-1, "The overlay build timed out.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}

public sealed record RestoreOverlayResult(bool Success, string Message, string? Directory)
{
    public static RestoreOverlayResult Failed(string message) => new(false, message, null);
}

public interface IRestoreOverlayService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Makes the given exact packages (and their transitive closure) available to the session's network-less guests, and returns the
    /// directory they live in. The same set is built once; a different set gets its own directory and only then does the pointer switch.
    /// </summary>
    Task<RestoreOverlayResult> EnsureAsync(string sessionId, IReadOnlyCollection<(string Id, string Version)> packages, string reviewedBy, CancellationToken ct);
}

public sealed class RestoreOverlayService(
    IRestoreOverlayRunner runner,
    IOptions<RestoreOverlayOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<RestoreOverlayService>? logger = null) : IRestoreOverlayService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private const string ManifestFileName = "restore-cache-manifest.json";

    private RestoreOverlayOptions Options => options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Options.Root) && !string.IsNullOrWhiteSpace(Options.PrepareScript) &&
        !string.IsNullOrWhiteSpace(configuration["ArtifactVerification:Image"]);

    public async Task<RestoreOverlayResult> EnsureAsync(string sessionId, IReadOnlyCollection<(string Id, string Version)> packages, string reviewedBy, CancellationToken ct)
    {
        if (!IsConfigured) return RestoreOverlayResult.Failed("Host-side package acquisition is not configured (Isolation:RestoreOverlayRoot, Isolation:RestoreOverlayPrepareScript, ArtifactVerification:Image).");
        if (!RestoreOverlayLocator.IsValidSessionId(sessionId)) return RestoreOverlayResult.Failed("The session id is not valid for an overlay.");
        if (packages.Count == 0) return RestoreOverlayResult.Failed("There is nothing to fetch.");
        if (string.IsNullOrWhiteSpace(reviewedBy) || reviewedBy.Length > 100 || reviewedBy.Any(char.IsControl)) return RestoreOverlayResult.Failed("A reviewer is required.");

        var normalized = new SortedDictionary<string, (string Id, string Version)>(StringComparer.Ordinal);
        foreach (var (id, version) in packages)
        {
            if (!NuGetFlatContainerVersionCatalog.IsValidPackageId(id)) return RestoreOverlayResult.Failed($"'{AuditText.Sanitize(id, 60)}' is not a valid package id.");
            if (version.Any(c => c is '[' or ']' or '(' or ')' or ',' or '*' or ' ') || !NuGetVersion.TryParse(version, out var parsed) || parsed.IsPrerelease)
                return RestoreOverlayResult.Failed($"'{AuditText.Sanitize(version, 60)}' is not one exact stable version.");
            normalized[id.ToLowerInvariant() + "@" + parsed.ToNormalizedString()] = (id, parsed.ToNormalizedString());
        }

        var setName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized.Keys)))).ToLowerInvariant()[..32];
        var sessionDirectory = Path.Combine(Path.GetFullPath(Options.Root!), sessionId);
        var target = Path.Combine(sessionDirectory, setName);

        var gate = Gates.GetOrAdd(sessionDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            if (!File.Exists(Path.Combine(target, ManifestFileName)))
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // a partial leftover is never trusted
                var failure = await BuildAsync(sessionDirectory, target, setName, normalized.Values.ToList(), reviewedBy, ct).ConfigureAwait(false);
                if (failure is not null) return failure;
            }

            // Switch the pointer last and atomically: a guest that is already running keeps the directory it mounted.
            var temporary = Path.Combine(sessionDirectory, RestoreOverlayLocator.PointerFileName + ".tmp-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(temporary, setName, ct).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(sessionDirectory, RestoreOverlayLocator.PointerFileName), overwrite: true);
            return new RestoreOverlayResult(true, $"{normalized.Count} package(s) available to the session's guests.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Switching the restore overlay of session {SessionId} failed", sessionId);
            return RestoreOverlayResult.Failed("The overlay directory could not be written: " + AuditText.Sanitize(ex.GetType().Name, 60));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The reviewed package list the cache builder reads. Upgrading <c>Aspire.Hosting.AppHost</c> also needs two packages that the Aspire AppHost SDK
    /// injects into the project at that same version (the dashboard and the orchestration for the platform); they cannot be package references, so
    /// they go in as the group's <c>sdkPackages</c>. Without them the upgraded AppHost project cannot be restored offline (seen live: NU1301 for
    /// Aspire.Dashboard.Sdk.linux-x64 13.6.5).
    /// </summary>
    internal static string BuildReviewedList(string reviewedBy, DateTimeOffset now, string targetFramework, IReadOnlyList<(string Id, string Version)> packages)
    {
        var reviewedAt = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var roots = packages.Select(p => new { id = p.Id, version = p.Version }).ToArray();
        var sdk = ImplicitSdkPackages(packages);
        if (sdk.Count == 0)
            return JsonSerializer.Serialize(new { schema = 1, reviewedBy, reviewedAtUtc = reviewedAt, targetFramework, packages = roots });

        return JsonSerializer.Serialize(new
        {
            schema = 1,
            reviewedBy,
            reviewedAtUtc = reviewedAt,
            targetFramework,
            groups = new[] { new { name = "upgrade", packages = roots, sdkPackages = sdk.Select(p => new { id = p.Id, version = p.Version }).ToArray() } }
        });
    }

    // The verification guests are linux containers; the SDK picks the runtime-identifier package of the platform it runs on.
    private const string AspireRuntimeIdentifier = "linux-x64";

    internal static IReadOnlyList<(string Id, string Version)> ImplicitSdkPackages(IEnumerable<(string Id, string Version)> packages)
    {
        var result = new List<(string Id, string Version)>();
        foreach (var (id, version) in packages)
        {
            if (!string.Equals(id, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(("Aspire.Dashboard.Sdk." + AspireRuntimeIdentifier, version));
            result.Add(("Aspire.Hosting.Orchestration." + AspireRuntimeIdentifier, version));
        }

        return result;
    }

    private async Task<RestoreOverlayResult?> BuildAsync(string sessionDirectory, string target, string setName,
        IReadOnlyList<(string Id, string Version)> packages, string reviewedBy, CancellationToken ct)
    {
        var reviewedList = Path.Combine(sessionDirectory, $".reviewed-{setName}-{Guid.NewGuid():N}.json");
        try
        {
            var json = BuildReviewedList(reviewedBy, time.GetUtcNow(), Options.TargetFramework, packages);
            await File.WriteAllTextAsync(reviewedList, json, new UTF8Encoding(false), ct).ConfigureAwait(false);

            var result = await runner.RunAsync(new RestoreOverlayRunRequest(
                Options.Python, Options.PrepareScript!,
                new Dictionary<string, string>
                {
                    ["ARTIFACT_AUDIT_IMAGE"] = configuration["ArtifactVerification:Image"]!,
                    ["ARTIFACT_AUDIT_CACHE_OUT"] = target,
                    ["ARTIFACT_AUDIT_CACHE_PACKAGES"] = reviewedList
                },
                Options.Timeout), ct).ConfigureAwait(false);

            if (result.ExitCode == 0 && File.Exists(Path.Combine(target, ManifestFileName))) return null;

            logger?.LogWarning("The restore overlay build failed with exit code {ExitCode}: {Output}", result.ExitCode, result.Output);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            return RestoreOverlayResult.Failed($"The reviewed cache builder failed (exit {result.ExitCode}): {AuditText.Sanitize(result.Output, 300)}");
        }
        finally
        {
            try { File.Delete(reviewedList); } catch (IOException) { /* best effort */ }
        }
    }
}

// ───────────────────────────── resolving and preparing the upgrades of a scanned project ─────────────────────────────

public enum DependencyOverlayState
{
    /// <summary>Host-side package acquisition is not configured: targets are known but cannot be restored by the guest.</summary>
    NotConfigured,

    /// <summary>Nothing is approved by policy (everything needs the user or has no candidate), so nothing is fetched.</summary>
    NothingToFetch,

    /// <summary>The fetch is running.</summary>
    Pending,

    /// <summary>The packages are available to the session's guests.</summary>
    Ready,

    Failed
}

/// <param name="Candidates">What every vulnerable package may move to.</param>
/// <param name="Overlay">The fetch of the policy-approved packages; it keeps running after the plan is returned. Completes with null when nothing is fetched.</param>
public sealed record DependencyUpgradePlan(IReadOnlyList<DependencyUpgradeCandidate> Candidates, Task<RestoreOverlayResult?> Overlay, bool OverlayConfigured)
{
    public DependencyOverlayState OverlayState =>
        !OverlayConfigured ? DependencyOverlayState.NotConfigured
        : !Overlay.IsCompleted ? DependencyOverlayState.Pending
        : !Overlay.IsCompletedSuccessfully ? DependencyOverlayState.Failed
        : Overlay.Result is null ? DependencyOverlayState.NothingToFetch
        : Overlay.Result.Success ? DependencyOverlayState.Ready
        : DependencyOverlayState.Failed;
}

/// <param name="Approved">Packages the user approved that the host had resolved as needing approval (canonical ids).</param>
/// <param name="Ignored">Ids that were not such a candidate: unknown to the session's scan, approved by policy already, without a candidate, or malformed.</param>
/// <param name="Overlay">The fetch of everything now approved; null when nothing was approved.</param>
public sealed record DependencyUpgradeApproval(IReadOnlyList<string> Approved, IReadOnlyList<string> Ignored, RestoreOverlayResult? Overlay);

/// <summary>A direct package the host chose a version for and fetched: a finished upgrade must leave it at that version in these projects.</summary>
public sealed record UpgradeTarget(string Package, string Version, IReadOnlyList<string> Projects);

/// <summary>
/// A package version the host fetched into the session's offline overlay, direct or transitive, with where it is used. These are the facts a
/// worker needs to make the change; without them it had to rediscover them and, seeing a restore succeed for versions the base cache does not
/// hold, concluded that its own edits had not reached the command container.
/// </summary>
public sealed record FetchedUpgrade(string Package, IReadOnlyList<string> CurrentVersions, string Version, bool Transitive, IReadOnlyList<string> Projects);

public interface IDependencyUpgradeTargets
{
    /// <summary>The direct packages whose chosen version the host fetched for this session (policy-approved plus user-approved); never transitive ones.</summary>
    IReadOnlyList<UpgradeTarget> ApprovedTargets(string sessionId);

    /// <summary>Everything the host fetched for this session, transitive packages included: what a worker is told, not what a finished upgrade is checked against.</summary>
    IReadOnlyList<FetchedUpgrade> FetchedUpgrades(string sessionId) => [];
}

public interface IDependencyUpgradeApprover
{
    /// <summary>
    /// Records the user's approval of the named packages (only candidates the HOST resolved as needing approval can be approved: the user
    /// never supplies a version) and fetches them together with everything policy approves. Only a trusted structured approve may call it.
    /// </summary>
    Task<DependencyUpgradeApproval> ApproveAsync(string sessionId, string userId, IReadOnlyCollection<string> packageIds, CancellationToken ct);
}

public interface IDependencyUpgradeService
{
    /// <summary>
    /// Chooses a target for every vulnerable package of <paramref name="report"/> and starts fetching the ones policy approves into the
    /// session's overlay. Returns as soon as the targets are chosen; the fetch is reported through <see cref="DependencyUpgradePlan.Overlay"/>.
    /// </summary>
    Task<DependencyUpgradePlan> ResolveAndPrepareAsync(DependencyScanReport report, string sessionId, CancellationToken ct);
}

public sealed class DependencyUpgradeService(
    INuGetVersionCatalog catalog,
    INuGetAdvisorySnapshotProvider advisories,
    IRestoreOverlayService overlay,
    IOptions<ArtifactAuditOptions> options,
    TimeProvider time,
    ILogger<DependencyUpgradeService>? logger = null) : IDependencyUpgradeService, IDependencyUpgradeApprover, IDependencyUpgradeTargets
{
    public const string PolicyReviewer = "codexflow-host:same-major-minimal-fixed-version";
    private const string ApprovedReviewerPrefix = "codexflow-host:policy+user-approved:";

    // What this session's scan resolved and what its user approved. In memory only: after a restart the user simply approves again.
    private readonly ConcurrentDictionary<string, SessionUpgrades> _sessions = new(StringComparer.Ordinal);

    private sealed class SessionUpgrades
    {
        public IReadOnlyList<DependencyUpgradeCandidate> Candidates { get; set; } = [];
        public HashSet<string> ApprovedPackages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ApprovedBy { get; set; }
        public object Gate { get; } = new();
    }

    public async Task<DependencyUpgradePlan> ResolveAndPrepareAsync(DependencyScanReport report, string sessionId, CancellationToken ct)
    {
        var snapshot = await UsableSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            // Without trustworthy advisory data no version can be called safe: nothing is chosen, nothing is fetched.
            var none = report.Remediation.GroupBy(r => r.Package, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DependencyUpgradeCandidate(g.Key, g.Select(r => r.CurrentVersion).Distinct().Order().ToList(), null,
                    DependencyUpgradeStatus.NoCandidate, "漏洞公告数据不可用，无法选择安全的目标版本。", g.All(r => r.Transitive),
                    g.SelectMany(r => r.Projects).Distinct().Order().ToList())).ToList();
            return new DependencyUpgradePlan(none, Task.FromResult<RestoreOverlayResult?>(null), overlay.IsConfigured);
        }

        var versions = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            report.Remediation.Select(r => r.Package).Distinct(StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (package, token) => versions[package] = await catalog.GetVersionsAsync(package, token).ConfigureAwait(false)).ConfigureAwait(false);

        var candidates = DependencyUpgradePlanner.Plan(report.Remediation, snapshot.Packages, id => versions.TryGetValue(id, out var v) ? v : []);
        // Each target is the smallest fix of its own advisories; make them agree with each other before anything is fetched or approved.
        candidates = [.. await DependencyUpgradeConsistency.RaiseAsync(candidates, snapshot.Packages, id => versions.TryGetValue(id, out var v) ? v : [], catalog, ct).ConfigureAwait(false)];
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionUpgrades());
        string? approvedBy;
        List<(string Package, string Version)> approved;
        lock (state.Gate)
        {
            state.Candidates = candidates;
            approvedBy = state.ApprovedBy;
            approved = FetchSet(candidates, state.ApprovedPackages);
        }

        Task<RestoreOverlayResult?> fetch;
        if (!overlay.IsConfigured || approved.Count == 0)
        {
            fetch = Task.FromResult<RestoreOverlayResult?>(null);
        }
        else
        {
            // The fetch outlives this call (the analysis turn must not wait for a download) and is bounded by the builder's own timeout.
            fetch = Task.Run<RestoreOverlayResult?>(async () =>
            {
                try
                {
                    return await overlay.EnsureAsync(sessionId, approved, ReviewerFor(approvedBy, approved, candidates), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Fetching the approved dependency upgrades for session {SessionId} failed", sessionId);
                    return RestoreOverlayResult.Failed("The fetch failed: " + AuditText.Sanitize(ex.GetType().Name, 60));
                }
            }, CancellationToken.None);
        }

        return new DependencyUpgradePlan(candidates, fetch, overlay.IsConfigured);
    }

    public async Task<DependencyUpgradeApproval> ApproveAsync(string sessionId, string userId, IReadOnlyCollection<string> packageIds, CancellationToken ct)
    {
        var ignored = new List<string>();
        if (!_sessions.TryGetValue(sessionId, out var state))
            return new DependencyUpgradeApproval([], packageIds.ToList(), null);

        var approved = new List<string>();
        List<(string Package, string Version)> toFetch;
        IReadOnlyList<DependencyUpgradeCandidate> candidates;
        lock (state.Gate)
        {
            foreach (var id in packageIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = NuGetFlatContainerVersionCatalog.IsValidPackageId(id)
                    ? state.Candidates.FirstOrDefault(c => string.Equals(c.Package, id, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (candidate is { Status: DependencyUpgradeStatus.NeedsApproval, TargetVersion: not null }) approved.Add(candidate.Package);
                else ignored.Add(id);
            }

            if (approved.Count == 0) return new DependencyUpgradeApproval([], ignored, null);

            foreach (var package in approved) state.ApprovedPackages.Add(package);
            state.ApprovedBy = userId;
            candidates = state.Candidates;
            toFetch = FetchSet(candidates, state.ApprovedPackages);
        }

        logger?.LogInformation("User {UserId} approved the cross-version upgrade of {Packages} for session {SessionId}", userId, string.Join(", ", approved), sessionId);
        if (!overlay.IsConfigured)
            return new DependencyUpgradeApproval(approved, ignored, RestoreOverlayResult.Failed("Host-side package acquisition is not configured."));

        var result = await overlay.EnsureAsync(sessionId, toFetch, ReviewerFor(userId, toFetch, candidates), ct).ConfigureAwait(false);
        return new DependencyUpgradeApproval(approved, ignored, result);
    }

    public IReadOnlyList<UpgradeTarget> ApprovedTargets(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return [];
        }

        lock (state.Gate)
        {
            var fetched = FetchSet(state.Candidates, state.ApprovedPackages).Select(f => f.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. state.Candidates
                .Where(c => !c.Transitive && c.TargetVersion is not null && fetched.Contains(c.Package))
                .Select(c => new UpgradeTarget(c.Package, c.TargetVersion!, c.Projects))];
        }
    }

    public IReadOnlyList<FetchedUpgrade> FetchedUpgrades(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return [];
        }

        lock (state.Gate)
        {
            var fetched = FetchSet(state.Candidates, state.ApprovedPackages).Select(f => f.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. state.Candidates
                .Where(c => c.TargetVersion is not null && fetched.Contains(c.Package))
                .Select(c => new FetchedUpgrade(c.Package, c.CurrentVersions, c.TargetVersion!, c.Transitive, c.Projects))];
        }
    }

    /// <summary>Everything the guests may restore: what policy approves plus the packages the user approved (only ever the host-resolved target).</summary>
    private static List<(string Package, string Version)> FetchSet(IReadOnlyList<DependencyUpgradeCandidate> candidates, HashSet<string> approvedPackages) =>
        candidates.Where(c => c.TargetVersion is not null &&
                              (c.Status == DependencyUpgradeStatus.AutoApproved ||
                               (c.Status == DependencyUpgradeStatus.NeedsApproval && approvedPackages.Contains(c.Package))))
            .Select(c => (c.Package, c.TargetVersion!)).ToList();

    /// <summary>The reviewer recorded in the reviewed list: policy alone, or policy plus the user whose approval added bigger jumps.</summary>
    private static string ReviewerFor(string? approvedBy, IReadOnlyList<(string Package, string Version)> fetch, IReadOnlyList<DependencyUpgradeCandidate> candidates)
    {
        var hasUserApproved = !string.IsNullOrWhiteSpace(approvedBy) &&
                              fetch.Any(f => candidates.Any(c => c.Status == DependencyUpgradeStatus.NeedsApproval && string.Equals(c.Package, f.Package, StringComparison.OrdinalIgnoreCase)));
        return hasUserApproved ? ApprovedReviewerPrefix + AuditText.Sanitize(approvedBy, 40) : PolicyReviewer;
    }

    /// <summary>Advisory data that is as fresh and complete as the dependency audit itself requires; otherwise null (never "no advisories").</summary>
    private async Task<NuGetAdvisorySnapshot?> UsableSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var result = await advisories.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (result.Status != AdvisoryStatus.Ok || result.Snapshot is not { } snapshot) return null;

            var now = time.GetUtcNow();
            var limits = options.Value.Advisory;
            if (snapshot.Packages.Count < limits.MinimumPackageEntries || string.IsNullOrWhiteSpace(snapshot.Sha256)) return null;
            if (snapshot.RetrievedAtUtc > now + TimeSpan.FromMinutes(5) || now - snapshot.RetrievedAtUtc > limits.MaxAge) return null;
            if (now - snapshot.SourceUpdatedUtc > limits.MaxSourceAge) return null;
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            logger?.LogWarning(ex, "Advisory data was not usable for choosing upgrade targets");
            return null;
        }
    }
}
