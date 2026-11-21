// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.BusinessSkills;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed record BusinessPluginDirectory(
    List<BusinessPluginEntity> Plugins,
    Dictionary<string, PluginConnectionEntity> Connections,
    Dictionary<string, BusinessCredentialMetaEntity> Credentials,
    Dictionary<string, int> ActiveCapabilityCounts);

public sealed record CatalogCapabilityPage(List<CatalogCapabilityEntity> Capabilities, int FilteredCount);

public sealed record PluginConnectionSaveResult(bool Updated, bool CredentialRemoved = false);

public interface IBusinessPluginStore
{
    Task<BusinessPluginDirectory> LoadDirectoryAsync(CancellationToken ct);
    Task<BusinessPluginEntity?> FindPluginAsync(string pluginId, CancellationToken ct);
    Task<PluginConnectionEntity?> FindConnectionAsync(string connectionId, CancellationToken ct);
    Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct);
    Task<List<string>> FindOtherCredentialUsersAsync(string pluginId, string credentialRef, CancellationToken ct);
    Task<int> CountCapabilitiesAsync(string snapshotId, CancellationToken ct);
    Task<List<CatalogCapabilityEntity>> ReadCapabilitiesAsync(string snapshotId, CancellationToken ct);
    Task<CatalogSnapshotEntity?> FindSnapshotAsync(string snapshotId, string? pluginId, CancellationToken ct);
    Task<CatalogCapabilityEntity?> FindCapabilityAsync(string snapshotId, string capabilityId, CancellationToken ct);
    Task<CatalogCapabilityPage> QueryCapabilitiesAsync(
        string snapshotId, string? search, string? groupTag, bool executableOnly, int page, int pageSize, CancellationToken ct);
    Task<PluginOperationEntity?> FindOperationAsync(string operationId, CancellationToken ct);
    Task<PluginOperationEntity?> FindRequestAsync(string kind, string pluginId, string clientRequestId, CancellationToken ct);

    Task CreatePluginAsync(BusinessPluginEntity plugin, PluginConnectionEntity connection, CancellationToken ct);

    /// <summary>Saves the new connection revision, optional credential metadata, and invalidation in one guarded write.</summary>
    Task<PluginConnectionSaveResult> TrySaveConnectionAsync(
        BusinessPluginEntity plugin, PluginConnectionEntity connection, int expectedRevision,
        BusinessCredentialMetaEntity? newCredential, string? removeCredentialRef, CancellationToken ct);

    Task<bool> DisablePluginAsync(string pluginId, DateTime now, CancellationToken ct);

    /// <summary>Returns the already registered operation if a request with the same id was registered first.</summary>
    Task<PluginOperationEntity?> TryRegisterOperationAsync(PluginOperationEntity operation, CancellationToken ct);
    Task SaveOperationResultAsync(PluginOperationEntity operation, CancellationToken ct);

    /// <summary>Activates a snapshot and records the result atomically if the connection and active snapshot are unchanged.</summary>
    Task<bool> TryActivateCatalogAsync(
        BusinessPluginEntity plugin, string? previousSnapshotId, CatalogSnapshotEntity snapshot,
        IReadOnlyList<CatalogCapabilityEntity> capabilities, PluginOperationEntity operation,
        bool needsRevalidation, CancellationToken ct);

    Task<bool> TryUpdateCapabilityRiskAsync(
        string pluginId, string snapshotId, string capabilityId, string risk, CancellationToken ct);
}
