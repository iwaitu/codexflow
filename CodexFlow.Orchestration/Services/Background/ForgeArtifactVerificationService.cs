// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Hosting;
using System.Text.Json;
using System.Xml.Linq;
using CodexFlow.Application.Jobs;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Services;
using CodexFlow.Domain.Jobs;
using CodexFlow.Services.ArtifactAudit;
using CodexFlow.Services.Verification;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services.Background;

/// <summary>Loads trusted identity from storage and verifies exclusively through the isolated backend.</summary>
public sealed class ForgeArtifactVerificationService(
    IWorkerJobStore storage, IGatewayChatStore chat, IIsolatedExecutionService execution, IConfiguration configuration, IArtifactAuditService auditService,
    IPlanArtifactStore planStore, ILogger<ForgeArtifactVerificationService>? logger = null, ISessionStore? runtimeSessions = null,
    IDependencyRiskScanService? dependencyScan = null, IEnumerable<IProjectVerifier>? projectVerifiers = null,
    IOptionsMonitor<ExecutionOptions>? executionOptions = null)
    : IForgeArtifactVerificationService
{
    private readonly IReadOnlyList<IProjectVerifier> _projectVerifiers =
        (projectVerifiers ?? []).ToArray() is { Length: > 0 } configured ? configured : [new DotNetProjectVerifier()];

    public async Task<ForgeArtifactVerificationReceipt> VerifyAsync(string userId, VerifyForgeArtifactRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            var receipt = await VerifyCoreAsync(userId, request, deadline.Token);
            logger?.LogInformation(
                "Artifact verification result for job {JobId}. Failure={FailureCode} Identity={IdentityVerified} Build={BuildPassed} Tests={TestsPassed} TestCount={TestCount} Audit={AuditState} AuditReason={AuditReason} Layers={Layers}",
                request.JobId, receipt.FailureCode, receipt.IdentityVerified, receipt.BuildPassed,
                receipt.TestsPassed, receipt.TestCount, receipt.Audit?.State, receipt.Audit?.ReasonCode,
                receipt.Audit is { } audit ? string.Join(", ", audit.Layers.Select(layer => $"{layer.Name}:{layer.State}:{layer.ReasonCode}")) : null);
            return receipt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed("ArtifactVerificationTimeout"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or
            JsonException or OverflowException or OperationCanceledException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or ArgumentException or HttpRequestException or System.Data.Common.DbException)
        {
            logger?.LogWarning(ex, "Artifact verification failed for session {SessionId}, job {JobId}", request.SessionId, request.JobId);
            return Failed("ArtifactVerificationError");
        }
    }

    private async Task<ForgeArtifactVerificationReceipt> VerifyCoreAsync(string userId, VerifyForgeArtifactRequest request, CancellationToken ct)
    {
        var session = await chat.GetSession(request.SessionId);
        if (session is null || session.UserId != userId) return Failed("ArtifactNotFound");
        var jobs = await storage.ReadSessionJobsAsync(request.SessionId, ct);
        var job = jobs.SingleOrDefault(x => x.JobId == request.JobId);
        if (job is null) return Failed("ArtifactNotFound");
        if (!CodexFlow.Core.Workers.WorkerJobTypes.TryResolveWorkerType(job.JobType, out var type) ||
            type != CodexFlow.Core.Protocols.WorkerType.Forge) return Failed("ArtifactJobTypeMismatch");
        if (job.Status != BackgroundJobStatus.Completed) return Failed("ArtifactJobNotCompleted");
        var manifest = ForgeArtifactManifestFactory.ParseManifest(job.ResultPayloadJson);
        if (manifest is null) return Failed("LegacyUnverified");
        if (manifest.TaskId != (job.TaskId ?? job.JobId)) return Failed("ArtifactOwnershipMismatch");
        var receipt = new ForgeArtifactVerificationReceipt { Manifest = manifest, ManifestHash = GitArtifactInspector.ManifestHash(manifest) };
        var planError = await ValidatePlanAsync(manifest, userId, jobs, ct);
        if (planError is not null) return Failed(planError, receipt);
        var planJobs = LatestPlanJobs(jobs, manifest);
        var completedForgeJobs = planJobs.Where(x => x.JobId != job.JobId && x.Status == BackgroundJobStatus.Completed &&
            CodexFlow.Core.Workers.WorkerJobTypes.TryResolveWorkerType(x.JobType, out var workerType) &&
            workerType == CodexFlow.Core.Protocols.WorkerType.Forge).ToArray();
        if (completedForgeJobs.Any(x => ForgeArtifactManifestFactory.ParseManifest(x.ResultPayloadJson) is null))
            return Failed("LegacyUnverifiedParent", receipt);
        var parents = completedForgeJobs
            .Select(x => ForgeArtifactManifestFactory.ParseManifest(x.ResultPayloadJson)).OfType<ForgeArtifactManifest>().ToArray();
        // A newer successful attempt must never be replaced with stale evidence.
        if (!planJobs.Any(x => x.JobId == job.JobId))
            return Failed("ArtifactSuperseded", receipt);
        var inspector = new GitArtifactInspector();
        var selectedProjectUrl = session.ProjectUrl;
        if (runtimeSessions is not null)
        {
            var runtimeSession = await runtimeSessions.GetSessionAsync(request.SessionId, ct);
            if (runtimeSession is not null)
            {
                if (runtimeSession.Id != request.SessionId || runtimeSession.UserId != userId)
                    return Failed("ArtifactOwnershipMismatch", receipt);
                selectedProjectUrl = runtimeSession.ProjectUrl?.AbsoluteUri ?? selectedProjectUrl;
            }
        }
        var root = await ResolveAuthorizedRepositoryAsync(session.WorkspacePath, selectedProjectUrl, job.PayloadJson, inspector, ct);
        if (string.IsNullOrWhiteSpace(root)) return Failed("ArtifactRepositoryUnavailable", receipt);
        var repositoryAuthorization = LocalRepositoryAuthorization.Evaluate(root, session.WorkspacePath, configuration);
        if (!repositoryAuthorization.Allowed)
            return Failed(repositoryAuthorization.Reason == LocalRepositoryDenial.InvalidConfiguration
                ? "ArtifactRepositoryAuthorizationMisconfigured" : "ArtifactRepositoryNotAuthorized", receipt);
        var error = await inspector.ValidateAsync(manifest, root, userId, request.SessionId, job.JobId, job.Attempt, parents, ct);
        if (error is not null) return Failed(error, receipt);
        receipt = receipt with { IdentityVerified = true };

        var localPolicy = LocalModeVerificationPolicy.Evaluate(configuration, executionOptions);
        if (localPolicy.IsLocalBackend)
        {
            return await HandleLocalBackendAsync(job, manifest, receipt, localPolicy.Policy, ct);
        }

        var context = new TrustedExecutionContext
        {
            UserId = userId, SessionId = request.SessionId, JobId = job.JobId, TaskId = manifest.TaskId,
            AttemptId = $"{job.JobId}:{job.Attempt}", WorkspaceRoot = manifest.WorktreePath,
            BaseCommit = manifest.HeadCommit, ScopeHash = manifest.ScopeHash, Scope = ExportScope.Empty,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(20)
        };
        var image = configuration["ArtifactVerification:Image"];
        if (string.IsNullOrWhiteSpace(image)) return Failed("VerificationImageNotConfigured", receipt);
        receipt = receipt with { VerificationImage = image };
        var opened = await execution.OpenSessionAsync(context, new(image, PinToBaseCommit: true), ct);
        if (!opened.Decision.IsAllowed || opened.Session is null) return Failed(opened.Decision.ReasonCode, receipt);
        await using var defaultSession = opened.Session;
        if (defaultSession.Layout.Backend == ExecutionBackend.Local)
        {
            return await HandleLocalBackendAsync(job, manifest, receipt, localPolicy.Policy, ct);
        }
        // The language-specific part (find the project, restore, build, test, read the test evidence) lives in an IProjectVerifier.
        var verifier = await ProjectVerifierSelector.SelectAsync(_projectVerifiers, defaultSession, ct);
        if (verifier is null) return Failed("ArtifactProjectCoverageUnavailable", receipt);
        IIsolatedExecutionSession isolated = defaultSession;
        // A language with its own image verifies in a fresh snapshot of that image (the snapshot used for detection only read files).
        var languageImage = verifier.ImageConfigurationKey is { } imageKey ? configuration[imageKey] : null;
        IsolatedSessionOpenResult? languageOpened = null;
        if (!string.IsNullOrWhiteSpace(languageImage) && !string.Equals(languageImage, image, StringComparison.Ordinal))
        {
            languageOpened = await execution.OpenSessionAsync(context, new(languageImage, PinToBaseCommit: true), ct);
            if (!languageOpened.Decision.IsAllowed || languageOpened.Session is null) return Failed(languageOpened.Decision.ReasonCode, receipt);
            isolated = languageOpened.Session;
            receipt = receipt with { VerificationImage = languageImage };
        }

        await using var languageSession = languageOpened?.Session;
        if (isolated.Layout.Backend == ExecutionBackend.Local)
        {
            return await HandleLocalBackendAsync(job, manifest, receipt, localPolicy.Policy, ct);
        }
        var runner = new VerificationStageRunner(isolated, receipt, manifest.JobId, logger);
        var verificationFailure = await verifier.VerifyAsync(runner, ct);
        receipt = runner.Receipt;
        if (verificationFailure is not null) return Failed(verificationFailure, receipt);
        // Audit a fresh immutable snapshot, not the filesystem modified by the project's build/test code.
        var auditOpen = await execution.OpenSessionAsync(context, new(image, PinToBaseCommit: true), ct);
        if (!auditOpen.Decision.IsAllowed || auditOpen.Session is null) return Failed(auditOpen.Decision.ReasonCode, receipt);
        await using var auditSession = auditOpen.Session;
        if (auditSession.Layout.Backend == ExecutionBackend.Local)
        {
            return await HandleLocalBackendAsync(job, manifest, receipt, localPolicy.Policy, ct);
        }
        ArtifactAuditResult audit;
        // The final task may touch only tests; include every verified ancestor task's changes.
        var reviewPaths = parents.Append(manifest).SelectMany(artifact => artifact.ChangedPaths)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // A deleted or renamed-away path has no content in the fixed snapshot. Read what was removed from Git objects on the host
        // (no repository code runs); anything unreadable simply stays without evidence and keeps the model layer at NeedsReview.
        var deletedEvidence = new Dictionary<string, DeletedPathEvidence>(StringComparer.Ordinal);
        foreach (var reviewPath in reviewPaths)
        {
            var preState = await inspector.TryReadDeletedFilePreStateAsync(manifest.WorktreePath, manifest.HeadCommit, reviewPath, ct);
            if (preState is not null) deletedEvidence[reviewPath] = new DeletedPathEvidence(preState.Content, preState.DeletedInCommit);
        }
        // Dependency advisories that already existed when the plan started and were accepted with its approval (never anything the change introduces).
        var existingDebt = await ResolveExistingDependencyDebtAsync(userId, manifest, parents, inspector, ct);
        // What this chain touched, from the chain's starting commit to the head: the model audit answers for those lines. Unknown (no single
        // starting commit, or git could not tell) means null, and then nothing is set aside.
        var changedLines = await ResolveChangedLinesAsync(manifest, parents, reviewPaths, inspector, ct);
        try { audit = await auditService.AuditAsync(manifest, receipt.ManifestHash, auditSession, ct, reviewPaths, deletedEvidence, existingDebt, changedLines); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or
            UnauthorizedAccessException or ArgumentException or HttpRequestException)
        {
            // Preserve completed technical evidence when the independent security stage fails.
            return Failed("ArtifactAuditError", receipt with { Audit = new() { State = ArtifactAuditState.Error } });
        }
        receipt = receipt with { Audit = audit };
        if (audit.State == ArtifactAuditState.Pass &&
            (audit.ReviewedPaths is null || !audit.ReviewedPaths.Order(StringComparer.Ordinal).SequenceEqual(reviewPaths, StringComparer.Ordinal)))
            return Failed("ArtifactAuditCoverageIncomplete", receipt);
        error = await inspector.ValidateAsync(manifest, root, userId, request.SessionId, job.JobId, job.Attempt, parents, ct);
        if (error is not null) return Failed(error, receipt with { IdentityVerified = false });
        var currentJobs = await storage.ReadSessionJobsAsync(request.SessionId, ct);
        var current = currentJobs.SingleOrDefault(x => x.JobId == job.JobId);
        if (current is null || current.Attempt != job.Attempt || current.Status != BackgroundJobStatus.Completed || current.ResultPayloadJson != job.ResultPayloadJson)
            return Failed("ArtifactAttemptChanged", receipt with { IdentityVerified = false });
        planError = await ValidatePlanAsync(manifest, userId, currentJobs, ct);
        if (planError is not null) return Failed(planError, receipt with { IdentityVerified = false });
        var currentParents = LatestPlanJobs(currentJobs, manifest).Where(x => x.JobId != job.JobId && x.Status == BackgroundJobStatus.Completed &&
            CodexFlow.Core.Workers.WorkerJobTypes.TryResolveWorkerType(x.JobType, out var workerType) &&
            workerType == CodexFlow.Core.Protocols.WorkerType.Forge).ToArray();
        if (currentParents.Length != completedForgeJobs.Length || currentParents.Any(parent => !completedForgeJobs.Any(original =>
            original.JobId == parent.JobId && original.Attempt == parent.Attempt && original.ResultPayloadJson == parent.ResultPayloadJson)))
            return Failed("ArtifactParentSetChanged", receipt with { IdentityVerified = false });
        receipt = receipt with { CompletedAtUtc = DateTimeOffset.UtcNow };
        return ArtifactQualityGate.Passes(receipt, request.SessionId, request.JobId) ? receipt : Failed("SecurityGateNotPassed", receipt);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<CodexFlow.Core.Services.ChangedLineRange>>?> ResolveChangedLinesAsync(
        ForgeArtifactManifest manifest, IReadOnlyList<ForgeArtifactManifest> parents, IReadOnlyList<string> reviewPaths, GitArtifactInspector inspector, CancellationToken ct)
    {
        var chainBase = await inspector.FindChainBaseAsync(manifest.WorktreePath, parents.Append(manifest).ToArray(), ct);
        return chainBase is null ? null : await inspector.GetChangedLineRangesAsync(manifest.WorktreePath, chainBase, manifest.HeadCommit, reviewPaths, ct);
    }

    /// <summary>
    /// When the approved plan accepts existing dependency debt, scans the commit the chain started from and returns the advisories found
    /// there. Null (= nothing is accepted, every advisory blocks) when the plan did not accept it, the chain has no single starting
    /// commit, or the base scan could not produce evidence. A missing base scan never loosens the gate.
    /// </summary>
    private async Task<ExistingDependencyDebt?> ResolveExistingDependencyDebtAsync(string userId, ForgeArtifactManifest manifest,
        IReadOnlyList<ForgeArtifactManifest> parents, GitArtifactInspector inspector, CancellationToken ct)
    {
        if (dependencyScan is null) return null;
        // The approval is for exactly this plan hash and version: ValidatePlanAsync has already proven it matches the manifest.
        var plan = await planStore.GetCurrentAsync(manifest.SessionId, ct);
        if (plan?.Approval is not { AcceptsExistingDependencyDebt: true } approval ||
            approval.ApprovedMarkdownHash != manifest.PlanHash || approval.ApprovedPlanVersion != manifest.PlanVersion) return null;

        var chainBase = await inspector.FindChainBaseAsync(manifest.WorktreePath, parents.Append(manifest).ToArray(), ct);
        if (chainBase is null) return null;

        var report = await dependencyScan.ScanCommitAsync(manifest.WorktreePath, chainBase, userId, manifest.SessionId, "chain-base", ct);
        if (!report.Available)
        {
            logger?.LogWarning("Base scan of {Commit} produced no evidence ({Message}); existing dependency debt is not accepted", chainBase, report.Message);
            return null;
        }

        return ExistingDependencyDebt.FromReport(report, approval.ApprovedByUserId, approval.ApprovedAtUtc);
    }

    private static bool Succeeded(IsolatedCommandResult result) =>
        result.Decision.IsAllowed && result.Status == IsolatedCommandStatus.Completed && result.ExitCode == 0 && !result.Truncated;

    private async Task<string?> ValidatePlanAsync(ForgeArtifactManifest manifest, string userId,
        IReadOnlyList<BackgroundJob> jobs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifest.PlanArtifactId) || string.IsNullOrWhiteSpace(manifest.PlanVersion) ||
            string.IsNullOrWhiteSpace(manifest.PlanHash) || manifest.RequiredTaskIds is not { Count: > 0 })
            return "ArtifactPlanUnverified";
        var plan = await planStore.GetCurrentAsync(manifest.SessionId, ct);
        if (plan is null || plan.Id != manifest.PlanArtifactId || plan.Version != manifest.PlanVersion ||
            PlanArtifactSupport.ComputeMarkdownHash(plan.Markdown) != manifest.PlanHash || plan.SessionId != manifest.SessionId)
            return "ArtifactPlanChanged";
        if (plan.Approval?.ApprovedByUserId != userId || plan.Approval.ApprovedMarkdownHash != manifest.PlanHash ||
            plan.Approval.ApprovedPlanVersion != manifest.PlanVersion ||
            plan.Status is PlanArtifactStatus.Rejected or PlanArtifactStatus.Superseded or PlanArtifactStatus.Failed or
                PlanArtifactStatus.Draft or PlanArtifactStatus.NeedsClarification or PlanArtifactStatus.ReadyForApproval)
            return "ArtifactPlanNotApproved";
        var required = plan.Tasks.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray();
        if (required.Length == 0 || required.Any(string.IsNullOrWhiteSpace) || required.Distinct(StringComparer.Ordinal).Count() != required.Length ||
            !required.SequenceEqual(manifest.RequiredTaskIds.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !required.Contains(manifest.TaskId, StringComparer.Ordinal)) return "ArtifactPlanTaskMismatch";
        var latest = LatestPlanJobs(jobs, manifest);
        return plan.Tasks.Any(task => !latest.Any(x => x.TaskId == task.Id && x.Status == BackgroundJobStatus.Completed &&
            CodexFlow.Core.Workers.WorkerJobTypes.TryResolveWorkerType(x.JobType, out var type) &&
            type == (CodexFlow.Core.Models.CodexTaskClassifier.IsAnalysisTask(task)
                ? CodexFlow.Core.Protocols.WorkerType.Explore : CodexFlow.Core.Protocols.WorkerType.Forge)))
            ? "ArtifactPlanIncomplete" : null;
    }

    private static BackgroundJob[] LatestPlanJobs(IReadOnlyList<BackgroundJob> jobs, ForgeArtifactManifest manifest) =>
        jobs.Where(job =>
        {
            if (string.IsNullOrWhiteSpace(job.PayloadJson)) return false;
            try
            {
                using var payload = JsonDocument.Parse(job.PayloadJson);
                var root = payload.RootElement;
                return root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("planArtifactId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == manifest.PlanArtifactId &&
                    root.TryGetProperty("planVersion", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == manifest.PlanVersion &&
                    root.TryGetProperty("planHash", out var hash) && hash.ValueKind == JsonValueKind.String && hash.GetString() == manifest.PlanHash;
            }
            catch (JsonException) { return false; }
        }).GroupBy(x => x.TaskId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.JobId, StringComparer.Ordinal).First()).ToArray();

    private async Task<ForgeArtifactVerificationReceipt> HandleLocalBackendAsync(
        BackgroundJob expectedJob,
        ForgeArtifactManifest manifest,
        ForgeArtifactVerificationReceipt receipt,
        LocalVerificationModePolicy policy,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var persisted = await PersistLocalVerificationOutcomeAsync(expectedJob, policy, now, ct);
        if (!persisted)
        {
            return Failed("ArtifactAttemptChanged", receipt with { IdentityVerified = false });
        }

        if (policy == LocalVerificationModePolicy.Block)
        {
            return Failed(
                LocalModeVerificationPolicy.BlockedReasonCode,
                receipt with
                {
                    VerificationStatus = LocalModeVerificationPolicy.BlockedStatus,
                    VerificationNotice = "本地执行模式下已配置阻止未隔离验证；请切换 Execution:Backend=Docker 后重试。"
                });
        }

        return receipt with
        {
            FailureCode = null,
            Message = LocalModeVerificationPolicy.UnverifiedNotice,
            VerificationStatus = LocalModeVerificationPolicy.SkippedReasonCode,
            VerifiedWithoutIsolation = true,
            VerificationNotice = LocalModeVerificationPolicy.UnverifiedNotice,
            CompletedAtUtc = now,
            Audit = new ArtifactAuditResult
            {
                State = ArtifactAuditState.NotRun,
                ArtifactId = manifest.ArtifactId,
                Commit = manifest.HeadCommit,
                ManifestHash = receipt.ManifestHash,
                ReasonCode = LocalModeVerificationPolicy.SkippedReasonCode,
                CompletedAtUtc = now,
                Layers =
                [
                    new ArtifactAuditLayerResult
                    {
                        Name = "verification",
                        State = ArtifactAuditState.NotRun,
                        ReasonCode = LocalModeVerificationPolicy.SkippedReasonCode,
                        Message = LocalModeVerificationPolicy.UnverifiedNotice,
                        CompletedAtUtc = now
                    }
                ]
            }
        };
    }

    private async Task<bool> PersistLocalVerificationOutcomeAsync(
        BackgroundJob expectedJob,
        LocalVerificationModePolicy policy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var payload = string.IsNullOrWhiteSpace(expectedJob.ResultPayloadJson)
            ? new Newtonsoft.Json.Linq.JObject()
            : Newtonsoft.Json.Linq.JObject.Parse(expectedJob.ResultPayloadJson);

        string? errorMessage = expectedJob.ErrorMessage;
        if (policy == LocalVerificationModePolicy.Block)
        {
            // Keep Status=Completed so switching Execution:Backend back to Docker allows direct re-verification.
            errorMessage = "本地执行模式下已配置阻止未隔离验证；请切换 Execution:Backend=Docker 后重试。";
            payload["verification"] = LocalModeVerificationPolicy.BlockedStatus;
            payload["verificationNotice"] = errorMessage;
        }
        else
        {
            payload["verification"] = LocalModeVerificationPolicy.SkippedReasonCode;
            payload["verifiedWithoutIsolation"] = true;
            payload["verificationNotice"] = LocalModeVerificationPolicy.UnverifiedNotice;
        }

        return await storage.TryUpdateVerificationPayloadAsync(
            expectedJob, payload.ToString(Newtonsoft.Json.Formatting.None), errorMessage, now.UtcDateTime, ct);
    }

    private static ForgeArtifactVerificationReceipt Failed(string code, ForgeArtifactVerificationReceipt? receipt = null) =>
        (receipt ?? new()) with { FailureCode = code, Message = code, CompletedAtUtc = DateTimeOffset.UtcNow };

    // The job payload is host-owned. It identifies the exact repository selected at dispatch; do not guess
    // by directory name or last-modified time when a session contains multiple clones.
    private static async Task<string?> ResolveAuthorizedRepositoryAsync(string sessionRoot, string? projectUrl, string? payload,
        GitArtifactInspector inspector, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot) || !Directory.Exists(sessionRoot)) return null;
        var root = sessionRoot;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("mainWorkspacePath", out var main) && main.ValueKind == JsonValueKind.String)
                root = main.GetString() ?? sessionRoot;
            else if (json.RootElement.TryGetProperty("workspacePath", out var workspace) && workspace.ValueKind == JsonValueKind.String)
                root = workspace.GetString() ?? sessionRoot;
        }
        if (!Path.IsPathRooted(root) || !Directory.Exists(root)) return null;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullSession = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Reject reparse points in the repository's ancestry, including a substituted session directory.
        for (var directory = new DirectoryInfo(fullRoot); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        if (string.Equals(fullRoot, fullSession, comparison) || fullRoot.StartsWith(fullSession + Path.DirectorySeparatorChar, comparison))
            return fullRoot;
        // Gateway can attach an explicitly selected local repository outside its session storage directory.
        // Authorize only the exact persisted project URL, never a path asserted solely by a worker payload.
        if (Uri.TryCreate(projectUrl, UriKind.Absolute, out var selectedProject) && selectedProject.IsFile &&
            string.Equals(fullRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedProject.LocalPath)), comparison))
            return fullRoot;
        // A retained shadow worktree can be the session root, but both paths must resolve to the same Git store.
        if (!Directory.Exists(Path.Combine(fullSession, ".git")) && !File.Exists(Path.Combine(fullSession, ".git"))) return null;
        return await inspector.GetRepositoryIdentityAsync(fullRoot, ct) == await inspector.GetRepositoryIdentityAsync(fullSession, ct)
            ? fullRoot : null;
    }

}
