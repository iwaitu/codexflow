// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using System.Globalization;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Services;
using CodexFlow.Services.ArtifactAudit.Ecosystems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services.ArtifactAudit;

public interface IArtifactAuditService
{
    /// <summary>
    /// Audits the artifact described by the trusted <paramref name="manifest"/>.
    /// <paramref name="freshSnapshot"/> MUST be a newly opened pinned-commit session (<c>PinToBaseCommit = true</c>,
    /// <c>BaseCommit = manifest.HeadCommit</c>) on which no build/test has run. The caller owns and disposes the session.
    /// Never throws for scan problems: every failure is an <see cref="ArtifactAuditState.Error"/>/NeedsReview result.
    /// Only caller cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="requiredReviewPaths">
    /// Optional complete server-side review set (union of verified parents' and current changed paths). The model audit then
    /// covers this set plus <c>manifest.ChangedPaths</c>; unreadable or over-budget paths keep the result at NeedsReview.
    /// The manifest/hash/commit binding is unchanged.
    /// </param>
    /// <param name="deletedPathEvidence">
    /// Optional host-read text of paths in the review set that no longer exist in the snapshot (deleted or renamed away). Without it
    /// such a path keeps the model layer at NeedsReview, because nothing shows what was removed.
    /// </param>
    /// <param name="existingDebt">
    /// Optional dependency advisories that already existed at the chain's base commit and were accepted together with the plan. Matching
    /// advisories are reported in <c>BaselineDebt</c> and do not block; anything else the change introduces still does.
    /// </param>
    /// <param name="changedLines">
    /// Optional host-derived lines the change touched, per path. With it (and <c>Model.ScopeToChangedLines</c>) the model layer answers for those lines and
    /// lower-severity findings elsewhere are reported as <c>BaselineDebt</c>. Null keeps the whole-file behaviour.
    /// </param>
    Task<ArtifactAuditResult> AuditAsync(ForgeArtifactManifest manifest, string manifestHash, IIsolatedExecutionSession freshSnapshot, CancellationToken ct,
        IReadOnlyList<string>? requiredReviewPaths = null, IReadOnlyDictionary<string, DeletedPathEvidence>? deletedPathEvidence = null,
        ExistingDependencyDebt? existingDebt = null, IReadOnlyDictionary<string, IReadOnlyList<ChangedLineRange>>? changedLines = null);
}

public sealed class ArtifactAuditService : IArtifactAuditService
{
    public const string PolicyVersion = "artifact-audit/1";
    public const string ScannerName = "gitleaks+nuget-advisory-offline+chat-model";

    private readonly ArtifactAuditOptions _options;
    private readonly INuGetAdvisorySnapshotProvider _advisories;
    private readonly IArtifactModelAuditor _model;
    private readonly TimeProvider _time;
    private readonly ILogger<ArtifactAuditService>? _logger;
    private readonly IReadOnlyList<LockfileDependencyScanner> _lockfileScanners;
    private readonly IConfiguration? _configuration;
    private readonly IOptionsMonitor<ExecutionOptions>? _executionOptions;

    public ArtifactAuditService(
        IOptions<ArtifactAuditOptions> options,
        INuGetAdvisorySnapshotProvider advisories,
        IArtifactModelAuditor model,
        TimeProvider? time = null,
        ILogger<ArtifactAuditService>? logger = null,
        IOsvSnapshotProvider? osvAdvisories = null,
        IConfiguration? configuration = null,
        IOptionsMonitor<ExecutionOptions>? executionOptions = null)
    {
        _options = options.Value;
        _advisories = advisories;
        _model = model;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _configuration = configuration;
        _executionOptions = executionOptions;
        // Without OSV data the npm and Python manifests stay "unsupported", exactly as before.
        _lockfileScanners = osvAdvisories is null
            ? []
            : [new LockfileDependencyScanner(new NpmEcosystem(), _options, osvAdvisories, _time),
               new LockfileDependencyScanner(new PythonEcosystem(), _options, osvAdvisories, _time)];
    }

