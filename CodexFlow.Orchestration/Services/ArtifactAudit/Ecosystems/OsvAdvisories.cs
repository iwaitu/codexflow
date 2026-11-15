// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Services.ArtifactAudit.Ecosystems;

/// <summary>One boundary of an affected range: <c>introduced</c>, <c>fixed</c>, <c>last_affected</c> or <c>limit</c>.</summary>
public sealed record OsvEvent(string Kind, string Version);

public sealed record OsvRange(string Type, IReadOnlyList<OsvEvent> Events);

public sealed record OsvAffected(string Package, IReadOnlyList<OsvRange> Ranges, IReadOnlyList<string> Versions);

/// <summary>An advisory in the OSV schema (https://ossf.github.io/osv-schema/), reduced to what the dependency gate needs.</summary>
public sealed record OsvAdvisory(string Id, int Severity, IReadOnlyList<OsvAffected> Affected)
{
    public string SeverityName => Severity switch { 0 => "Low", 1 => "Moderate", 2 => "High", 3 => "Critical", _ => "Unknown" };

    /// <summary>High, Critical and unknown severity block; Low and Moderate are existing risk for a human to decide on.</summary>
    public bool IsBlocking => Severity >= 2;
}

/// <summary>All advisories of one ecosystem, keyed by the package name as that ecosystem normalizes it.</summary>
public sealed record OsvSnapshot(
    string Ecosystem,
    string Version,
    DateTimeOffset RetrievedAtUtc,
    DateTimeOffset SourceUpdatedUtc,
    IReadOnlyDictionary<string, IReadOnlyList<OsvAdvisory>> Packages);

public sealed record OsvSnapshotResult(AdvisoryStatus Status, OsvSnapshot? Snapshot, string Message)
{
    public static OsvSnapshotResult Unavailable(string message) => new(AdvisoryStatus.Unavailable, null, message);
    public static OsvSnapshotResult Invalid(string message) => new(AdvisoryStatus.Invalid, null, message);
}

public interface IOsvSnapshotProvider
{
    /// <summary>Returns the host-managed advisory data of an ecosystem ("npm", "PyPI").</summary>
    Task<OsvSnapshotResult> GetSnapshotAsync(string ecosystem, CancellationToken ct);
}

/// <summary>Reads OSV records.</summary>
public static class OsvParser
{
    /// <summary>
    /// Parses one OSV record for an ecosystem. A withdrawn record, or one that names no package of this ecosystem, yields
    /// <see langword="null"/>; a record that is not valid JSON or has no id throws <see cref="JsonException"/>.
    /// </summary>
    public static OsvAdvisory? Parse(string json, string ecosystem, Func<string, string> normalizeName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("An OSV record must be an object.");
        if (root.TryGetProperty("withdrawn", out var withdrawn) && withdrawn.ValueKind == JsonValueKind.String) return null;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("An OSV record needs an id.");

        var affected = new List<OsvAffected>();
        if (root.TryGetProperty("affected", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object) continue;
                var eco = package.TryGetProperty("ecosystem", out var e) ? e.GetString() : null;
                // OSV writes a version suffix for some ecosystems ("npm" is plain; Debian-style "Debian:12" is not ours).
                if (!string.Equals(eco, ecosystem, StringComparison.OrdinalIgnoreCase)) continue;
                var name = package.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var ranges = new List<OsvRange>();
                if (entry.TryGetProperty("ranges", out var rangeArray) && rangeArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var range in rangeArray.EnumerateArray())
                    {
                        var type = range.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var events = new List<OsvEvent>();
                        if (range.TryGetProperty("events", out var eventArray) && eventArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ev in eventArray.EnumerateArray())
                            {
                                foreach (var kind in new[] { "introduced", "fixed", "last_affected", "limit" })
                                {
                                    if (ev.TryGetProperty(kind, out var value) && value.ValueKind == JsonValueKind.String)
                                        events.Add(new OsvEvent(kind, value.GetString()!));
                                }
                            }
                        }

                        ranges.Add(new OsvRange(type, events));
                    }
                }

                var versions = entry.TryGetProperty("versions", out var versionArray) && versionArray.ValueKind == JsonValueKind.Array
                    ? versionArray.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                    : [];
                affected.Add(new OsvAffected(normalizeName(name!), ranges, versions));
            }
        }

        return affected.Count == 0 ? null : new OsvAdvisory(id.GetString()!, ReadSeverity(root), affected);
    }

    // GitHub-reviewed records carry a word in database_specific.severity. A record without one is "unknown", which blocks:
    // not knowing how bad a vulnerability is must never make it easier to accept.
    private static int ReadSeverity(JsonElement root)
    {
        if (root.TryGetProperty("database_specific", out var db) && db.ValueKind == JsonValueKind.Object &&
            db.TryGetProperty("severity", out var severity) && severity.ValueKind == JsonValueKind.String)
        {
            return severity.GetString()!.Trim().ToUpperInvariant() switch
            {
                "LOW" => 0,
                "MODERATE" or "MEDIUM" => 1,
                "HIGH" => 2,
                "CRITICAL" => 3,
                _ => 4
            };
        }

        return 4;
    }
}

