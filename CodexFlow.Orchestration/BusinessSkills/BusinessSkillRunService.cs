// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Gateway;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.BusinessSkills;

public interface IBusinessSkillRunService
{
    Task<BusinessServiceResult<SkillRunDto>> StartDraftTestRunAsync(string draftId, StartSkillRunRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillRunDto>> StartPublishedSkillRunAsync(string skillId, StartSkillRunRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillRunDto>> GetRunAsync(string runId, CancellationToken ct = default);
    Task<BusinessServiceResult<IReadOnlyList<SkillRunDto>>> ListRunsForSkillAsync(string skillId, CancellationToken ct = default);
    Task<BusinessServiceResult<SkillRunDto>> CancelRunAsync(string runId, CancellationToken ct = default);
    Task ReconcileOrphanedRunsOnStartupAsync(CancellationToken ct = default);
}

/// <summary>
/// P3 业务 Skill 受控运行、目录新鲜度检查、SQLite 检查点持久化、断点恢复与取消协调服务。
/// </summary>
public sealed class BusinessSkillRunService : IBusinessSkillRunService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IJobDbContext _db;
    private readonly IBusinessPluginControlService _pluginControl;
    private readonly IBusinessCredentialStore _credentialStore;
    private readonly IBusinessSkillApprovalCoordinator _approvalCoordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BusinessSkillRunService> _logger;

    public BusinessSkillRunService(
        IJobDbContext db,
        IBusinessPluginControlService pluginControl,
        IBusinessCredentialStore credentialStore,
        IBusinessSkillApprovalCoordinator approvalCoordinator,
        IServiceScopeFactory scopeFactory,
        ILogger<BusinessSkillRunService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(pluginControl);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _pluginControl = pluginControl;
        _credentialStore = credentialStore;
        _approvalCoordinator = approvalCoordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<BusinessServiceResult<SkillRunDto>> StartDraftTestRunAsync(
        string draftId,
        StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "clientRequestId is required.");
        }

        var draftScopeSkillId = $"draft:{draftId}";
        var clientReqId = request.ClientRequestId.Trim();
        var existing = await TryFindExistingIdempotentRunAsync(draftScopeSkillId, clientReqId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return BusinessServiceResult<SkillRunDto>.Ok(existing, 202);
        }

        var draft = await _db.SkillDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Draft '{draftId}' not found.");
        }

