// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.BusinessSkills;

namespace CodexFlow.Orchestration.BusinessSkills;

public interface IBusinessSkillBuilderStore
{
    Task CreateDraftAsync(SkillDraftEntity draft, CancellationToken ct);
    Task<bool> TryUpdateDraftAsync(SkillDraftRevision expected, SkillDraftEntity draft, CancellationToken ct);
    Task<PluginOperationEntity?> FindGenerationAsync(string draftId, string clientRequestId, CancellationToken ct);
    Task<PluginOperationEntity> ReserveGenerationAsync(PluginOperationEntity proposed, CancellationToken ct);
    Task<bool> CompleteGenerationAsync(SkillDraftRevision expected, SkillDraftEntity draft, PluginOperationEntity operation, CancellationToken ct);
    Task FailGenerationAsync(PluginOperationEntity operation, CancellationToken ct);
    Task<bool> PublishAsync(BusinessSkillPublication publication, CancellationToken ct);
    Task<bool> ActivateVersionAsync(string skillId, int version, BusinessSkillPluginBinding binding, DateTime now, CancellationToken ct);
    Task<IReadOnlyList<PublishedSkillEntity>> ListPublishedSkillsAsync(CancellationToken ct);
    Task<IReadOnlyList<CatalogCapabilityEntity>> ListSelectedCapabilitiesAsync(string snapshotId, string[] capabilityIds, CancellationToken ct);
}

public sealed record SkillDraftRevision(string Id, int Revision, DateTime UpdatedAtUtc)
{
    public static SkillDraftRevision Capture(SkillDraftEntity draft)
        => new(draft.Id, draft.Revision, draft.UpdatedAtUtc);
}

public sealed record BusinessSkillPluginBinding(string PluginId, string ConnectionId, int ConnectionRevision, string CredentialRevision)
{
    public static BusinessSkillPluginBinding Capture(BusinessPluginEntity plugin, PluginConnectionEntity connection)
        => new(plugin.Id, connection.Id, connection.Revision, connection.CredentialRevision);
}

public sealed record BusinessSkillPublication(
    SkillDraftRevision ExpectedDraft, BusinessSkillPluginBinding Plugin, string CatalogSchemaHash,
    string SucceededTestRunId, PublishedSkillEntity Skill, PublishedSkillVersionEntity Version);