    public async Task<ArtifactAuditResult> AuditAsync(ForgeArtifactManifest manifest, string manifestHash, IIsolatedExecutionSession freshSnapshot, CancellationToken ct,
        IReadOnlyList<string>? requiredReviewPaths = null, IReadOnlyDictionary<string, DeletedPathEvidence>? deletedPathEvidence = null,
        ExistingDependencyDebt? existingDebt = null, IReadOnlyDictionary<string, IReadOnlyList<ChangedLineRange>>? changedLines = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(freshSnapshot);

        // ── bind the audit to exactly this manifest / commit ─────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(manifest.ArtifactId) || !AuditText.IsFullCommitId(manifest.HeadCommit))
            return Early(manifest, manifestHash, ArtifactAuditReasons.InvalidInput, "Manifest lacks an artifact id or a full head commit id.");
        if (!string.Equals(manifestHash, ArtifactQualityGate.ComputeManifestHash(manifest), StringComparison.Ordinal))
            return Early(manifest, manifestHash, ArtifactAuditReasons.ManifestHashMismatch, "The supplied manifest hash does not match the manifest.");

        var localPolicy = _configuration is not null
            ? LocalModeVerificationPolicy.Evaluate(_configuration, _executionOptions)
            : new LocalVerificationPolicyEvaluation(freshSnapshot.Layout.Backend == ExecutionBackend.Local, LocalVerificationModePolicy.Skip);

        if (localPolicy.IsLocalBackend || freshSnapshot.Layout.Backend == ExecutionBackend.Local)
        {
            var now = _time.GetUtcNow();
            var blocked = localPolicy.Policy == LocalVerificationModePolicy.Block;
            var state = blocked ? ArtifactAuditState.Fail : ArtifactAuditState.NotRun;
            var reasonCode = blocked ? LocalModeVerificationPolicy.BlockedStatus : LocalModeVerificationPolicy.SkippedReasonCode;
            var message = blocked
                ? "本地执行模式下已配置阻止未隔离审计；请切换 Execution:Backend=Docker 后重试。"
                : LocalModeVerificationPolicy.UnverifiedNotice;

            return new ArtifactAuditResult
            {
                State = state,
                ArtifactId = manifest.ArtifactId,
                Commit = manifest.HeadCommit,
                ManifestHash = manifestHash,
                ReasonCode = reasonCode,
                CompletedAtUtc = now,
                Layers =
                [
                    new ArtifactAuditLayerResult
                    {
                        Name = "audit",
                        State = state,
                        ReasonCode = reasonCode,
                        Message = message,
                        CompletedAtUtc = now
                    }
                ]
            };
        }

        var outcomes = new List<AuditLayerOutcome>();

        // ── the snapshot must be untouched: no build/restore/test leftovers ──────────────────────────────────
        var find = await freshSnapshot.RunAsync(new(SnapshotInventory.FindArgv(), Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
        if (!AuditText.CommandSucceeded(find))
            return Finish(manifest, manifestHash, [Layer("snapshot", ArtifactAuditState.Error, ArtifactAuditReasons.SnapshotInventoryFailed,
                $"Could not inventory the snapshot ({find.Status}/{find.Decision.ReasonCode}).")], ArtifactAuditReasons.SnapshotInventoryFailed);
        var inventory = SnapshotInventory.Parse(find.Stdout);
        if (inventory.UsedSnapshotMarkers.Count > 0)
            return Finish(manifest, manifestHash, [Layer("snapshot", ArtifactAuditState.Error, ArtifactAuditReasons.SnapshotNotFresh,
                $"The snapshot already contains build/restore/test output ({AuditText.Sanitize(inventory.UsedSnapshotMarkers[0], 120)}); a fresh immutable snapshot is required.")], ArtifactAuditReasons.SnapshotNotFresh);

        // ── freeze the model input NOW: only `cat` on the pristine tree, before any restore/project code can touch it ──
        ModelAuditInput? frozenModelInput = null;
        try
        {
            frozenModelInput = await ModelAuditInputBuilder.BuildAsync(freshSnapshot, manifest, manifestHash, _options.Model, ct, requiredReviewPaths, deletedPathEvidence).ConfigureAwait(false);
            if (_options.Model.ScopeToChangedLines && changedLines is not null)
                frozenModelInput = frozenModelInput with { ChangedLines = changedLines, LineMargin = Math.Max(0, _options.Model.ChangedLineMargin) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reading model audit input failed");
        }

        // ── layer 1: secrets, always on the pristine tree ──────────────────────────────────────────────────
        outcomes.Add(await RunLayerAsync("secrets", () => new GitleaksSecretScanner(_options).ScanAsync(freshSnapshot, ct), ct).ConfigureAwait(false));

        // ── layer 2: dependencies against the host-managed advisory snapshot ───────────────────────────────
        outcomes.Add(await RunLayerAsync("dependencies", () => RunDependencyLayerAsync(freshSnapshot, inventory, existingDebt, ct), ct).ConfigureAwait(false));

        // ── layer 3: model opinion; never sees a tree with known secrets, never rescues a failed scan ────────
        if (outcomes.Any(o => o.State is ArtifactAuditState.Fail or ArtifactAuditState.Error))
        {
            outcomes.Add(new AuditLayerOutcome("model", ArtifactAuditState.NotRun, ArtifactAuditReasons.ModelSkipped,
                "Skipped because a deterministic layer failed or errored.", "chat-model", string.Empty, string.Empty, []));
        }
        else
        {
            outcomes.Add(frozenModelInput is null
                ? new AuditLayerOutcome("model", ArtifactAuditState.Error, ArtifactAuditReasons.ModelError,
                    "Could not read the changed files from the pristine snapshot.", "chat-model", string.Empty, string.Empty, [])
                : await RunLayerAsync("model", () => _model.AuditAsync(frozenModelInput, ct), ct).ConfigureAwait(false));
        }

        var modelPassed = outcomes.Any(o => o.Name == "model" && o.State == ArtifactAuditState.Pass);
        return Finish(manifest, manifestHash, outcomes.Select(o => (o, _time.GetUtcNow())).ToList(), reviewedPaths: modelPassed && frozenModelInput is not null && frozenModelInput.Unreviewed.Count == 0
                ? frozenModelInput.Files.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal).ToList() : null);
    }

    /// <summary>
    /// The dependency layer: one scanner per ecosystem present in the snapshot, combined into one result. The worst state wins, so a
    /// vulnerable npm package fails the layer even when the .NET part is clean, and an ecosystem without usable data is an Error.
    /// </summary>
    private async Task<AuditLayerOutcome> RunDependencyLayerAsync(IIsolatedExecutionSession snapshot, SnapshotInventory inventory,
        ExistingDependencyDebt? existingDebt, CancellationToken ct)
    {
        var applicable = _lockfileScanners.Where(s => s.Ecosystem.ClaimedManifests(inventory).Count > 0).ToList();
        if (applicable.Count == 0)
            return await new NuGetDependencyScanner(_options, _advisories, _time).ScanAsync(snapshot, inventory, ct, existingDebt).ConfigureAwait(false);

        var claimed = applicable.SelectMany(s => s.Ecosystem.ClaimedManifests(inventory)).ToHashSet(StringComparer.Ordinal);
        var rest = inventory with { UnsupportedManifests = inventory.UnsupportedManifests.Where(m => !claimed.Contains(m)).ToList() };

        var outcomes = new List<AuditLayerOutcome>();
        var dotNet = rest.ProjectFiles.Count > 0 || rest.LegacyPackagesConfigs.Count > 0;
        if (dotNet)
        {
            outcomes.Add(await new NuGetDependencyScanner(_options, _advisories, _time).ScanAsync(snapshot, rest, ct, existingDebt).ConfigureAwait(false));
        }
        else if (rest.UnsupportedManifests.Count > 0)
        {
            // No .NET project, so the NuGet advisory data is not needed; manifests nothing covers are still reported.
            var findings = rest.UnsupportedManifests.Select(manifest => new ArtifactAuditFinding(
                AuditText.Fingerprint("dependencies", "ecosystem", manifest), "Unknown", manifest,
                "Dependency manifest of an ecosystem this gate cannot scan; not covered by any vulnerability check.")
            { Layer = "dependencies", Category = ArtifactAuditReasons.UnsupportedEcosystem }).ToList();
            outcomes.Add(new AuditLayerOutcome("dependencies", ArtifactAuditState.NeedsReview, ArtifactAuditReasons.UnsupportedEcosystem,
                $"{findings.Count} dependency manifest(s) of unsupported ecosystems.", "unsupported-ecosystem", string.Empty, string.Empty, findings));
        }

        foreach (var scanner in applicable)
        {
            outcomes.Add(await scanner.ScanAsync(snapshot, inventory, ct, existingDebt).ConfigureAwait(false));
        }

        return CombineDependencyOutcomes(outcomes);
    }

    internal static AuditLayerOutcome CombineDependencyOutcomes(IReadOnlyList<AuditLayerOutcome> outcomes)
    {
        if (outcomes.Count == 1) return outcomes[0];
        var state = AuditStatePolicy.Combine(outcomes.Select(o => o.State));
        var dominant = outcomes.FirstOrDefault(o => o.State == state && state != ArtifactAuditState.Pass);
        return new AuditLayerOutcome("dependencies", state, dominant?.ReasonCode ?? ArtifactAuditReasons.Clean,
            string.Join(" | ", outcomes.Select(o => o.Message)),
            string.Join("+", outcomes.Select(o => o.Tool).Where(t => t.Length > 0).Distinct()),
            outcomes.Select(o => o.ToolVersion).FirstOrDefault(v => v.Length > 0) ?? string.Empty,
            string.Join("; ", outcomes.Select(o => o.DataVersion).Where(v => v.Length > 0)),
            outcomes.SelectMany(o => o.Findings).ToList())
        {
            BaselineDebt = outcomes.SelectMany(o => o.BaselineDebt).ToList(),
            Remediation = outcomes.SelectMany(o => o.Remediation).ToList()
        };
    }

    private async Task<AuditLayerOutcome> RunLayerAsync(string name, Func<Task<AuditLayerOutcome>> run, CancellationToken ct)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Artifact audit layer {Layer} crashed", name);
            return new AuditLayerOutcome(name, ArtifactAuditState.Error, ArtifactAuditReasons.ScanFailed,
                "Audit layer crashed: " + AuditText.Redact(ex.GetType().Name + ": " + ex.Message, 200), string.Empty, string.Empty, string.Empty, []);
        }
    }