        var candidate = string.IsNullOrWhiteSpace(draft.CandidateDefinitionJson)
            ? null
            : JsonSerializer.Deserialize<SkillDefinitionV1>(draft.CandidateDefinitionJson, JsonOptions);
        if (candidate is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Draft has no candidateDefinition to test.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == draft.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        var snap = await _db.CatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == draft.CatalogSnapshotId && s.PluginId == draft.PluginId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null || snap is null || !plugin.Enabled)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable, "Plugin must remain enabled with valid connection and snapshot.");
        }

        var capMap = await LoadCapabilityMapAsync(snap.Id, ct).ConfigureAwait(false);
        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            candidate,
            plugin.Id,
            snap.Id,
            conn.Environment,
            capMap,
            isReadOnlyTestRun: !request.AllowWriteInTestRun);
        if (!validation.IsValid)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(
                422,
                validation.Errors[0].Code,
                validation.Errors[0].Message,
                fieldErrors: validation.Errors);
        }

        if (!SkillValueBinder.TryValidateRunInputs(candidate.Inputs, request.Inputs ?? new Dictionary<string, JsonElement>(), out var resolvedInputs, out var inputErrors))
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "Test-run inputs failed schema validation.", fieldErrors: inputErrors);
        }

        var credTuple = await ResolveExecutionCredentialAsync(conn, ct).ConfigureAwait(false);
        if (credTuple.MissingProtectedStore)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.AuthRequired,
                "Execution credential cannot be decrypted because persistent OS protection is unavailable.");
        }

        var hasWriteSteps = candidate.UsedCapabilityIds.Any(id =>
            capMap.TryGetValue(id, out var c) && c.Summary.Risk != CapabilityRiskLevel.Read);

        return await CreateAndLaunchRunAsync(new PendingLaunchSpec(
            RunId: $"srun-{Guid.NewGuid():N}"[..18],
            DbSkillId: draftScopeSkillId,
            SkillVersion: draft.Revision,
            ClientRequestId: clientReqId,
            Definition: candidate with { ContentHash = validation.ContentHash },
            Plugin: plugin,
            Connection: conn,
            Snapshot: snap,
            Capabilities: capMap,
            ResolvedInputs: resolvedInputs,
            AuthKind: credTuple.AuthKind,
            SecretJson: credTuple.SecretJson,
            IsTestRun: true,
            AllowWriteInTestRun: request.AllowWriteInTestRun,
            WriteStepUnverified: hasWriteSteps && !candidate.ValidationEvidence.WriteStepVerified,
            SessionId: request.SessionId,
            DraftIdForEvidence: draft.Id,
            DraftRevisionForEvidence: draft.Revision,
            StaticValidationHashForEvidence: validation.ContentHash), ct).ConfigureAwait(false);
    }

    public async Task<BusinessServiceResult<SkillRunDto>> StartPublishedSkillRunAsync(
        string skillId,
        StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "clientRequestId is required.");
        }

        var clientReqId = request.ClientRequestId.Trim();
        var existing = await TryFindExistingIdempotentRunAsync(skillId, clientReqId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return BusinessServiceResult<SkillRunDto>.Ok(existing, 202);
        }

        var skill = await _db.PublishedSkills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skillId, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Published skill '{skillId}' not found.");
        }

        var targetVersion = request.Version ?? skill.ActiveVersion;
        var verEntity = await _db.PublishedSkillVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.SkillId == skillId && v.Version == targetVersion, ct)
            .ConfigureAwait(false);
        if (verEntity is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Version {targetVersion} not found for skill '{skillId}'.");
        }

        if (verEntity.NeedsRevalidation)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                $"Skill '{skillId}' v{targetVersion} requires revalidation after plugin/catalog change before execution.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == skill.PluginId, ct).ConfigureAwait(false);
        var conn = plugin is null ? null : await _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (plugin is null || conn is null || !plugin.Enabled)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable,
                $"Plugin '{skill.PluginId}' is disabled or missing.");
        }

        var snap = await _db.CatalogSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == verEntity.CatalogSnapshotId && s.PluginId == plugin.Id, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Bound catalog snapshot not found.");
        }

        // 第 10 节：运行前检查目录 15 分钟新鲜度；超期对已授权同目标执行 1 次刷新核对，发现破坏性变更或刷新失败即阻断启动
        if (snap.FetchedAtUtc < DateTime.UtcNow.AddMinutes(-BusinessSkillDefaults.CatalogFreshnessMinutes))
        {
            var refreshRes = await _pluginControl.DiscoverAsync(
                plugin.Id,
                new DiscoverPluginRequest($"freshness-{Guid.NewGuid():N}"),
                ct).ConfigureAwait(false);

            if (!refreshRes.Success || refreshRes.Value is null)
            {
                return BusinessServiceResult<SkillRunDto>.Fail(
                    refreshRes.StatusCode >= 400 ? refreshRes.StatusCode : 503,
                    refreshRes.Error?.Code ?? BusinessSkillErrorCodes.ConnectionUnavailable,
                    refreshRes.Error?.Message ?? "Failed verifying target catalog freshness before starting run.");
            }

            if (refreshRes.Value.Diff is { TriggersNeedsRevalidation: true })
            {
                return BusinessServiceResult<SkillRunDto>.Fail(
                    409,
                    BusinessSkillErrorCodes.CatalogChanged,
                    "Target catalog changed during pre-run freshness verification; skill requires revalidation before running.");
            }
        }

        var def = JsonSerializer.Deserialize<SkillDefinitionV1>(verEntity.DefinitionJson, JsonOptions);
        if (def is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Published skill definition JSON is invalid.");
        }

        var capMap = await LoadCapabilityMapAsync(verEntity.CatalogSnapshotId, ct).ConfigureAwait(false);
        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            def, plugin.Id, verEntity.CatalogSnapshotId, conn.Environment, capMap);
        if (!validation.IsValid)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, validation.Errors[0].Code, validation.Errors[0].Message, fieldErrors: validation.Errors);
        }

        if (!SkillValueBinder.TryValidateRunInputs(def.Inputs, request.Inputs ?? new Dictionary<string, JsonElement>(), out var resolvedInputs, out var inputErrors))
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed,
                "Run inputs failed schema validation.", fieldErrors: inputErrors);
        }

        var credTuple = await ResolveExecutionCredentialAsync(conn, ct).ConfigureAwait(false);
        if (credTuple.MissingProtectedStore)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(422, BusinessSkillErrorCodes.AuthRequired,
                "Execution credential cannot be decrypted because persistent OS protection is unavailable.");
        }

        return await CreateAndLaunchRunAsync(new PendingLaunchSpec(
            RunId: $"srun-{Guid.NewGuid():N}"[..18],
            DbSkillId: skill.Id,
            SkillVersion: verEntity.Version,
            ClientRequestId: clientReqId,
            Definition: def,
            Plugin: plugin,
            Connection: conn,
            Snapshot: snap,
            Capabilities: capMap,
            ResolvedInputs: resolvedInputs,
            AuthKind: credTuple.AuthKind,
            SecretJson: credTuple.SecretJson,
            IsTestRun: request.IsTestRun,
            AllowWriteInTestRun: true,
            WriteStepUnverified: !verEntity.WriteStepVerified,
            SessionId: request.SessionId,
            DraftIdForEvidence: null,
            DraftRevisionForEvidence: 0,
            StaticValidationHashForEvidence: null), ct).ConfigureAwait(false);
    }

    public async Task<BusinessServiceResult<SkillRunDto>> GetRunAsync(string runId, CancellationToken ct = default)
    {
        var run = await _db.SkillRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Run '{runId}' not found.");
        }

        var steps = await _db.SkillRunSteps.AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.Sequence)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return BusinessServiceResult<SkillRunDto>.Ok(MapRunDto(run, steps, snapId: string.Empty, isTestRun: run.SkillId.StartsWith("draft:", StringComparison.Ordinal)));
    }

    public async Task<BusinessServiceResult<IReadOnlyList<SkillRunDto>>> ListRunsForSkillAsync(string skillId, CancellationToken ct = default)
    {
        var runs = await _db.SkillRuns.AsNoTracking()
            .Where(r => r.SkillId == skillId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var runIds = runs.Select(static r => r.RunId).ToArray();
        var steps = runIds.Length == 0
            ? []
            : await _db.SkillRunSteps.AsNoTracking().Where(s => runIds.Contains(s.RunId)).ToListAsync(ct).ConfigureAwait(false);
        var byRun = steps.GroupBy(static s => s.RunId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.OrderBy(x => x.Sequence).ToArray(), StringComparer.Ordinal);

        var dtos = runs.Select(r => MapRunDto(
            r,
            byRun.TryGetValue(r.RunId, out var list) ? list : [],
            snapId: string.Empty,
            isTestRun: r.SkillId.StartsWith("draft:", StringComparison.Ordinal))).ToArray();

        return BusinessServiceResult<IReadOnlyList<SkillRunDto>>.Ok(dtos);
    }

    public async Task<BusinessServiceResult<SkillRunDto>> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        var run = await _db.SkillRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return BusinessServiceResult<SkillRunDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Run '{runId}' not found.");
        }

        run.StopRequested = true;
        run.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _approvalCoordinator.RequestRunStop(runId, "Run cancellation requested by control-plane operator.");

        var steps = await _db.SkillRunSteps.AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.Sequence)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return BusinessServiceResult<SkillRunDto>.Ok(MapRunDto(run, steps, snapId: string.Empty, isTestRun: run.SkillId.StartsWith("draft:", StringComparison.Ordinal)));
    }

    public async Task ReconcileOrphanedRunsOnStartupAsync(CancellationToken ct = default)
    {
        var activeStatuses = new[]
        {
            nameof(SkillRunStatus.Queued),
            nameof(SkillRunStatus.Running),
            nameof(SkillRunStatus.WaitingForApproval)
        };

        var orphans = await _db.SkillRuns
            .Where(r => activeStatuses.Contains(r.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var run in orphans)
        {
            var steps = await _db.SkillRunSteps
                .Where(s => s.RunId == run.RunId)
                .OrderBy(s => s.Sequence)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var hasDispatchingStep = steps.Any(static s => string.Equals(s.State, nameof(SkillRunStepState.Dispatching), StringComparison.Ordinal));
            if (hasDispatchingStep)
            {
                foreach (var ds in steps.Where(static s => string.Equals(s.State, nameof(SkillRunStepState.Dispatching), StringComparison.Ordinal)))
                {
                    ds.State = nameof(SkillRunStepState.OutcomeUnknown);
                    ds.ErrorCode = BusinessSkillErrorCodes.OutcomeUnknown;
                    ds.ResponseSummary = "Process terminated while non-read request was Dispatching; external side effect is unknown.";
                    ds.CompletedAtUtc = now;
                }

                run.Status = nameof(SkillRunStatus.OutcomeUnknown);
                run.PendingApprovalJson = null;
                run.ErrorJson = JsonSerializer.Serialize(new BusinessSkillErrorDto(
                    BusinessSkillErrorCodes.OutcomeUnknown,
                    "Host restarted while a dispatched write request was in flight; please reconcile target state manually before retrying.",
                    [],
                    false,
                    run.RunId), JsonOptions);
            }
            else if (string.Equals(run.Status, nameof(SkillRunStatus.WaitingForApproval), StringComparison.Ordinal))
            {
                run.Status = nameof(SkillRunStatus.Failed);
                run.PendingApprovalJson = null;
                run.ErrorJson = JsonSerializer.Serialize(new BusinessSkillErrorDto(
                    BusinessSkillErrorCodes.ApprovalExpired,
                    "Host restarted while waiting for write approval; pending approval was invalidated.",
                    [],
                    false,
                    run.RunId), JsonOptions);
            }
            else
            {
                run.Status = nameof(SkillRunStatus.Failed);
                run.PendingApprovalJson = null;
                run.ErrorJson = JsonSerializer.Serialize(new BusinessSkillErrorDto(
                    BusinessSkillErrorCodes.ConnectionUnavailable,
                    "Host restarted before run completed.",
                    [],
                    true,
                    run.RunId), JsonOptions);
            }

            run.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<BusinessServiceResult<SkillRunDto>> CreateAndLaunchRunAsync(PendingLaunchSpec spec, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var runEntity = new SkillRunEntity
        {
            RunId = spec.RunId,
            SkillId = spec.DbSkillId,
            SkillVersion = spec.SkillVersion,
            ClientRequestId = spec.ClientRequestId,
            Status = nameof(SkillRunStatus.Queued),
            StopRequested = false,
            ExternalRequestCount = 0,
            ReadOnlyRetryCount = 0,
            InputsJson = JsonSerializer.Serialize(spec.ResolvedInputs, JsonOptions),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.SkillRuns.Add(runEntity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _approvalCoordinator.RegisterActiveRun(spec.RunId, spec.Plugin.Id);
        _ = Task.Run(() => ExecuteRunInBackgroundAsync(spec), CancellationToken.None);

        var initialDto = MapRunDto(runEntity, [], spec.Snapshot.Id, spec.IsTestRun);
        return BusinessServiceResult<SkillRunDto>.Ok(initialDto, 202);
    }

    private async Task ExecuteRunInBackgroundAsync(PendingLaunchSpec spec)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IJobDbContext>();
            var engine = scope.ServiceProvider.GetRequiredService<SkillWorkflowEngine>();
            var gateway = scope.ServiceProvider.GetService<ICodexGateway>();

            var runRow = await db.SkillRuns.FirstOrDefaultAsync(r => r.RunId == spec.RunId, CancellationToken.None).ConfigureAwait(false);
            if (runRow is null)
            {
                return;
            }

            runRow.Status = nameof(SkillRunStatus.Running);
            runRow.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var runtimeHost = new SqliteSkillRunRuntimeHost(spec.RunId, spec.Plugin.Id, _scopeFactory, _approvalCoordinator);
            var execContext = new SkillRunExecutionContext
            {
                RunId = spec.RunId,
                Definition = spec.Definition,
                PluginId = spec.Plugin.Id,
                PluginName = spec.Plugin.Name,
                SourceUrl = spec.Connection.SourceUrl,
                Environment = spec.Connection.Environment,
                ConnectionId = spec.Connection.Id,
                ConnectionRevision = spec.Connection.Revision,
                CredentialRevision = spec.Connection.CredentialRevision,
                CatalogSnapshotHash = spec.Snapshot.SchemaHash,
                EndpointPolicy = DeserializePolicy(spec.Connection.EndpointPolicyJson, spec.Connection.SourceUrl),
                ExecutionAuthKind = spec.AuthKind,
                ExecutionSecretJson = spec.SecretJson,
                SnapshotCapabilities = spec.Capabilities,
                Inputs = spec.ResolvedInputs,
                IsTestRun = spec.IsTestRun,
                AllowWriteInTestRun = spec.AllowWriteInTestRun,
                WriteStepUnverified = spec.WriteStepUnverified,
                SessionId = spec.SessionId
            };

            var outcome = await engine.ExecuteAsync(execContext, runtimeHost, CancellationToken.None).ConfigureAwait(false);

            runRow.Status = outcome.FinalStatus.ToString();
            runRow.ExternalRequestCount = outcome.ExternalRequestsConsumed;
            runRow.ReadOnlyRetryCount = outcome.ReadOnlyRetriesConsumed;
            runRow.PendingApprovalJson = null;
            runRow.OutputPayloadJson = outcome.OutputPayload.HasValue ? JsonSerializer.Serialize(outcome.OutputPayload.Value, JsonOptions) : null;
            runRow.ErrorJson = outcome.Error is not null ? JsonSerializer.Serialize(outcome.Error, JsonOptions) : null;
            runRow.UpdatedAtUtc = DateTime.UtcNow;

            if (spec.DraftIdForEvidence is not null && outcome.FinalStatus == SkillRunStatus.Succeeded)
            {
                var draft = await db.SkillDrafts.FirstOrDefaultAsync(d => d.Id == spec.DraftIdForEvidence, CancellationToken.None).ConfigureAwait(false);
                if (draft is not null &&
                    draft.Revision == spec.DraftRevisionForEvidence &&
                    !string.IsNullOrWhiteSpace(draft.CandidateDefinitionJson))
                {
                    var cand = JsonSerializer.Deserialize<SkillDefinitionV1>(draft.CandidateDefinitionJson, JsonOptions);
                    if (cand is not null)
                    {
                        var evHash = BusinessSkillBuilderService.ComputeTestRunEvidenceHash(
                            draft.Revision,
                            spec.Snapshot.SchemaHash,
                            spec.StaticValidationHashForEvidence!);

                        var updatedCand = cand with
                        {
                            ValidationEvidence = cand.ValidationEvidence with
                            {
                                DraftRevision = draft.Revision,
                                CatalogHash = spec.Snapshot.SchemaHash,
                                StaticValidationHash = spec.StaticValidationHashForEvidence!,
                                ReadOnlyTestRunId = spec.RunId,
                                ReadOnlyTestRunEvidenceHash = evHash,
                                WriteStepVerified = cand.ValidationEvidence.WriteStepVerified || (spec.AllowWriteInTestRun && outcome.AnyWriteSucceeded),
                                VerifiedAtUtc = DateTimeOffset.UtcNow
                            }
                        };
                        draft.CandidateDefinitionJson = JsonSerializer.Serialize(updatedCand, JsonOptions);
                        draft.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
            }

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            if (gateway is not null && !string.IsNullOrWhiteSpace(spec.SessionId))
            {
                await gateway.PublishEventAsync(
                    spec.SessionId,
                    new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.BusinessSkillRunUpdated,
                        Payload = new
                        {
                            runId = runRow.RunId,
                            skillId = runRow.SkillId,
                            skillVersion = runRow.SkillVersion,
                            status = runRow.Status,
                            stopRequested = runRow.StopRequested,
                            updatedAtUtc = new DateTimeOffset(runRow.UpdatedAtUtc, TimeSpan.Zero).ToString("O")
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while executing business skill run {RunId}", spec.RunId);
            try
            {
                await using var errScope = _scopeFactory.CreateAsyncScope();
                var db = errScope.ServiceProvider.GetRequiredService<IJobDbContext>();
                var runRow = await db.SkillRuns.FirstOrDefaultAsync(r => r.RunId == spec.RunId, CancellationToken.None).ConfigureAwait(false);
                if (runRow is not null)
                {
                    runRow.Status = nameof(SkillRunStatus.Failed);
                    runRow.PendingApprovalJson = null;
                    runRow.ErrorJson = JsonSerializer.Serialize(
                        new BusinessSkillErrorDto(BusinessSkillErrorCodes.ConnectionUnavailable, ex.Message, [], false, spec.RunId),
                        JsonOptions);
                    runRow.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogDebug(innerEx, "Failed persisting failure state for run {RunId}", spec.RunId);
            }
        }
        finally
        {
            _approvalCoordinator.UnregisterActiveRun(spec.RunId);
        }
    }

    private async Task<SkillRunDto?> TryFindExistingIdempotentRunAsync(string skillId, string clientRequestId, CancellationToken ct)
    {
        var run = await _db.SkillRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SkillId == skillId && r.ClientRequestId == clientRequestId, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var steps = await _db.SkillRunSteps.AsNoTracking()
            .Where(s => s.RunId == run.RunId)
            .OrderBy(s => s.Sequence)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        return MapRunDto(run, steps, snapId: string.Empty, isTestRun: skillId.StartsWith("draft:", StringComparison.Ordinal));
    }

    private async Task<Dictionary<string, CapabilityDetailDto>> LoadCapabilityMapAsync(string snapshotId, CancellationToken ct)
    {
        var rows = await _db.CatalogCapabilities.AsNoTracking()
            .Where(c => c.SnapshotId == snapshotId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(static c => new CapabilityDetailDto
        {
            Summary = new CapabilitySummaryDto
            {
                Id = c.Id,
                SnapshotId = c.SnapshotId,
                NativeIdentity = c.NativeIdentity,
                ModelAlias = c.ModelAlias,
                Title = c.Title,
                Description = c.Description,
                SourceDescriptionMissing = c.SourceDescriptionMissing,
                GroupTag = c.GroupTag,
                Risk = Enum.TryParse<CapabilityRiskLevel>(c.Risk, out var r) ? r : CapabilityRiskLevel.Unknown,
                SuggestedSourceRiskHint = !string.IsNullOrEmpty(c.SuggestedSourceRiskHint) && Enum.TryParse<CapabilityRiskLevel>(c.SuggestedSourceRiskHint, out var sr) ? sr : null,
                Executability = Enum.TryParse<CapabilityExecutabilityStatus>(c.Executability, out var ex) ? ex : CapabilityExecutabilityStatus.Unsupported,
                Diagnostics = JsonSerializer.Deserialize<string[]>(c.DiagnosticsJson, JsonOptions) ?? []
            },
            InputSchemaJson = c.InputSchemaJson,
            OutputSchemaJson = c.OutputSchemaJson,
            Invocation = JsonSerializer.Deserialize<CapabilityInvocationSpecDto>(c.InvocationJson, JsonOptions) ?? new CapabilityInvocationSpecDto()
        }).ToDictionary(static d => d.Summary.Id, StringComparer.Ordinal);
    }

    private async Task<(BusinessAuthKind AuthKind, string? SecretJson, bool MissingProtectedStore)> ResolveExecutionCredentialAsync(
        PluginConnectionEntity connection,
        CancellationToken ct)
    {
        var targetRef = !string.IsNullOrEmpty(connection.ExecutionCredentialRef)
            ? connection.ExecutionCredentialRef
            : connection.DocumentCredentialRef;

        if (string.IsNullOrEmpty(targetRef))
        {
            return (BusinessAuthKind.None, null, false);
        }

        var meta = await _db.BusinessCredentialMetas.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CredentialRef == targetRef, ct)
            .ConfigureAwait(false);
        if (meta is null || !Enum.TryParse<BusinessAuthKind>(meta.AuthKind, out var authKind) || authKind == BusinessAuthKind.None)
        {
            return (BusinessAuthKind.None, null, false);
        }

        if (!_credentialStore.IsPersistentProtectionAvailable)
        {
            return (authKind, null, true);
        }

        var secretJson = await _credentialStore.ReadSecretAsync(meta.CredentialRef, meta.CipherFileName, ct).ConfigureAwait(false);
        return (authKind, secretJson, false);
    }

    private static EndpointPolicyDto DeserializePolicy(string json, string fallbackSourceUrl)
    {
        var parsed = JsonSerializer.Deserialize<EndpointPolicyDto>(json, JsonOptions);
        if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.AllowedOrigin))
        {
            return parsed;
        }

        return new EndpointPolicyDto
        {
            AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(new Uri(fallbackSourceUrl, UriKind.Absolute))
        };
    }

    private static SkillRunDto MapRunDto(
        SkillRunEntity r,
        IReadOnlyList<SkillRunStepEntity> steps,
        string snapId,
        bool isTestRun)
    {
        JsonElement? outEl = null;
        if (!string.IsNullOrWhiteSpace(r.OutputPayloadJson))
        {
            using var doc = JsonDocument.Parse(r.OutputPayloadJson);
            outEl = doc.RootElement.Clone();
        }

        return new SkillRunDto
        {
            RunId = r.RunId,
            SkillId = r.SkillId,
            SkillVersion = r.SkillVersion,
            CatalogSnapshotId = snapId,
            IsTestRun = isTestRun,
            ClientRequestId = r.ClientRequestId,
            Status = Enum.TryParse<SkillRunStatus>(r.Status, out var st) ? st : SkillRunStatus.Failed,
            StopRequested = r.StopRequested,
            ExternalRequestCount = r.ExternalRequestCount,
            ReadOnlyRetryCount = r.ReadOnlyRetryCount,
            Steps = steps.Select(static s => new SkillRunStepDto
            {
                StepId = s.StepId,
                StepType = s.StepType,
                State = Enum.TryParse<SkillRunStepState>(s.State, out var ss) ? ss : SkillRunStepState.Failed,
                RequestSummary = s.RequestSummary,
                ResponseSummary = s.ResponseSummary,
                Evidence = s.Evidence,
                ErrorCode = s.ErrorCode,
                StartedAtUtc = new DateTimeOffset(s.StartedAtUtc, TimeSpan.Zero),
                CompletedAtUtc = s.CompletedAtUtc.HasValue ? new DateTimeOffset(s.CompletedAtUtc.Value, TimeSpan.Zero) : null
            }).ToArray(),
            PendingApproval = string.IsNullOrWhiteSpace(r.PendingApprovalJson)
                ? null
                : JsonSerializer.Deserialize<BusinessApprovalRequestDto>(r.PendingApprovalJson, JsonOptions),
            OutputPayload = outEl,
            Error = string.IsNullOrWhiteSpace(r.ErrorJson)
                ? null
                : JsonSerializer.Deserialize<BusinessSkillErrorDto>(r.ErrorJson, JsonOptions),
            CreatedAtUtc = new DateTimeOffset(r.CreatedAtUtc, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(r.UpdatedAtUtc, TimeSpan.Zero)
        };
    }

    private sealed class SqliteSkillRunRuntimeHost(
        string runId,
        string pluginId,
        IServiceScopeFactory scopeFactory,
        IBusinessSkillApprovalCoordinator approvalCoordinator) : ISkillRunRuntimeHost
    {
        private readonly Dictionary<string, int> _stepSequences = new(StringComparer.Ordinal);
        private int _nextSeq = 1;

        public async Task PersistStepCheckpointAsync(
            string stepId,
            string stepType,
            SkillRunStepState state,
            string? requestSummary,
            string? responseSummary,
            string? evidence,
            string? errorCode,
            CancellationToken ct)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IJobDbContext>();

            var isTerminal = state is SkillRunStepState.Succeeded or SkillRunStepState.Failed or SkillRunStepState.Cancelled or SkillRunStepState.OutcomeUnknown;
            var now = DateTime.UtcNow;

            if (!_stepSequences.TryGetValue(stepId, out var seq))
            {
                seq = _nextSeq++;
                _stepSequences[stepId] = seq;
                db.SkillRunSteps.Add(new SkillRunStepEntity
                {
                    RunId = runId,
                    Sequence = seq,
                    StepId = stepId,
                    StepType = stepType,
                    State = state.ToString(),
                    RequestSummary = requestSummary,
                    ResponseSummary = responseSummary,
                    Evidence = evidence,
                    ErrorCode = errorCode,
                    StartedAtUtc = now,
                    CompletedAtUtc = isTerminal ? now : null
                });
            }
            else
            {
                var row = await db.SkillRunSteps.FirstOrDefaultAsync(s => s.RunId == runId && s.Sequence == seq, ct).ConfigureAwait(false);
                if (row is not null)
                {
                    row.State = state.ToString();
                    if (requestSummary is not null)
                    {
                        row.RequestSummary = requestSummary;
                    }

                    if (responseSummary is not null)
                    {
                        row.ResponseSummary = responseSummary;
                    }

                    if (evidence is not null)
                    {
                        row.Evidence = evidence;
                    }

                    if (errorCode is not null)
                    {
                        row.ErrorCode = errorCode;
                    }

                    if (isTerminal)
                    {
                        row.CompletedAtUtc = now;
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task UpdateRunProgressAsync(int externalRequestsConsumed, int readOnlyRetriesConsumed, CancellationToken ct)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IJobDbContext>();
            var row = await db.SkillRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            row.ExternalRequestCount = externalRequestsConsumed;
            row.ReadOnlyRetryCount = readOnlyRetriesConsumed;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task<bool> IsStopOrInvalidationRequestedAsync(CancellationToken ct)
        {
            if (approvalCoordinator.IsStopOrInvalidationRequested(runId))
            {
                return true;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IJobDbContext>();
            var row = await db.SkillRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
            return row?.StopRequested == true;
        }

        public Task<SkillApprovalGrantOutcome> RequestAndAwaitApprovalAsync(
            BusinessApprovalRequestDto approvalRequest,
            BusinessApprovalDigestInput digestInput,
            CancellationToken ct)
            => approvalCoordinator.AwaitApprovalAsync(pluginId, approvalRequest, digestInput, ct);
    }

    private sealed record PendingLaunchSpec(
        string RunId,
        string DbSkillId,
        int SkillVersion,
        string ClientRequestId,
        SkillDefinitionV1 Definition,
        BusinessPluginEntity Plugin,
        PluginConnectionEntity Connection,
        CatalogSnapshotEntity Snapshot,
        IReadOnlyDictionary<string, CapabilityDetailDto> Capabilities,
        IReadOnlyDictionary<string, JsonElement> ResolvedInputs,
        BusinessAuthKind AuthKind,
        string? SecretJson,
        bool IsTestRun,
        bool AllowWriteInTestRun,
        bool WriteStepUnverified,
        string? SessionId,
        string? DraftIdForEvidence,
        int DraftRevisionForEvidence,
        string? StaticValidationHashForEvidence);
}
