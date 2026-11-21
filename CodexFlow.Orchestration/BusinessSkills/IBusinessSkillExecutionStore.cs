// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;

namespace CodexFlow.Orchestration.BusinessSkills;

public interface IBusinessSkillExecutionStore
{
    Task<BusinessPluginEntity?> FindPluginAsync(string id, CancellationToken ct);
    Task<PluginConnectionEntity?> FindConnectionAsync(string id, string? pluginId, CancellationToken ct);
    Task<CatalogSnapshotEntity?> FindSnapshotAsync(string id, string? pluginId, CancellationToken ct);
    Task<SkillDraftEntity?> FindDraftAsync(string id, CancellationToken ct);
    Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct);
    Task<PublishedSkillEntity?> FindPublishedSkillAsync(string id, CancellationToken ct);
    Task<PublishedSkillVersionEntity?> FindPublishedVersionAsync(string skillId, int version, CancellationToken ct);
    Task<IReadOnlyList<PublishedSkillEntity>> ListEnabledPublishedSkillsAsync(CancellationToken ct);
    Task<IReadOnlyList<PublishedSkillVersionEntity>> ListPublishedVersionsAsync(string[] skillIds, CancellationToken ct);
    Task<IReadOnlyList<CatalogCapabilityEntity>> ListCapabilitiesAsync(string snapshotId, CancellationToken ct);
    Task<IReadOnlyList<BusinessCapabilityRisk>> ListCapabilityRisksAsync(string snapshotId, string[] capabilityIds, CancellationToken ct);

    Task<SkillRunEntity?> FindRunAsync(string id, CancellationToken ct);
    Task<SkillRunEntity?> FindIdempotentRunAsync(string skillId, string clientRequestId, CancellationToken ct);
    Task<IReadOnlyList<SkillRunEntity>> ListRecentRunsAsync(string skillId, CancellationToken ct);
    Task<IReadOnlyList<SkillRunEntity>> ListActiveRunsAsync(CancellationToken ct);
    Task<IReadOnlyList<SkillRunEntity>> ListWaitingRunsAsync(CancellationToken ct);
    Task<SkillRunStepEntity[]> ListRunStepsAsync(string[] runIds, CancellationToken ct);

    Task<SkillRunEntity> CreateRunAsync(SkillRunEntity proposed, CancellationToken ct);
    Task<SkillRunEntity?> RequestStopAsync(string runId, DateTime now, CancellationToken ct);
    Task<bool> StartQueuedRunAsync(string runId, DateTime now, CancellationToken ct);
    Task SetPendingApprovalAsync(string runId, SkillRunStatus status, string json, DateTime now, CancellationToken ct);
    Task ClearPendingApprovalAsync(string runId, DateTime now, CancellationToken ct);
    Task<SkillRunEntity?> FinishRunAsync(string runId, SkillRunFinalState state, SkillDraftRunEvidence? evidence, CancellationToken ct);
    Task RecoverRunsAsync(IReadOnlyList<SkillRunRecovery> recoveries, CancellationToken ct);
    Task PersistCheckpointAsync(SkillRunStepEntity checkpoint, bool insert, CancellationToken ct);
    Task UpdateProgressAsync(string runId, int externalRequests, int readOnlyRetries, DateTime now, CancellationToken ct);
}

public sealed record BusinessCapabilityRisk(string Id, string Risk);

public sealed record SkillRunFinalState(
    SkillRunStatus Status, int? ExternalRequests, int? ReadOnlyRetries,
    string? OutputJson, string? ErrorJson, DateTime Now, bool UpdateOutput = true);

public sealed record SkillDraftRunEvidence(
    string DraftId, int Revision, string ExpectedDefinitionJson, string NewDefinitionJson, DateTime Now);

public sealed record SkillRunRecovery(
    string RunId, string ExpectedStatus, SkillRunFinalState FinalState, IReadOnlyList<SkillRunStepEntity> ChangedSteps);
