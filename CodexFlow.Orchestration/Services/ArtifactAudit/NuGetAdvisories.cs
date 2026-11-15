// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace CodexFlow.Services.ArtifactAudit;

public sealed record NuGetAdvisory(string Url, int Severity, string VersionRange)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string SeverityName => Severity switch { 0 => "Low", 1 => "Moderate", 2 => "High", 3 => "Critical", _ => "Unknown" };

    /// <summary>High and Critical block; Low/Moderate are existing-risk NeedsReview.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBlocking => Severity >= 2;

    /// <summary>Last URL segment (e.g. GHSA-xxxx), used as the advisory id.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id
    {
        get
        {
            var trimmed = Url.TrimEnd('/');
            var i = trimmed.LastIndexOf('/');
            return i >= 0 && i < trimmed.Length - 1 ? trimmed[(i + 1)..] : trimmed;
        }
    }
}

/// <summary>Immutable, validated copy of the official NuGet vulnerability data (VulnerabilityInfo resource).</summary>
public sealed record NuGetAdvisorySnapshot(
    DateTimeOffset RetrievedAtUtc,
    DateTimeOffset SourceUpdatedUtc,
    string Endpoint,
    string Sha256,
    IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> Packages)
{
    public string Version => $"nuget-vulnerability-data@{SourceUpdatedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}/sha256:{Sha256[..Math.Min(16, Sha256.Length)]}/retrieved:{RetrievedAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
}

public enum AdvisoryStatus { Ok, Unavailable, Stale, Invalid }

public sealed record AdvisorySnapshotResult(AdvisoryStatus Status, NuGetAdvisorySnapshot? Snapshot, string Message)
{
    public static AdvisorySnapshotResult Ok(NuGetAdvisorySnapshot snapshot, string message = "ok") => new(AdvisoryStatus.Ok, snapshot, message);
}

public interface INuGetAdvisorySnapshotProvider
{
    Task<AdvisorySnapshotResult> GetSnapshotAsync(CancellationToken ct);
}

public static class NuGetAdvisoryMatcher
{
    /// <summary>True/false when decidable; null when the version or the advisory range cannot be parsed (caller must fail closed).</summary>
    public static bool? Affects(NuGetAdvisory advisory, string resolvedVersion)
    {
        if (!NuGetVersion.TryParse(resolvedVersion, out var version) || !VersionRange.TryParse(advisory.VersionRange, out var range)) return null;
        return range.Satisfies(version);
    }
}

/// <summary>Strict parsers for the two documents of the NuGet VulnerabilityInfo resource.</summary>
public static class NuGetAdvisoryParser
{
    public sealed record PageRef(string Name, string Url, DateTimeOffset Updated);

    public static bool TryParseIndex(ReadOnlySpan<byte> json, out List<PageRef> pages, out string error)
    {
        pages = [];
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { error = "index is not an array"; return false; }
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object ||
                    !e.TryGetProperty("@name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !e.TryGetProperty("@id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !e.TryGetProperty("@updated", out var upd) || upd.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(upd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
                {
                    error = "index entry is malformed";
                    return false;
                }
                pages.Add(new PageRef(name.GetString()!, id.GetString()!, updated));
            }
            if (pages.Count(p => p.Name == "base") != 1 || pages.Any(p => p.Name is not ("base" or "update"))) { error = "index must contain exactly one base page and only base/update pages"; return false; }
            return true;
        }
        catch (JsonException ex)
        {
            error = "index is not valid JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>Page = { "lowercase.package.id": [ { url, severity 0..3, versions "(, 1.2.3)" } ] }. Any unparsable range fails closed.</summary>
    public static bool TryParsePage(ReadOnlySpan<byte> json, IDictionary<string, List<NuGetAdvisory>> into, out string error)
    {
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "page is not an object"; return false; }
            foreach (var pkg in doc.RootElement.EnumerateObject())
            {
                if (pkg.Value.ValueKind != JsonValueKind.Array || pkg.Name.Length == 0) { error = "package entry is malformed"; return false; }
                var key = pkg.Name.ToLowerInvariant();
                foreach (var a in pkg.Value.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object ||
                        !a.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString()) ||
                        !a.TryGetProperty("severity", out var sev) || sev.ValueKind != JsonValueKind.Number || !sev.TryGetInt32(out var severity) || severity is < 0 or > 3 ||
                        !a.TryGetProperty("versions", out var ver) || ver.ValueKind != JsonValueKind.String || !VersionRange.TryParse(ver.GetString()!, out _))
                    {
                        error = $"advisory entry for '{pkg.Name}' is malformed";
                        return false;
                    }
                    var item = new NuGetAdvisory(url.GetString()!, severity, ver.GetString()!);
                    if (!into.TryGetValue(key, out var list)) into[key] = list = [];
                    if (!list.Contains(item)) list.Add(item);
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "page is not valid JSON: " + ex.Message;
            return false;
        }
    }