/// <summary>Decides whether an advisory affects a version of a package.</summary>
public static class OsvMatcher
{
    /// <summary>
    /// <see langword="true"/> / <see langword="false"/> when the advisory's ranges and version list decide it;
    /// <see langword="null"/> when they cannot be evaluated (an unparsable boundary, or only git ranges), which the scanner
    /// treats as an error rather than as "not affected".
    /// </summary>
    public static bool? Affects(OsvAffected affected, string version, IVersionComparer comparer)
    {
        if (affected.Versions.Any(v => string.Equals(v, version, StringComparison.Ordinal))) return true;

        var evaluated = false;
        foreach (var range in affected.Ranges)
        {
            if (!string.Equals(range.Type, "SEMVER", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(range.Type, "ECOSYSTEM", StringComparison.OrdinalIgnoreCase)) continue;
            var inside = InRange(range, version, comparer);
            if (inside is null) return null;
            evaluated = true;
            if (inside == true) return true;
        }

        // Only git ranges (commit hashes) and no explicit versions: nothing here can say whether this version is affected.
        if (!evaluated && affected.Versions.Count == 0 && affected.Ranges.Count > 0) return null;
        return false;
    }

    private static bool? InRange(OsvRange range, string version, IVersionComparer comparer)
    {
        // Events are processed in version order; "introduced: 0" is below every version.
        var ordered = new List<OsvEvent>(range.Events);
        var failed = false;
        ordered.Sort((a, b) =>
        {
            if (a.Version == "0" && b.Version == "0") return 0;
            if (a.Version == "0") return -1;
            if (b.Version == "0") return 1;
            var c = comparer.Compare(a.Version, b.Version);
            if (c is null) { failed = true; return 0; }
            return c.Value;
        });
        if (failed) return null;

        string? introduced = null;
        foreach (var ev in ordered)
        {
            if (ev.Kind == "introduced")
            {
                introduced ??= ev.Version;
                continue;
            }

            if (introduced is null) continue; // an end without a start covers nothing
            var afterStart = introduced == "0" ? 1 : comparer.Compare(version, introduced);
            var againstEnd = comparer.Compare(version, ev.Version);
            if (afterStart is null || againstEnd is null) return null;
            var inside = afterStart >= 0 && (ev.Kind == "last_affected" ? againstEnd <= 0 : againstEnd < 0);
            if (inside) return true;
            introduced = null;
        }

        // An introduced boundary that nothing closes stays open: every later version is affected.
        if (introduced is not null)
        {
            var afterStart = introduced == "0" ? 1 : comparer.Compare(version, introduced);
            if (afterStart is null) return null;
            return afterStart >= 0;
        }

        return false;
    }
}

/// <summary>
/// Host-managed advisory data on disk: <c>{directory}/{ecosystem}/snapshot.json</c> (<c>retrievedAtUtc</c>, <c>sourceUpdatedUtc</c>) next to
/// either <c>advisories.ndjson</c> (one reduced OSV record per line, built by <c>scripts/e2e/prepare-osv-advisories.py</c>; <c>snapshot.json</c>
/// then also names the number of <c>records</c> and the <c>indexSha256</c>, both of which must match) or, as a fallback, the OSV records
/// themselves as <c>*.json</c> files. Loaded results are cached until the metadata file changes.
/// </summary>
public sealed class DirectoryOsvSnapshotProvider(string? directory) : IOsvSnapshotProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, OsvSnapshotResult Result)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<OsvSnapshotResult> GetSnapshotAsync(string ecosystem, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Task.FromResult(OsvSnapshotResult.Unavailable("No OSV advisory directory is configured."));
        var folder = Path.Combine(directory, ecosystem);
        var metadataPath = Path.Combine(folder, "snapshot.json");
        if (!File.Exists(metadataPath)) return Task.FromResult(OsvSnapshotResult.Unavailable($"No OSV snapshot for {ecosystem} (missing snapshot.json)."));

        var stamp = File.GetLastWriteTimeUtc(metadataPath);
        lock (_gate)
        {
            if (_cache.TryGetValue(ecosystem, out var cached) && cached.Stamp == stamp) return Task.FromResult(cached.Result);
        }

        var result = Load(ecosystem, folder, metadataPath, ct);
        lock (_gate)
        {
            _cache[ecosystem] = (stamp, result);
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Loads <c>advisories.ndjson</c>. The file is hashed while it is read and must match the count and SHA-256 in snapshot.json: a file
    /// that lost lines (an antivirus may remove some) or was edited is Invalid, never used with holes in it.
    /// </summary>
    private static OsvSnapshotResult LoadIndex(string ecosystem, string indexPath, DateTimeOffset retrieved, DateTimeOffset source,
        long? expectedRecords, string? expectedSha, Func<string, string> normalize, CancellationToken ct)
    {
        if (expectedRecords is null || string.IsNullOrWhiteSpace(expectedSha))
            return OsvSnapshotResult.Invalid($"snapshot.json of {ecosystem} must name the records and indexSha256 of advisories.ndjson.");

        var packages = new Dictionary<string, List<OsvAdvisory>>(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long lines = 0;
        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 1 << 20);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                lines++;
                var advisory = OsvParser.Parse(line, ecosystem, normalize);
                if (advisory is null) continue;
                foreach (var name in advisory.Affected.Select(a => a.Package).Distinct(StringComparer.Ordinal))
                {
                    if (!packages.TryGetValue(name, out var list)) packages[name] = list = [];
                    list.Add(advisory);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return OsvSnapshotResult.Invalid($"advisories.ndjson of {ecosystem} is unreadable: {ex.GetType().Name}.");
        }

        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (lines != expectedRecords.Value)
            return OsvSnapshotResult.Invalid($"advisories.ndjson of {ecosystem} has {lines} records, snapshot.json says {expectedRecords.Value}.");
        if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
            return OsvSnapshotResult.Invalid($"advisories.ndjson of {ecosystem} does not match the SHA-256 in snapshot.json.");

        return new OsvSnapshotResult(AdvisoryStatus.Ok,
            new OsvSnapshot(ecosystem, $"osv-{ecosystem.ToLowerInvariant()}:sha256:{sha[..16]}", retrieved, source,
                packages.ToDictionary(p => p.Key, p => (IReadOnlyList<OsvAdvisory>)p.Value, StringComparer.Ordinal)),
            "ok");
    }

    private static OsvSnapshotResult Load(string ecosystem, string folder, string metadataPath, CancellationToken ct)
    {
        DateTimeOffset retrieved;
        DateTimeOffset source;
        long? expectedRecords = null;
        string? expectedSha = null;
        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            retrieved = metadata.RootElement.GetProperty("retrievedAtUtc").GetDateTimeOffset();
            source = metadata.RootElement.GetProperty("sourceUpdatedUtc").GetDateTimeOffset();
            if (metadata.RootElement.TryGetProperty("records", out var records) && records.TryGetInt64(out var count)) expectedRecords = count;
            if (metadata.RootElement.TryGetProperty("indexSha256", out var shaElement) && shaElement.ValueKind == JsonValueKind.String) expectedSha = shaElement.GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return OsvSnapshotResult.Invalid($"snapshot.json of {ecosystem} is unreadable: {ex.GetType().Name}.");
        }

        Func<string, string> normalize = ecosystem.Equals("PyPI", StringComparison.OrdinalIgnoreCase)
            ? Pep440Comparer.NormalizeName
            : name => name;
        var packages = new Dictionary<string, List<OsvAdvisory>>(StringComparer.Ordinal);

        var indexPath = Path.Combine(folder, "advisories.ndjson");
        if (File.Exists(indexPath))
            return LoadIndex(ecosystem, indexPath, retrieved, source, expectedRecords, expectedSha, normalize, ct);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(file), "snapshot.json", StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(file);
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n"));
                hash.AppendData(Encoding.UTF8.GetBytes(text));
                var advisory = OsvParser.Parse(text, ecosystem, normalize);
                if (advisory is null) continue;
                foreach (var name in advisory.Affected.Select(a => a.Package).Distinct(StringComparer.Ordinal))
                {
                    if (!packages.TryGetValue(name, out var list)) packages[name] = list = [];
                    list.Add(advisory);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return OsvSnapshotResult.Invalid($"An OSV record of {ecosystem} is unreadable: {ex.GetType().Name}.");
        }

        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new OsvSnapshotResult(AdvisoryStatus.Ok,
            new OsvSnapshot(ecosystem, $"osv-{ecosystem.ToLowerInvariant()}:sha256:{sha[..16]}", retrieved, source,
                packages.ToDictionary(p => p.Key, p => (IReadOnlyList<OsvAdvisory>)p.Value, StringComparer.Ordinal)),
            "ok");
    }
}
