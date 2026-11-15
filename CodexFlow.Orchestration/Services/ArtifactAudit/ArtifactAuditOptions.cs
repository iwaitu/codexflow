// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
namespace CodexFlow.Services.ArtifactAudit;

/// <summary>
/// Trusted host configuration ("ArtifactAudit" section). Nothing here is ever read from model or repository content.
/// Required checks cannot be switched off from configuration: a missing layer yields NotRun/Error, never Pass.
/// </summary>
public sealed class ArtifactAuditOptions
{
    public const string SectionName = "ArtifactAudit";

    // ── secrets (gitleaks inside the verifier image) ─────────────────────────────
    public string GitleaksConfigPath { get; set; } = "/etc/codexflow/gitleaks.toml";
    public string GitleaksIgnorePath { get; set; } = "/etc/codexflow/empty-ignore";
    /// <summary>When set, the guest config file must hash to exactly this SHA-256 (hex) or the layer is Error.</summary>
    public string? ExpectedGitleaksConfigSha256 { get; set; }
    /// <summary>When set, <c>gitleaks version</c> must equal this value or the layer is Error.</summary>
    public string? ExpectedGitleaksVersion { get; set; }
    public int GitleaksMaxDecodeDepth { get; set; } = 3;
    public TimeSpan SecretScanTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxReportedFindings { get; set; } = 100;

    // ── dependencies ────────────────────────────────────────────────────────────
    public TimeSpan RestoreTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ListPackageTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public int MaxProjects { get; set; } = 100;

    public NuGetAdvisoryOptions Advisory { get; set; } = new();

    /// <summary>
    /// Host directory with OSV advisory data for the lockfile-based ecosystems: <c>{dir}/npm</c> and <c>{dir}/PyPI</c>, each with a
    /// <c>snapshot.json</c> and the OSV records. Without it a repository that has npm or Python manifests gets an Error, never a Pass.
    /// </summary>
    public string? OsvAdvisoryDirectory { get; set; }

    /// <summary>Optional reviewed baseline of pre-existing advisories. Unset = every High/Critical advisory blocks (the default).</summary>
    public DependencyBaselineOptions DependencyBaseline { get; set; } = new();

    // ── model layer ──────────────────────────────────────────────────────────────
    public ModelAuditOptions Model { get; set; } = new();
}

/// <summary>
/// Host-managed baseline of known, accepted dependency advisories (trusted configuration; never read from the repository or the model).
/// A configured baseline that is missing, malformed, expired or does not match <see cref="ExpectedSha256"/> makes the dependency layer
/// an Error: it is never silently ignored and never widened.
/// </summary>
public sealed class DependencyBaselineOptions
{
    /// <summary>Host path of the baseline JSON (see <c>DependencyBaselineLoader</c> for the schema). Null/empty disables the feature.</summary>
    public string? Path { get; set; }
    /// <summary>When set, the file must hash to exactly this SHA-256 (hex), so a reviewed baseline cannot be swapped unnoticed.</summary>
    public string? ExpectedSha256 { get; set; }
}

public sealed class NuGetAdvisoryOptions
{
    /// <summary>Fixed endpoint. Only https and hosts from <see cref="AllowedHosts"/> are ever contacted (also for redirects/resources).</summary>
    public string ServiceIndexUrl { get; set; } = "https://api.nuget.org/v3/index.json";
    public List<string> AllowedHosts { get; set; } = ["api.nuget.org"];
    /// <summary>Host directory that stores the validated snapshot between runs. Required for offline-by-policy operation.</summary>
    public string? CacheDirectory { get; set; }
    /// <summary>A cached snapshot older than this is refreshed first; if refresh fails an older one may still be used up to <see cref="MaxAge"/>.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(6);
    /// <summary>Hard freshness limit: older data means the dependency layer is Error (never Pass).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(48);
    /// <summary>The source data itself (its latest update stamp) must not be older than this.</summary>
    public TimeSpan MaxSourceAge { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public long MaxPageBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>Sanity floor: a snapshot with fewer vulnerable-package entries is considered truncated/invalid.</summary>
    public int MinimumPackageEntries { get; set; } = 100;
}

public sealed class ModelAuditOptions
{
    public int MaxFiles { get; set; } = 40;
    public int MaxBytesPerFile { get; set; } = 32 * 1024;
    public int MaxTotalBytes { get; set; } = 160 * 1024;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);
    public float Temperature { get; set; }

    /// <summary>
    /// When the host can locate what the change touched, the model is asked to answer for those lines only and lower-severity findings in
    /// code the change did not touch are reported as baseline debt instead of blocking. High and critical findings always block.
    /// </summary>
    public bool ScopeToChangedLines { get; set; } = true;

    /// <summary>A finding within this many lines of a changed line still counts as caused by the change.</summary>
    public int ChangedLineMargin { get; set; } = 3;
}
