// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Data;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed partial class EfBusinessSkillExecutionStore : IBusinessSkillBuilderStore
{
    public async Task CreateDraftAsync(SkillDraftEntity draft, CancellationToken ct)
    {
        _db.SkillDrafts.Add(draft);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateDraftAsync(SkillDraftRevision expected, SkillDraftEntity draft, CancellationToken ct)
        => await MatchDraft(expected).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.CatalogSnapshotId, draft.CatalogSnapshotId)
            .SetProperty(row => row.Revision, draft.Revision)
            .SetProperty(row => row.Name, draft.Name)
            .SetProperty(row => row.BusinessGoal, draft.BusinessGoal)
            .SetProperty(row => row.SelectedCapabilityIdsJson, draft.SelectedCapabilityIdsJson)
            .SetProperty(row => row.CandidateDefinitionJson, draft.CandidateDefinitionJson)
            .SetProperty(row => row.ClarificationQuestionsJson, draft.ClarificationQuestionsJson)
            .SetProperty(row => row.ValidationErrorsJson, draft.ValidationErrorsJson)
            .SetProperty(row => row.NeedsRevalidation, draft.NeedsRevalidation)
            .SetProperty(row => row.UpdatedAtUtc, draft.UpdatedAtUtc), ct).ConfigureAwait(false) != 0;

    public Task<PluginOperationEntity?> FindGenerationAsync(string draftId, string clientRequestId, CancellationToken ct)
        => _db.PluginOperations.AsNoTracking().FirstOrDefaultAsync(row =>
            row.Kind == "generate" && row.TargetId == draftId && row.ClientRequestId == clientRequestId, ct);

    public async Task<PluginOperationEntity> ReserveGenerationAsync(PluginOperationEntity proposed, CancellationToken ct)
    {
        _db.PluginOperations.Add(proposed);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return proposed;
        }
        catch (DbUpdateException)
        {
            _db.Entry(proposed).State = EntityState.Detached;
            var existing = await FindGenerationAsync(proposed.TargetId, proposed.ClientRequestId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    public Task<bool> CompleteGenerationAsync(
        SkillDraftRevision expected, SkillDraftEntity draft, PluginOperationEntity operation, CancellationToken ct)
        => ExecuteBuilderTransactionAsync(async () =>
            await TryUpdateDraftAsync(expected, draft, ct).ConfigureAwait(false) &&
            await UpdateGenerationAsync(operation, ct).ConfigureAwait(false) != 0, ct);

    public Task FailGenerationAsync(PluginOperationEntity operation, CancellationToken ct)
        => UpdateGenerationAsync(operation, ct);

    public Task<bool> PublishAsync(BusinessSkillPublication publication, CancellationToken ct)
        => ExecuteBuilderTransactionAsync(async () =>
        {
            var binding = publication.Plugin;
            var skill = publication.Skill;
            var version = publication.Version;
            if (!await MatchDraft(publication.ExpectedDraft).AnyAsync(row => !row.NeedsRevalidation &&
                    row.PluginId == binding.PluginId && row.CatalogSnapshotId == version.CatalogSnapshotId, ct).ConfigureAwait(false) ||
                !await MatchPlugin(binding).AnyAsync(ct).ConfigureAwait(false) ||
                !await _db.CatalogSnapshots.AnyAsync(row => row.Id == version.CatalogSnapshotId && row.PluginId == binding.PluginId &&
                    row.SchemaHash == publication.CatalogSchemaHash, ct).ConfigureAwait(false) ||
                !await _db.SkillRuns.AnyAsync(row => row.RunId == publication.SucceededTestRunId &&
                    row.Status == nameof(SkillRunStatus.Succeeded), ct).ConfigureAwait(false))
            {
                return false;
            }

            var latestVersion = await _db.PublishedSkillVersions.Where(row => row.SkillId == skill.Id)
                .MaxAsync(row => (int?)row.Version, ct).ConfigureAwait(false);
            if ((latestVersion ?? 0) + 1 != version.Version)
            {
                return false;
            }

            var existing = await FindPublishedSkillAsync(skill.Id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                _db.PublishedSkills.Add(skill);
            }
            else if (await _db.PublishedSkills.Where(row => row.Id == skill.Id && row.PluginId == binding.PluginId)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, skill.Name)
                        .SetProperty(row => row.Description, skill.Description)
                        .SetProperty(row => row.Environment, skill.Environment)
                        .SetProperty(row => row.ActiveVersion, skill.ActiveVersion)
                        .SetProperty(row => row.UpdatedAtUtc, skill.UpdatedAtUtc), ct).ConfigureAwait(false) == 0)
            {
                return false;
            }

            _db.PublishedSkillVersions.Add(version);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    public async Task<bool> ActivateVersionAsync(
        string skillId, int version, BusinessSkillPluginBinding binding, DateTime now, CancellationToken ct)
    {
        var plugin = MatchPlugin(binding);
        return await _db.PublishedSkills.Where(row => row.Id == skillId && row.PluginId == binding.PluginId &&
                plugin.Any() && _db.PublishedSkillVersions.Any(candidate =>
                    candidate.SkillId == skillId && candidate.Version == version && !candidate.NeedsRevalidation))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ActiveVersion, version)
                .SetProperty(row => row.UpdatedAtUtc, now), ct).ConfigureAwait(false) != 0;
    }

    public async Task<IReadOnlyList<PublishedSkillEntity>> ListPublishedSkillsAsync(CancellationToken ct)
        => await _db.PublishedSkills.AsNoTracking().OrderBy(row => row.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<CatalogCapabilityEntity>> ListSelectedCapabilitiesAsync(
        string snapshotId, string[] capabilityIds, CancellationToken ct)
        => await _db.CatalogCapabilities.AsNoTracking().Where(row =>
            row.SnapshotId == snapshotId && capabilityIds.Contains(row.Id)).ToListAsync(ct).ConfigureAwait(false);

    private IQueryable<SkillDraftEntity> MatchDraft(SkillDraftRevision expected)
        => _db.SkillDrafts.Where(row => row.Id == expected.Id && row.Revision == expected.Revision &&
            row.UpdatedAtUtc == expected.UpdatedAtUtc);

    private IQueryable<BusinessPluginEntity> MatchPlugin(BusinessSkillPluginBinding binding)
        => _db.BusinessPlugins.Where(row => row.Id == binding.PluginId && row.Enabled &&
            row.ConnectionId == binding.ConnectionId && _db.PluginConnections.Any(connection =>
                connection.Id == binding.ConnectionId && connection.Revision == binding.ConnectionRevision &&
                connection.CredentialRevision == binding.CredentialRevision));

    private Task<int> UpdateGenerationAsync(PluginOperationEntity operation, CancellationToken ct)
        => _db.PluginOperations.Where(row => row.OperationId == operation.OperationId && row.Kind == "generate" &&
            row.Status == nameof(PluginOperationStatus.Running) && row.Sequence == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, operation.Status)
                .SetProperty(row => row.Sequence, operation.Sequence)
                .SetProperty(row => row.ResultMessage, operation.ResultMessage)
                .SetProperty(row => row.ErrorJson, operation.ErrorJson)
                .SetProperty(row => row.UpdatedAtUtc, operation.UpdatedAtUtc), ct);

    private async Task<bool> ExecuteBuilderTransactionAsync(Func<Task<bool>> operation, CancellationToken ct)
    {
        var active = _db.Database.CurrentTransaction;
        if (active is null)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var succeeded = await operation().ConfigureAwait(false);
            if (succeeded)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            return succeeded;
        }

        var savepoint = "skill_builder_" + Guid.NewGuid().ToString("N");
        await active.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        var completed = false;
        try
        {
            completed = await operation().ConfigureAwait(false);
            return completed;
        }
        finally
        {
            if (!completed)
            {
                await active.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            }

            await active.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
