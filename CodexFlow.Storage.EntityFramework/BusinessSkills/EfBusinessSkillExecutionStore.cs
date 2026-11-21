// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed partial class EfBusinessSkillExecutionStore(IJobDbContext db) : IBusinessSkillExecutionStore
{
    private readonly IJobDbContext _db = db;

    public Task<BusinessPluginEntity?> FindPluginAsync(string id, CancellationToken ct)
        => _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, ct);

    public Task<PluginConnectionEntity?> FindConnectionAsync(string id, string? pluginId, CancellationToken ct)
        => _db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == id && (pluginId == null || row.PluginId == pluginId), ct);

    public Task<CatalogSnapshotEntity?> FindSnapshotAsync(string id, string? pluginId, CancellationToken ct)
        => _db.CatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == id && (pluginId == null || row.PluginId == pluginId), ct);

    public Task<SkillDraftEntity?> FindDraftAsync(string id, CancellationToken ct)
        => _db.SkillDrafts.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, ct);

    public Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct)
        => _db.BusinessCredentialMetas.AsNoTracking().FirstOrDefaultAsync(row => row.CredentialRef == credentialRef, ct);

    public Task<PublishedSkillEntity?> FindPublishedSkillAsync(string id, CancellationToken ct)
        => _db.PublishedSkills.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, ct);

    public Task<PublishedSkillVersionEntity?> FindPublishedVersionAsync(string skillId, int version, CancellationToken ct)
        => _db.PublishedSkillVersions.AsNoTracking().FirstOrDefaultAsync(
            row => row.SkillId == skillId && row.Version == version, ct);

    public async Task<IReadOnlyList<PublishedSkillEntity>> ListEnabledPublishedSkillsAsync(CancellationToken ct)
        => await _db.PublishedSkills.AsNoTracking()
            .Where(row => _db.BusinessPlugins.Any(plugin => plugin.Id == row.PluginId && plugin.Enabled))
            .OrderBy(row => row.Id).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PublishedSkillVersionEntity>> ListPublishedVersionsAsync(string[] skillIds, CancellationToken ct)
        => await _db.PublishedSkillVersions.AsNoTracking()
            .Where(row => skillIds.Contains(row.SkillId)).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<CatalogCapabilityEntity>> ListCapabilitiesAsync(string snapshotId, CancellationToken ct)
        => await _db.CatalogCapabilities.AsNoTracking()
            .Where(row => row.SnapshotId == snapshotId).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<BusinessCapabilityRisk>> ListCapabilityRisksAsync(
        string snapshotId, string[] capabilityIds, CancellationToken ct)
        => await _db.CatalogCapabilities.AsNoTracking()
            .Where(row => row.SnapshotId == snapshotId && capabilityIds.Contains(row.Id))
            .Select(row => new BusinessCapabilityRisk(row.Id, row.Risk)).ToListAsync(ct).ConfigureAwait(false);

    public Task<SkillRunEntity?> FindRunAsync(string id, CancellationToken ct)
        => _db.SkillRuns.AsNoTracking().FirstOrDefaultAsync(row => row.RunId == id, ct);

    public Task<SkillRunEntity?> FindIdempotentRunAsync(string skillId, string clientRequestId, CancellationToken ct)
        => _db.SkillRuns.AsNoTracking().FirstOrDefaultAsync(
            row => row.SkillId == skillId && row.ClientRequestId == clientRequestId, ct);

    public async Task<IReadOnlyList<SkillRunEntity>> ListRecentRunsAsync(string skillId, CancellationToken ct)
        => await _db.SkillRuns.AsNoTracking().Where(row => row.SkillId == skillId)
            .OrderByDescending(row => row.CreatedAtUtc).Take(50).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SkillRunEntity>> ListActiveRunsAsync(CancellationToken ct)
        => await _db.SkillRuns.AsNoTracking().Where(row =>
                row.Status == nameof(SkillRunStatus.Queued) ||
                row.Status == nameof(SkillRunStatus.Running) ||
                row.Status == nameof(SkillRunStatus.WaitingForApproval))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SkillRunEntity>> ListWaitingRunsAsync(CancellationToken ct)
        => await _db.SkillRuns.AsNoTracking()
            .Where(row => row.Status == nameof(SkillRunStatus.WaitingForApproval) && row.PendingApprovalJson != null)
            .ToListAsync(ct).ConfigureAwait(false);

    public Task<SkillRunStepEntity[]> ListRunStepsAsync(string[] runIds, CancellationToken ct)
        => _db.SkillRunSteps.AsNoTracking().Where(row => runIds.Contains(row.RunId))
            .OrderBy(row => row.Sequence).ToArrayAsync(ct);
}