    public static string CanonicalHash(IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> packages)
    {
        var sb = new StringBuilder();
        foreach (var key in packages.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sb.Append(key).Append('\n');
            foreach (var a in packages[key].OrderBy(x => x.Url, StringComparer.Ordinal).ThenBy(x => x.VersionRange, StringComparer.Ordinal))
                sb.Append(a.Url).Append('|').Append(a.Severity.ToString(CultureInfo.InvariantCulture)).Append('|').Append(a.VersionRange).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// Host-managed advisory snapshot: contacts only the configured fixed https endpoint(s) (no redirects to other hosts, no
/// credentials, no caller-supplied URLs), validates and hashes the data, and persists it atomically so later audits stay
/// valid while the guest remains offline. Freshness is enforced by age; failure never degrades to "no data = pass".
/// </summary>
public sealed class HostManagedNuGetAdvisoryProvider : INuGetAdvisorySnapshotProvider, IDisposable
{
    private const string CacheFileName = "nuget-advisories.v1.json";
    private readonly HttpClient _http;
    private readonly ArtifactAuditOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HostManagedNuGetAdvisoryProvider>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NuGetAdvisorySnapshot? _memory;

    public HostManagedNuGetAdvisoryProvider(HttpClient http, IOptions<ArtifactAuditOptions> options, TimeProvider? time = null, ILogger<HostManagedNuGetAdvisoryProvider>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<AdvisorySnapshotResult> GetSnapshotAsync(CancellationToken ct)
    {
        var adv = _options.Advisory;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var cached = _memory ?? LoadCache();
            if (cached is not null && now - cached.RetrievedAtUtc < adv.RefreshInterval && now >= cached.RetrievedAtUtc - TimeSpan.FromMinutes(5))
            {
                _memory = cached;
                return AdvisorySnapshotResult.Ok(cached, "cache");
            }

            string failure;
            try
            {
                var (snapshot, error) = await FetchAsync(now, ct).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    _memory = snapshot;
                    StoreCache(snapshot);
                    return AdvisorySnapshotResult.Ok(snapshot, "refreshed");
                }
                failure = error;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = "advisory endpoint timed out";
            }
            catch (HttpRequestException ex)
            {
                failure = "advisory endpoint unreachable: " + ex.Message;
            }

            _logger?.LogWarning("NuGet advisory refresh failed: {Reason}", failure);
            if (cached is not null && now - cached.RetrievedAtUtc <= adv.MaxAge)
            {
                _memory = cached;
                return AdvisorySnapshotResult.Ok(cached, "cache (refresh failed: " + failure + ")");
            }

            return cached is null
                ? new AdvisorySnapshotResult(AdvisoryStatus.Unavailable, null, failure)
                : new AdvisorySnapshotResult(AdvisoryStatus.Stale, null, $"cached data is older than {adv.MaxAge} and refresh failed: {failure}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool HostAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        _options.Advisory.AllowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));

    private async Task<(NuGetAdvisorySnapshot? Snapshot, string Error)> FetchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var adv = _options.Advisory;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(adv.HttpTimeout);

        if (!Uri.TryCreate(adv.ServiceIndexUrl, UriKind.Absolute, out var indexUri) || !HostAllowed(indexUri))
            return (null, "configured service index URL is not an allowed https endpoint");
        var serviceIndex = await GetBytesAsync(indexUri, timeout.Token).ConfigureAwait(false);
        if (serviceIndex is null) return (null, "service index could not be downloaded");

        string? vulnIndexUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(serviceIndex, new JsonDocumentOptions { MaxDepth = 16 });
            foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
            {
                var type = r.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type is not null && type.StartsWith("VulnerabilityInfo/", StringComparison.Ordinal) && r.TryGetProperty("@id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    vulnIndexUrl = id.GetString();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "service index is malformed");
        }
        if (vulnIndexUrl is null || !Uri.TryCreate(vulnIndexUrl, UriKind.Absolute, out var vulnUri) || !HostAllowed(vulnUri))
            return (null, "service index has no allowed VulnerabilityInfo resource");

        var vulnIndex = await GetBytesAsync(vulnUri, timeout.Token).ConfigureAwait(false);
        if (vulnIndex is null) return (null, "vulnerability index could not be downloaded");
        if (!NuGetAdvisoryParser.TryParseIndex(vulnIndex, out var pages, out var indexError)) return (null, indexError);

        var merged = new Dictionary<string, List<NuGetAdvisory>>(StringComparer.Ordinal);
        foreach (var page in pages.OrderBy(p => p.Name == "base" ? 0 : 1).ThenBy(p => p.Updated))
        {
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var pageUri) || !HostAllowed(pageUri)) return (null, "vulnerability page URL is not an allowed https endpoint");
            var bytes = await GetBytesAsync(pageUri, timeout.Token).ConfigureAwait(false);
            if (bytes is null) return (null, $"vulnerability {page.Name} page could not be downloaded");
            if (!NuGetAdvisoryParser.TryParsePage(bytes, merged, out var pageError)) return (null, pageError);
        }

        if (merged.Count < adv.MinimumPackageEntries) return (null, $"advisory data has only {merged.Count} package entries (expected at least {adv.MinimumPackageEntries}); treated as truncated");
        var sourceUpdated = pages.Max(p => p.Updated);
        if (sourceUpdated > now + TimeSpan.FromHours(1)) return (null, "advisory data claims a future update time");

        IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> frozen = merged.ToDictionary(k => k.Key, k => (IReadOnlyList<NuGetAdvisory>)k.Value.ToArray(), StringComparer.Ordinal);
        return (new NuGetAdvisorySnapshot(now, sourceUpdated, adv.ServiceIndexUrl, NuGetAdvisoryParser.CanonicalHash(frozen), frozen), string.Empty);
    }

    /// <summary>Bounded GET with no redirects to unapproved hosts; returns null on any non-200/oversize response.</summary>
    private async Task<byte[]?> GetBytesAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var final = response.RequestMessage?.RequestUri ?? uri;
        if (response.StatusCode != System.Net.HttpStatusCode.OK || !HostAllowed(final)) return null;
        var limit = _options.Advisory.MaxPageBytes;
        if (response.Content.Headers.ContentLength is { } len && len > limit) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    // ── disk cache (host-controlled directory; integrity re-verified on load) ────────────────────────
    private sealed class CacheDocument
    {
        public int Schema { get; set; } = 1;
        public DateTimeOffset RetrievedAtUtc { get; set; }
        public DateTimeOffset SourceUpdatedUtc { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public Dictionary<string, List<NuGetAdvisory>> Packages { get; set; } = [];
    }

    private string? CachePath => _options.Advisory.CacheDirectory is { Length: > 0 } dir ? Path.Combine(dir, CacheFileName) : null;

    private NuGetAdvisorySnapshot? LoadCache()
    {
        try
        {
            if (CachePath is not { } path || !File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllBytes(path));
            if (doc is null || doc.Schema != 1) return null;
            IReadOnlyDictionary<string, IReadOnlyList<NuGetAdvisory>> packages =
                doc.Packages.ToDictionary(k => k.Key, k => (IReadOnlyList<NuGetAdvisory>)k.Value.ToArray(), StringComparer.Ordinal);
            // Re-validate everything: a tampered or hand-edited cache must not become trusted data.
            if (!string.Equals(NuGetAdvisoryParser.CanonicalHash(packages), doc.Sha256, StringComparison.Ordinal)) return null;
            if (packages.Count < _options.Advisory.MinimumPackageEntries) return null;
            if (packages.Values.SelectMany(x => x).Any(a => a.Severity is < 0 or > 3 || !VersionRange.TryParse(a.VersionRange, out _))) return null;
            return new NuGetAdvisorySnapshot(doc.RetrievedAtUtc, doc.SourceUpdatedUtc, doc.Endpoint, doc.Sha256, packages);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable NuGet advisory cache");
            return null;
        }
    }

    private void StoreCache(NuGetAdvisorySnapshot snapshot)
    {
        try
        {
            if (CachePath is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var doc = new CacheDocument
            {
                RetrievedAtUtc = snapshot.RetrievedAtUtc, SourceUpdatedUtc = snapshot.SourceUpdatedUtc, Endpoint = snapshot.Endpoint, Sha256 = snapshot.Sha256,
                Packages = snapshot.Packages.ToDictionary(k => k.Key, k => k.Value.ToList(), StringComparer.Ordinal)
            };
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(doc));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not persist NuGet advisory cache (continuing with in-memory snapshot)");
        }
    }
}
