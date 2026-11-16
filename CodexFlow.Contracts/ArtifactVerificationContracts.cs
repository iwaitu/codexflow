// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts;

/// <summary>Produced by the trusted worker; never accepted as authorization from model input.</summary>
public sealed record ForgeArtifactManifest
{
    public int Version { get; init; } = 1;
    public string ArtifactId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public int Attempt { get; init; }
    public string RepositoryIdentity { get; init; } = string.Empty;
    public string WorktreePath { get; init; } = string.Empty;
    public string BaseCommit { get; init; } = string.Empty;
    public string HeadCommit { get; init; } = string.Empty;
    public string ScopeHash { get; init; } = string.Empty;
    public string PlanArtifactId { get; init; } = string.Empty;
    public string PlanVersion { get; init; } = string.Empty;
    public string PlanHash { get; init; } = string.Empty;
    public IReadOnlyList<string> RequiredTaskIds { get; init; } = [];
    public IReadOnlyList<string> ParentArtifactIds { get; init; } = [];
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public enum ArtifactAuditState { NotRun, Pass, Fail, NeedsReview, Error }

public sealed record ArtifactAuditFinding(string Fingerprint, string Severity, string Location, string Evidence)
{
    // Backward-compatible additions (all optional): which layer produced the finding and exact package/advisory identity.
    public string Layer { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Package { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public string AdvisoryId { get; init; } = string.Empty;
}

/// <summary>Per-layer record (secrets / dependencies / model). Always populated, also for failed or skipped layers.</summary>
public sealed record ArtifactAuditLayerResult
{
    public string Name { get; init; } = string.Empty;
    public ArtifactAuditState State { get; init; }
    public string Tool { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string DataVersion { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; init; }
}

/// <summary>
/// How to fix one vulnerable package version: derived from the advisory data only (no network, nothing from the repository).
/// All advisories that affect the package version are combined, so the plan satisfies every one of them.
/// </summary>
public sealed record ArtifactDependencyRemediation
{
    /// <summary>The package ecosystem: <c>NuGet</c> (the default), <c>npm</c> or <c>PyPI</c>.</summary>
    public string Ecosystem { get; init; } = "NuGet";
    public string Package { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    /// <summary>True when the package is only a transitive dependency everywhere it appears (fix = add a direct reference or pin it).</summary>
    public bool Transitive { get; init; }
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyList<string> AdvisoryIds { get; init; } = [];
    public string HighestSeverity { get; init; } = string.Empty;
    /// <summary>Upgrade to at least this version (an advisory range ends at it exclusively). Null when none applies.</summary>
    public string? MinimumFixedVersion { get; init; }
    /// <summary>Upgrade to a version strictly greater than this (an advisory range ends at it inclusively); the exact version needs a package-feed lookup.</summary>
    public string? MustExceedVersion { get; init; }
    /// <summary>UpgradeToAtLeast, UpgradeBeyond or NoKnownFix (an advisory has no upper bound: replace the package or remove the dependency).</summary>
    public string Action { get; init; } = string.Empty;
    /// <summary>True when a reviewed baseline accepted every advisory of this package version; the debt is real but does not block.</summary>
    public bool BaselineAccepted { get; init; }
}

public sealed record ArtifactAuditResult
{
    /// <summary>
    /// Findings a reviewed baseline accepted (host-side, expiring, exact package+version+advisory). They are NOT in <see cref="Findings"/>,
    /// which must stay empty for a Pass, but they are always reported. Backward-compatible addition.
    /// </summary>
    public IReadOnlyList<ArtifactAuditFinding> BaselineDebt { get; init; } = [];
    /// <summary>Fix plan for every vulnerable package version, blocking or baseline-accepted. Backward-compatible addition.</summary>
    public IReadOnlyList<ArtifactDependencyRemediation> Remediation { get; init; } = [];
    /// <summary>Per-layer details (backward-compatible addition). Empty for results produced by older code.</summary>
    public IReadOnlyList<ArtifactAuditLayerResult> Layers { get; init; } = [];
    /// <summary>Paths the model audit COMPLETED over: filled only when the model layer ended in Pass with nothing unreviewed (empty for any Fail/Error/NeedsReview/NotRun, so it is never a mere input list). Backward-compatible addition.</summary>
    public IReadOnlyList<string> ReviewedPaths { get; init; } = [];
    /// <summary>Stable machine-readable reason for a non-Pass state (backward-compatible addition).</summary>
    public string ReasonCode { get; init; } = string.Empty;
    public ArtifactAuditState State { get; init; }
    public string ArtifactId { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string ManifestHash { get; init; } = string.Empty;
    public string Scanner { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = string.Empty;
    public IReadOnlyList<ArtifactAuditFinding> Findings { get; init; } = [];
    public DateTimeOffset CompletedAtUtc { get; init; }
}

/// <summary>The Gateway receives identifiers, never a client-selected filesystem path.</summary>
public sealed record VerifyForgeArtifactRequest(string SessionId, string JobId);

public sealed record ArtifactCommandEvidence(string Stage, IReadOnlyList<string> Arguments, int? ExitCode,
    string Status, string OutputHash, long DurationMilliseconds)
{
    /// <summary>Backend-produced execution identity and enforced resource limits; never project output.</summary>
    public string IsolationEvidence { get; init; } = string.Empty;
}

public sealed record ForgeArtifactVerificationReceipt
{
    public ForgeArtifactManifest? Manifest { get; init; }
    public string ManifestHash { get; init; } = string.Empty;
    public bool IdentityVerified { get; init; }
    public bool BuildPassed { get; init; }
    public int? BuildExitCode { get; init; }
    public bool TestsPassed { get; init; }
    public int? TestExitCode { get; init; }
    public int TestCount { get; init; }
    public string VerificationImage { get; init; } = string.Empty;
    public string VerificationImageId { get; init; } = string.Empty;
    public IReadOnlyList<string> VerificationProjects { get; init; } = [];
    public IReadOnlyList<ArtifactCommandEvidence> Commands { get; init; } = [];
    public IReadOnlyList<string> TestEvidenceHashes { get; init; } = [];
    public ArtifactAuditResult Audit { get; init; } = new();
    public string? FailureCode { get; init; }
    public string? Message { get; init; }
    public string VerificationStatus { get; init; } = string.Empty;
    public bool VerifiedWithoutIsolation { get; init; }
    public string? VerificationNotice { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
