// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed class EfBusinessPluginStore(IJobDbContext db) : IBusinessPluginStore
{
    public async Task<BusinessPluginDirectory> LoadDirectoryAsync(CancellationToken ct)
    {
        var plugins = await db.BusinessPlugins.AsNoTracking().OrderBy(p => p.CreatedAtUtc).ToListAsync(ct);
        var connections = await db.PluginConnections.AsNoTracking().ToDictionaryAsync(c => c.Id, StringComparer.Ordinal, ct);
        var credentials = await db.BusinessCredentialMetas.AsNoTracking().ToDictionaryAsync(c => c.CredentialRef, StringComparer.Ordinal, ct);
        var counts = await db.CatalogCapabilities.AsNoTracking()
            .Where(c => db.BusinessPlugins.Any(p => p.ActiveSnapshotId == c.SnapshotId))
            .GroupBy(c => c.SnapshotId)
            .Select(g => new { SnapshotId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SnapshotId, x => x.Count, StringComparer.Ordinal, ct);
        return new BusinessPluginDirectory(plugins, connections, credentials, counts);
    }

    public Task<BusinessPluginEntity?> FindPluginAsync(string pluginId, CancellationToken ct)
        => db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pluginId, ct);

    public Task<PluginConnectionEntity?> FindConnectionAsync(string connectionId, CancellationToken ct)
        => db.PluginConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);

    public Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct)
        => db.BusinessCredentialMetas.AsNoTracking().FirstOrDefaultAsync(m => m.CredentialRef == credentialRef, ct);

    public Task<List<string>> FindOtherCredentialUsersAsync(string pluginId, string credentialRef, CancellationToken ct)
        => db.PluginConnections.AsNoTracking()
            .Where(c => c.PluginId != pluginId && (c.DocumentCredentialRef == credentialRef || c.ExecutionCredentialRef == credentialRef))
            .Select(c => c.PluginId).Distinct().ToListAsync(ct);

    public Task<int> CountCapabilitiesAsync(string snapshotId, CancellationToken ct)
        => db.CatalogCapabilities.CountAsync(c => c.SnapshotId == snapshotId, ct);

    public Task<List<CatalogCapabilityEntity>> ReadCapabilitiesAsync(string snapshotId, CancellationToken ct)
        => db.CatalogCapabilities.AsNoTracking().Where(c => c.SnapshotId == snapshotId).ToListAsync(ct);

    public Task<CatalogSnapshotEntity?> FindSnapshotAsync(string snapshotId, string? pluginId, CancellationToken ct)
        => db.CatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId && (pluginId == null || s.PluginId == pluginId), ct);

    public Task<CatalogCapabilityEntity?> FindCapabilityAsync(string snapshotId, string capabilityId, CancellationToken ct)
        => db.CatalogCapabilities.AsNoTracking().FirstOrDefaultAsync(c => c.SnapshotId == snapshotId && c.Id == capabilityId, ct);

    public async Task<CatalogCapabilityPage> QueryCapabilitiesAsync(
        string snapshotId, string? search, string? groupTag, bool executableOnly, int page, int pageSize, CancellationToken ct)
    {
        var query = db.CatalogCapabilities.AsNoTracking().Where(c => c.SnapshotId == snapshotId);
        if (groupTag is not null)
        {
            query = query.Where(c => c.GroupTag == groupTag);
        }

        if (executableOnly)
        {
            query = query.Where(c => c.Executability == nameof(CapabilityExecutabilityStatus.Executable));
        }

        if (search is not null)
        {
            query = query.Where(c => c.Title.Contains(search) || c.Description.Contains(search) || c.NativeIdentity.Contains(search));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(c => c.GroupTag).ThenBy(c => c.Title).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new CatalogCapabilityPage(rows, total);
    }

    public Task<PluginOperationEntity?> FindOperationAsync(string operationId, CancellationToken ct)
        => db.PluginOperations.AsNoTracking().FirstOrDefaultAsync(o => o.OperationId == operationId, ct);

    public Task<PluginOperationEntity?> FindRequestAsync(string kind, string pluginId, string clientRequestId, CancellationToken ct)
        => db.PluginOperations.AsNoTracking().FirstOrDefaultAsync(
            o => o.Kind == kind && o.TargetId == pluginId && o.ClientRequestId == clientRequestId, ct);

    public async Task CreatePluginAsync(BusinessPluginEntity plugin, PluginConnectionEntity connection, CancellationToken ct)
    {
        db.BusinessPlugins.Add(plugin);
        db.PluginConnections.Add(connection);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PluginConnectionSaveResult> TrySaveConnectionAsync(
        BusinessPluginEntity plugin, PluginConnectionEntity connection, int expectedRevision,
        BusinessCredentialMetaEntity? newCredential, string? removeCredentialRef, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var updated = await db.PluginConnections.Where(c => c.Id == connection.Id && c.Revision == expectedRevision)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.SourceUrl, connection.SourceUrl)
                .SetProperty(c => c.Environment, connection.Environment)
                .SetProperty(c => c.EndpointPolicyJson, connection.EndpointPolicyJson)
                .SetProperty(c => c.DocumentCredentialRef, connection.DocumentCredentialRef)
                .SetProperty(c => c.ExecutionCredentialRef, connection.ExecutionCredentialRef)
                .SetProperty(c => c.Revision, connection.Revision)
                .SetProperty(c => c.CredentialRevision, connection.CredentialRevision)
                .SetProperty(c => c.IsEncryptedTransport, connection.IsEncryptedTransport)
                .SetProperty(c => c.UpdatedAtUtc, connection.UpdatedAtUtc), ct);
        if (updated == 0)
        {
            return new PluginConnectionSaveResult(false);
        }

        await db.BusinessPlugins.Where(p => p.Id == plugin.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.UpdatedAtUtc, plugin.UpdatedAtUtc), ct);

        if (newCredential is not null)
        {
            var changed = await db.BusinessCredentialMetas.Where(m => m.CredentialRef == newCredential.CredentialRef)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.AuthKind, newCredential.AuthKind)
                    .SetProperty(m => m.MaskedHint, newCredential.MaskedHint)
                    .SetProperty(m => m.Revision, newCredential.Revision)
                    .SetProperty(m => m.CipherFileName, newCredential.CipherFileName)
                    .SetProperty(m => m.UpdatedAtUtc, newCredential.UpdatedAtUtc), ct);
            if (changed == 0)
            {
                db.BusinessCredentialMetas.Add(newCredential);
                await db.SaveChangesAsync(ct);
            }
        }

        var removed = removeCredentialRef is not null && await db.BusinessCredentialMetas
            .Where(m => m.CredentialRef == removeCredentialRef && !db.PluginConnections.Any(
                c => c.DocumentCredentialRef == removeCredentialRef || c.ExecutionCredentialRef == removeCredentialRef))
            .ExecuteDeleteAsync(ct) > 0;
        await MarkDefinitionsAsync(plugin.Id, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new PluginConnectionSaveResult(true, removed);
    }

    public async Task<bool> DisablePluginAsync(string pluginId, DateTime now, CancellationToken ct)
        => await db.BusinessPlugins.Where(p => p.Id == pluginId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Enabled, false)
                .SetProperty(p => p.ArchivedAtUtc, p => p.ArchivedAtUtc ?? now)
                .SetProperty(p => p.UpdatedAtUtc, now), ct) != 0;

    public async Task<PluginOperationEntity?> TryRegisterOperationAsync(PluginOperationEntity operation, CancellationToken ct)
    {
        db.PluginOperations.Add(operation);
        try
        {
            await db.SaveChangesAsync(ct);
            db.Entry(operation).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            db.Entry(operation).State = EntityState.Detached;
            var existing = await FindRequestAsync(operation.Kind, operation.TargetId, operation.ClientRequestId, ct);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    public Task SaveOperationResultAsync(PluginOperationEntity operation, CancellationToken ct)
        => db.PluginOperations.Where(o => o.OperationId == operation.OperationId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, operation.Status)
                .SetProperty(o => o.Sequence, operation.Sequence)
                .SetProperty(o => o.SnapshotId, operation.SnapshotId)
                .SetProperty(o => o.ResultMessage, operation.ResultMessage)
                .SetProperty(o => o.ErrorJson, operation.ErrorJson)
                .SetProperty(o => o.UpdatedAtUtc, operation.UpdatedAtUtc), ct);

    public async Task<bool> TryActivateCatalogAsync(
        BusinessPluginEntity plugin, string? previousSnapshotId, CatalogSnapshotEntity snapshot,
        IReadOnlyList<CatalogCapabilityEntity> capabilities, PluginOperationEntity operation,
        bool needsRevalidation, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var updated = await db.BusinessPlugins.Where(p => p.Id == plugin.Id && p.Enabled
                && p.ActiveSnapshotId == previousSnapshotId && db.PluginConnections.Any(
                    c => c.Id == p.ConnectionId && c.Revision == snapshot.ConnectionRevision
                        && c.CredentialRevision == snapshot.CredentialRevision))
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.ActiveSnapshotId, snapshot.Id)
                .SetProperty(p => p.LastDiscoveredAtUtc, plugin.LastDiscoveredAtUtc)
                .SetProperty(p => p.UpdatedAtUtc, plugin.UpdatedAtUtc), ct);
        if (updated == 0)
        {
            return false;
        }

        db.CatalogSnapshots.Add(snapshot);
        db.CatalogCapabilities.AddRange(capabilities);
        await db.SaveChangesAsync(ct);
        await SaveOperationResultAsync(operation, ct);
        if (needsRevalidation)
        {
            await MarkDefinitionsAsync(plugin.Id, ct);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    public async Task<bool> TryUpdateCapabilityRiskAsync(
        string pluginId, string snapshotId, string capabilityId, string risk, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var updated = await db.CatalogCapabilities.Where(c => c.SnapshotId == snapshotId && c.Id == capabilityId
                && db.BusinessPlugins.Any(p => p.Id == pluginId && p.ActiveSnapshotId == snapshotId))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Risk, risk), ct);
        if (updated == 0)
        {
            return false;
        }

        await MarkDefinitionsAsync(pluginId, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    private async Task MarkDefinitionsAsync(string pluginId, CancellationToken ct)
    {
        await db.SkillDrafts.Where(d => d.PluginId == pluginId && !d.NeedsRevalidation)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.NeedsRevalidation, true), ct);
        await db.PublishedSkillVersions.Where(v => !v.NeedsRevalidation
                && db.PublishedSkills.Any(s => s.Id == v.SkillId && s.PluginId == pluginId))
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.NeedsRevalidation, true), ct);
    }
}