    private static (AuditLayerOutcome, DateTimeOffset) Layer(string name, ArtifactAuditState state, string reason, string message) =>
        (new AuditLayerOutcome(name, state, reason, message, string.Empty, string.Empty, string.Empty, []), default);

    private ArtifactAuditResult Early(ForgeArtifactManifest manifest, string manifestHash, string reason, string message) =>
        Finish(manifest, manifestHash, [(new AuditLayerOutcome("input", ArtifactAuditState.Error, reason, message, string.Empty, string.Empty, string.Empty, []), default)], reason);

    private ArtifactAuditResult Finish(ForgeArtifactManifest manifest, string manifestHash, IReadOnlyList<(AuditLayerOutcome Outcome, DateTimeOffset At)> layers, string? reason = null, IReadOnlyList<string>? reviewedPaths = null)
    {
        var now = _time.GetUtcNow();
        var results = layers.Select(l => l.Outcome.ToLayerResult(l.At == default ? now : l.At)).ToList();
        var state = AuditStatePolicy.Combine(layers.Select(l => l.Outcome.State));
        var findings = layers.SelectMany(l => l.Outcome.Findings).Take(400).ToList();
        var versions = layers.Select(l => l.Outcome).Where(o => o.Tool.Length > 0)
            .Select(o => $"{o.Name}={o.Tool}{(o.ToolVersion.Length > 0 ? " " + o.ToolVersion : string.Empty)}{(o.DataVersion.Length > 0 ? " [" + o.DataVersion + "]" : string.Empty)}");
        var dominant = layers.Select(l => l.Outcome).FirstOrDefault(o => o.State == state && state != ArtifactAuditState.Pass);
        return new ArtifactAuditResult
        {
            State = state,
            ArtifactId = manifest.ArtifactId,
            Commit = manifest.HeadCommit,
            ManifestHash = manifestHash,
            Scanner = ScannerName,
            ScannerVersion = PolicyVersion + "; " + string.Join("; ", versions),
            Findings = findings,
            BaselineDebt = layers.SelectMany(l => l.Outcome.BaselineDebt).Take(400).ToList(),
            Remediation = layers.SelectMany(l => l.Outcome.Remediation).Take(200).ToList(),
            Layers = results,
            ReviewedPaths = reviewedPaths ?? [],
            ReasonCode = state == ArtifactAuditState.Pass ? string.Empty : reason ?? dominant?.ReasonCode ?? string.Empty,
            CompletedAtUtc = now
        };
    }
}
