// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed record BusinessServiceResult<T>(
    bool Success,
    int StatusCode,
    T? Value = default,
    BusinessSkillErrorDto? Error = null)
{
    public static BusinessServiceResult<T> Ok(T value, int statusCode = 200)
        => new(true, statusCode, value, null);

    public static BusinessServiceResult<T> Fail(int statusCode, string code, string message, bool retryable = false, IReadOnlyList<BusinessSkillFieldError>? fieldErrors = null)
        => new(false, statusCode, default, new BusinessSkillErrorDto(
            code,
            message,
            fieldErrors ?? [],
            retryable,
            Guid.NewGuid().ToString("N")));
}

public interface IBusinessPluginControlService
{
    Task<IReadOnlyList<BusinessPluginDto>> ListPluginsAsync(CancellationToken ct = default);
    Task<BusinessServiceResult<BusinessPluginDto>> CreatePluginAsync(CreateBusinessPluginRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<BusinessPluginDto>> UpdateConnectionAsync(string pluginId, UpdatePluginConnectionRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<CredentialRefSummaryDto>> SetCredentialAsync(string pluginId, string slot, SetPluginCredentialRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<DeleteCredentialResponseDto>> DeleteCredentialAsync(string pluginId, string slot, bool unbindIfReferenced, CancellationToken ct = default);
    Task<BusinessServiceResult<DisablePluginResponseDto>> DisablePluginAsync(string pluginId, CancellationToken ct = default);
    Task<BusinessServiceResult<DiscoverPluginResultDto>> DiscoverAsync(string pluginId, DiscoverPluginRequest request, CancellationToken ct = default);
    Task<BusinessServiceResult<CapabilityPageResponseDto>> QueryCapabilitiesAsync(
        string pluginId,
        string? snapshotId,
        string? search,
        string? groupTag,
        bool executableOnly,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<BusinessServiceResult<CapabilitySummaryDto>> UpdateCapabilityRiskAsync(
        string pluginId,
        string capabilityId,
        UpdateCapabilityRiskRequest request,
        CancellationToken ct = default);
    Task<BusinessServiceResult<CapabilityDetailDto>> GetCapabilityDetailAsync(
        string pluginId,
        string? snapshotId,
        string capabilityId,
        CancellationToken ct = default);
    Task<BusinessServiceResult<PluginOperationStatusDto>> GetOperationStatusAsync(string operationId, CancellationToken ct = default);
}

public sealed class BusinessPluginControlService : IBusinessPluginControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IJobDbContext _db;
    private readonly IBusinessCredentialStore _credentialStore;
    private readonly OutboundTargetPolicyGuard _targetGuard;
    private readonly OpenApiBusinessPluginAdapter _openApiAdapter;
    private readonly McpBusinessPluginAdapter _mcpAdapter;
    private readonly IBusinessSkillApprovalCoordinator? _approvalCoordinator;

    public BusinessPluginControlService(
        IJobDbContext db,
        IBusinessCredentialStore credentialStore,
        OutboundTargetPolicyGuard targetGuard,
        OpenApiBusinessPluginAdapter? openApiAdapter = null,
        McpBusinessPluginAdapter? mcpAdapter = null,
        IBusinessSkillApprovalCoordinator? approvalCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(targetGuard);
        _db = db;
        _credentialStore = credentialStore;
        _targetGuard = targetGuard;
        _openApiAdapter = openApiAdapter ?? new OpenApiBusinessPluginAdapter(targetGuard);
        _mcpAdapter = mcpAdapter ?? new McpBusinessPluginAdapter(targetGuard);
        _approvalCoordinator = approvalCoordinator;
    }

    public async Task<IReadOnlyList<BusinessPluginDto>> ListPluginsAsync(CancellationToken ct = default)
    {
        var plugins = await _db.BusinessPlugins.AsNoTracking().OrderBy(p => p.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
        var connections = await _db.PluginConnections.AsNoTracking().ToDictionaryAsync(c => c.Id, StringComparer.Ordinal, ct).ConfigureAwait(false);
        var credMetas = await _db.BusinessCredentialMetas.AsNoTracking().ToDictionaryAsync(m => m.CredentialRef, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var activeSnapshotIds = plugins
            .Select(p => p.ActiveSnapshotId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var capabilityCounts = activeSnapshotIds.Length == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await _db.CatalogCapabilities.AsNoTracking()
                .Where(c => activeSnapshotIds.Contains(c.SnapshotId))
                .GroupBy(c => c.SnapshotId)
                .Select(g => new { SnapshotId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.SnapshotId, x => x.Count, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

        var result = new List<BusinessPluginDto>(plugins.Count);
        foreach (var plugin in plugins)
        {
            connections.TryGetValue(plugin.ConnectionId, out var conn);
            var count = plugin.ActiveSnapshotId is not null && capabilityCounts.TryGetValue(plugin.ActiveSnapshotId, out var c)
                ? c
                : 0;
            result.Add(MapPluginDto(plugin, conn, credMetas, count));
        }

        return result;
    }

    public async Task<BusinessServiceResult<BusinessPluginDto>> CreatePluginAsync(
        CreateBusinessPluginRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Plugin name is required.");
        }

        if (!Uri.TryCreate(request.SourceUrl?.Trim(), UriKind.Absolute, out var sourceUri))
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "SourceUrl must be an absolute HTTP/HTTPS URL.");
        }

        var policy = NormalizePolicy(request.EndpointPolicy, sourceUri);
        var eval = _targetGuard.ValidateUri(sourceUri, policy, BusinessAuthKind.None, OutboundRequestPurpose.Discovery);
        if (!eval.Allowed)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(422, eval.ErrorCode ?? BusinessSkillErrorCodes.ValidationFailed, eval.Message ?? "Target URL denied by endpoint policy.");
        }

        var pluginId = $"plg-{Guid.NewGuid():N}"[..16];
        var connectionId = $"conn-{Guid.NewGuid():N}"[..16];
        var now = DateTime.UtcNow;

        var plugin = new BusinessPluginEntity
        {
            Id = pluginId,
            Name = request.Name.Trim(),
            SourceType = request.SourceType.ToString(),
            ConnectionId = connectionId,
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var connection = new PluginConnectionEntity
        {
            Id = connectionId,
            PluginId = pluginId,
            SourceUrl = sourceUri.AbsoluteUri,
            Environment = string.IsNullOrWhiteSpace(request.Environment) ? "default" : request.Environment.Trim(),
            EndpointPolicyJson = JsonSerializer.Serialize(policy, JsonOptions),
            Revision = 1,
            CredentialRevision = "none",
            IsEncryptedTransport = eval.IsEncryptedTransport,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.BusinessPlugins.Add(plugin);
        _db.PluginConnections.Add(connection);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var credMetas = new Dictionary<string, BusinessCredentialMetaEntity>(StringComparer.Ordinal);
        return BusinessServiceResult<BusinessPluginDto>.Ok(MapPluginDto(plugin, connection, credMetas, 0), 201);
    }

    public async Task<BusinessServiceResult<BusinessPluginDto>> UpdateConnectionAsync(
        string pluginId,
        UpdatePluginConnectionRequest request,
        CancellationToken ct = default)
    {
        if (request is null || !Uri.TryCreate(request.SourceUrl?.Trim(), UriKind.Absolute, out var sourceUri))
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Valid SourceUrl is required.");
        }

        var plugin = await _db.BusinessPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _db.PluginConnections.FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Connection for plugin '{pluginId}' not found.");
        }

        if (connection.Revision != request.ExpectedRevision)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(
                409,
                BusinessSkillErrorCodes.CatalogChanged,
                $"Connection revision conflict: expected {request.ExpectedRevision}, actual {connection.Revision}.");
        }

        var credMetas = await LoadConnectionCredMetasAsync(connection, ct).ConfigureAwait(false);
        var effectiveAuth = ResolveEffectiveAuthKind(connection, credMetas);
        var policy = NormalizePolicy(request.EndpointPolicy, sourceUri);

        var eval = _targetGuard.ValidateUri(sourceUri, policy, effectiveAuth, OutboundRequestPurpose.Discovery);
        if (!eval.Allowed)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(422, eval.ErrorCode ?? BusinessSkillErrorCodes.ValidationFailed, eval.Message ?? "Target URL denied by endpoint policy.");
        }

        connection.SourceUrl = sourceUri.AbsoluteUri;
        connection.Environment = string.IsNullOrWhiteSpace(request.Environment) ? "default" : request.Environment.Trim();
        connection.EndpointPolicyJson = JsonSerializer.Serialize(policy, JsonOptions);
        connection.IsEncryptedTransport = eval.IsEncryptedTransport;
        connection.Revision += 1;
        connection.UpdatedAtUtc = DateTime.UtcNow;
        plugin.UpdatedAtUtc = connection.UpdatedAtUtc;

        await MarkDependentDefinitionsNeedsRevalidationAsync(pluginId, ct).ConfigureAwait(false);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(
                409,
                BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent connection update detected; reload latest connection state.");
        }

        var capCount = plugin.ActiveSnapshotId is null
            ? 0
            : await _db.CatalogCapabilities.CountAsync(c => c.SnapshotId == plugin.ActiveSnapshotId, ct).ConfigureAwait(false);

        return BusinessServiceResult<BusinessPluginDto>.Ok(MapPluginDto(plugin, connection, credMetas, capCount));
    }

    public async Task<BusinessServiceResult<CredentialRefSummaryDto>> SetCredentialAsync(
        string pluginId,
        string slot,
        SetPluginCredentialRequest request,
        CancellationToken ct = default)
    {
        if (!IsValidCredentialSlot(slot, out var normalizedSlot))
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Credential slot must be 'document' or 'execution'.");
        }

        if (request is null)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Request body is required.");
        }

        if (!_credentialStore.IsPersistentProtectionAvailable)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(
                503,
                BusinessSkillErrorCodes.ConnectionUnavailable,
                "OS-native encrypted credential storage is not available on this host; plaintext fallback is disabled.");
        }

        var plugin = await _db.BusinessPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _db.PluginConnections.FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Connection for plugin '{pluginId}' not found.");
        }

        if (request.AuthKind == BusinessAuthKind.BasicTlsOnly && !connection.IsEncryptedTransport)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(
                422,
                BusinessSkillErrorCodes.AuthRequired,
                "Basic authentication strictly requires HTTPS transport.");
        }

        if (request.AuthKind != BusinessAuthKind.None && string.IsNullOrWhiteSpace(request.SecretPayload))
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(
                422,
                BusinessSkillErrorCodes.ValidationFailed,
                "SecretPayload is required for non-None authentication kinds.");
        }

        var credentialRef = $"cred:{pluginId}:{normalizedSlot}";
        var newCredentialRevision = Guid.NewGuid().ToString("N")[..12];
        var stored = await _credentialStore.StoreSecretAsync(
            credentialRef,
            request.AuthKind,
            request.SecretPayload ?? string.Empty,
            newCredentialRevision,
            ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var meta = await _db.BusinessCredentialMetas.FirstOrDefaultAsync(m => m.CredentialRef == credentialRef, ct).ConfigureAwait(false);
        if (meta is null)
        {
            meta = new BusinessCredentialMetaEntity
            {
                CredentialRef = credentialRef,
                AuthKind = request.AuthKind.ToString(),
                MaskedHint = stored.Summary.MaskedHint,
                Revision = newCredentialRevision,
                CipherFileName = stored.CipherFileName,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.BusinessCredentialMetas.Add(meta);
        }
        else
        {
            meta.AuthKind = request.AuthKind.ToString();
            meta.MaskedHint = stored.Summary.MaskedHint;
            meta.Revision = newCredentialRevision;
            meta.CipherFileName = stored.CipherFileName;
            meta.UpdatedAtUtc = now;
        }

        if (normalizedSlot == "document")
        {
            connection.DocumentCredentialRef = credentialRef;
        }
        else
        {
            connection.ExecutionCredentialRef = credentialRef;
        }

        connection.CredentialRevision = newCredentialRevision;
        connection.Revision += 1;
        connection.UpdatedAtUtc = now;
        plugin.UpdatedAtUtc = now;

        await MarkDependentDefinitionsNeedsRevalidationAsync(pluginId, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return BusinessServiceResult<CredentialRefSummaryDto>.Ok(stored.Summary);
    }

    public async Task<BusinessServiceResult<DeleteCredentialResponseDto>> DeleteCredentialAsync(
        string pluginId,
        string slot,
        bool unbindIfReferenced,
        CancellationToken ct = default)
    {
        if (!IsValidCredentialSlot(slot, out var normalizedSlot))
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Credential slot must be 'document' or 'execution'.");
        }

        var plugin = await _db.BusinessPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _db.PluginConnections.FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Connection for plugin '{pluginId}' not found.");
        }

        var credRef = normalizedSlot == "document" ? connection.DocumentCredentialRef : connection.ExecutionCredentialRef;
        if (string.IsNullOrEmpty(credRef))
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.AuthRequired, $"No credential bound to '{normalizedSlot}' slot.");
        }

        var referencedByOthers = await _db.PluginConnections.AsNoTracking()
            .Where(c => c.PluginId != pluginId && (c.DocumentCredentialRef == credRef || c.ExecutionCredentialRef == credRef))
            .Select(c => c.PluginId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var otherSlotStillUsesSameRef = normalizedSlot == "document"
            ? string.Equals(connection.ExecutionCredentialRef, credRef, StringComparison.Ordinal)
            : string.Equals(connection.DocumentCredentialRef, credRef, StringComparison.Ordinal);

        if ((referencedByOthers.Count > 0 || otherSlotStillUsesSameRef) && !unbindIfReferenced)
        {
            var allRefs = referencedByOthers.Concat(otherSlotStillUsesSameRef ? [pluginId] : []).Distinct(StringComparer.Ordinal).ToArray();
            return BusinessServiceResult<DeleteCredentialResponseDto>.Ok(
                new DeleteCredentialResponseDto(false, credRef, allRefs.Length, allRefs),
                409);
        }

        if (normalizedSlot == "document")
        {
            connection.DocumentCredentialRef = null;
        }
        else
        {
            connection.ExecutionCredentialRef = null;
        }

        connection.CredentialRevision = Guid.NewGuid().ToString("N")[..12];
        connection.Revision += 1;
        connection.UpdatedAtUtc = DateTime.UtcNow;
        await MarkDependentDefinitionsNeedsRevalidationAsync(pluginId, ct).ConfigureAwait(false);

        var meta = await _db.BusinessCredentialMetas.FirstOrDefaultAsync(m => m.CredentialRef == credRef, ct).ConfigureAwait(false);
        var remaining = referencedByOthers.Count + (otherSlotStillUsesSameRef ? 1 : 0);
        if (meta is not null && remaining == 0)
        {
            await _credentialStore.DeleteSecretAsync(credRef, meta.CipherFileName, ct).ConfigureAwait(false);
            _db.BusinessCredentialMetas.Remove(meta);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return BusinessServiceResult<DeleteCredentialResponseDto>.Ok(
            new DeleteCredentialResponseDto(true, credRef, remaining, referencedByOthers));
    }

    public async Task<BusinessServiceResult<DisablePluginResponseDto>> DisablePluginAsync(
        string pluginId,
        CancellationToken ct = default)
    {
        var plugin = await _db.BusinessPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<DisablePluginResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var now = DateTime.UtcNow;
        plugin.Enabled = false;
        plugin.ArchivedAtUtc ??= now;
        plugin.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (_approvalCoordinator is not null)
        {
            await _approvalCoordinator.InvalidatePluginApprovalsAndRunsAsync(
                pluginId,
                $"Plugin '{pluginId}' was disabled; subsequent dispatches and pending approvals cancelled.",
                ct).ConfigureAwait(false);
        }

        return BusinessServiceResult<DisablePluginResponseDto>.Ok(
            new DisablePluginResponseDto(plugin.Id, plugin.Enabled, new DateTimeOffset(now, TimeSpan.Zero)));
    }

    public async Task<BusinessServiceResult<DiscoverPluginResultDto>> DiscoverAsync(
        string pluginId,
        DiscoverPluginRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "clientRequestId is required.");
        }

        var normalizedClientRequestId = request.ClientRequestId.Trim();
        var existingOp = await _db.PluginOperations.AsNoTracking()
            .FirstOrDefaultAsync(op => op.Kind == "discover" && op.TargetId == pluginId && op.ClientRequestId == normalizedClientRequestId, ct)
            .ConfigureAwait(false);

        if (existingOp is not null)
        {
            var reusedAccepted = new PluginOperationAcceptedDto(
                existingOp.OperationId,
                existingOp.Kind,
                Enum.TryParse<PluginOperationStatus>(existingOp.Status, out var st) ? st : PluginOperationStatus.Succeeded,
                existingOp.Sequence,
                existingOp.Version,
                ReusedFromClientRequestId: true);

            CatalogSnapshotDto? reusedSnapshot = null;
            if (!string.IsNullOrEmpty(existingOp.SnapshotId))
            {
                var reusedSnapEntity = await _db.CatalogSnapshots.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == existingOp.SnapshotId, ct)
                    .ConfigureAwait(false);
                if (reusedSnapEntity is not null)
                {
                    reusedSnapshot = MapSnapshotDto(reusedSnapEntity);
                }
            }

            return BusinessServiceResult<DiscoverPluginResultDto>.Ok(
                new DiscoverPluginResultDto(reusedAccepted, reusedSnapshot, null),
                202);
        }

        var plugin = await _db.BusinessPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        if (!plugin.Enabled)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' is disabled.");
        }

        var connection = await _db.PluginConnections.FirstOrDefaultAsync(c => c.Id == plugin.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Connection for plugin '{pluginId}' not found.");
        }

        var policy = DeserializePolicy(connection.EndpointPolicyJson, connection.SourceUrl);
        var sourceUri = new Uri(connection.SourceUrl, UriKind.Absolute);

        var (docAuthKind, secretJson) = await ResolveDocumentCredentialSecretAsync(connection, ct).ConfigureAwait(false);
        var targetEval = await _targetGuard.ValidateAndResolveAsync(sourceUri, policy, docAuthKind, OutboundRequestPurpose.Discovery, ct).ConfigureAwait(false);
        if (!targetEval.Allowed)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(422, targetEval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation, targetEval.Message ?? "Target URI denied by policy.");
        }

        var operationId = $"op-{Guid.NewGuid():N}"[..16];
        var snapshotId = $"snap-{Guid.NewGuid():N}"[..16];
        var now = DateTime.UtcNow;

        var opEntity = new PluginOperationEntity
        {
            OperationId = operationId,
            Kind = "discover",
            TargetId = pluginId,
            ClientRequestId = normalizedClientRequestId,
            Status = PluginOperationStatus.Running.ToString(),
            Sequence = 1,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.PluginOperations.Add(opEntity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CatalogSnapshotDto? newSnapshotDto = null;
        IReadOnlyList<CapabilityDetailDto> discoveredCapabilities = [];
        BusinessSkillErrorDto? discoveryError = null;

        try
        {
            var sourceType = Enum.Parse<BusinessPluginSourceType>(plugin.SourceType);
            if (sourceType == BusinessPluginSourceType.OpenApi)
            {
                var openApiRes = await _openApiAdapter.DiscoverFromUriAsync(
                    plugin.Id,
                    snapshotId,
                    sourceUri,
                    policy,
                    docAuthKind,
                    secretJson,
                    ct).ConfigureAwait(false);
                if (!openApiRes.Success || openApiRes.Snapshot is null)
                {
                    discoveryError = openApiRes.Error is not null
                        ? openApiRes.Error with { CorrelationId = operationId }
                        : null;
                }
                else
                {
                    newSnapshotDto = openApiRes.Snapshot;
                    discoveredCapabilities = openApiRes.Capabilities;
                }
            }
            else
            {
                var mcpRes = await _mcpAdapter.DiscoverAsync(
                    plugin.Id,
                    snapshotId,
                    sourceUri,
                    policy,
                    docAuthKind,
                    secretJson,
                    loggerFactory: null,
                    ct).ConfigureAwait(false);
                if (!mcpRes.Success || mcpRes.Snapshot is null)
                {
                    discoveryError = mcpRes.Error is not null
                        ? mcpRes.Error with { CorrelationId = operationId }
                        : null;
                }
                else
                {
                    newSnapshotDto = mcpRes.Snapshot;
                    discoveredCapabilities = mcpRes.Capabilities;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            discoveryError = new BusinessSkillErrorDto(BusinessSkillErrorCodes.ConnectionUnavailable, ex.Message, [], true, operationId);
        }

        if (discoveryError is not null || newSnapshotDto is null)
        {
            var effectiveErr = discoveryError ?? new BusinessSkillErrorDto(BusinessSkillErrorCodes.ConnectionUnavailable, "Discovery failed.", [], true, operationId);
            opEntity.Status = PluginOperationStatus.Failed.ToString();
            opEntity.Sequence = 2;
            opEntity.ResultMessage = effectiveErr.Message;
            opEntity.ErrorJson = JsonSerializer.Serialize(effectiveErr, JsonOptions);
            opEntity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var statusCode = effectiveErr.Code switch
            {
                BusinessSkillErrorCodes.AuthRequired => 401,
                BusinessSkillErrorCodes.UnsupportedProtocol or BusinessSkillErrorCodes.UnsupportedSchema or BusinessSkillErrorCodes.SelectionViolation => 422,
                _ => 502
            };
            return new BusinessServiceResult<DiscoverPluginResultDto>(false, statusCode, null, effectiveErr);
        }

        var previousCaps = string.IsNullOrEmpty(plugin.ActiveSnapshotId)
            ? new List<CatalogCapabilityEntity>()
            : await _db.CatalogCapabilities.AsNoTracking()
                .Where(c => c.SnapshotId == plugin.ActiveSnapshotId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var diff = !string.IsNullOrEmpty(plugin.ActiveSnapshotId)
            ? ComputeCatalogDiff(plugin.ActiveSnapshotId, snapshotId, previousCaps, discoveredCapabilities)
            : null;

        var snapEntity = new CatalogSnapshotEntity
        {
            Id = snapshotId,
            PluginId = plugin.Id,
            ConnectionRevision = connection.Revision,
            CredentialRevision = connection.CredentialRevision,
            Environment = connection.Environment,
            ProtocolVersion = newSnapshotDto.ProtocolVersion,
            SourceHash = newSnapshotDto.SourceHash,
            SchemaHash = newSnapshotDto.SchemaHash,
            Complete = newSnapshotDto.Complete,
            TotalCapabilities = newSnapshotDto.TotalCapabilities,
            ExecutableCount = newSnapshotDto.ExecutableCount,
            NeedsConfigurationCount = newSnapshotDto.NeedsConfigurationCount,
            UnsupportedCount = newSnapshotDto.UnsupportedCount,
            DiagnosticsJson = JsonSerializer.Serialize(newSnapshotDto.Diagnostics, JsonOptions),
            FetchedAtUtc = newSnapshotDto.FetchedAtUtc.UtcDateTime
        };

        _db.CatalogSnapshots.Add(snapEntity);
        foreach (var cap in discoveredCapabilities)
        {
            _db.CatalogCapabilities.Add(new CatalogCapabilityEntity
            {
                SnapshotId = snapshotId,
                Id = cap.Summary.Id,
                NativeIdentity = cap.Summary.NativeIdentity,
                ModelAlias = cap.Summary.ModelAlias,
                Title = cap.Summary.Title,
                Description = cap.Summary.Description,
                SourceDescriptionMissing = cap.Summary.SourceDescriptionMissing,
                GroupTag = cap.Summary.GroupTag,
                Risk = cap.Summary.Risk.ToString(),
                SuggestedSourceRiskHint = cap.Summary.SuggestedSourceRiskHint?.ToString(),
                Executability = cap.Summary.Executability.ToString(),
                DiagnosticsJson = JsonSerializer.Serialize(cap.Summary.Diagnostics, JsonOptions),
                InputSchemaJson = cap.InputSchemaJson,
                OutputSchemaJson = cap.OutputSchemaJson,
                InvocationJson = JsonSerializer.Serialize(cap.Invocation, JsonOptions)
            });
        }

        plugin.ActiveSnapshotId = snapshotId;
        plugin.LastDiscoveredAtUtc = newSnapshotDto.FetchedAtUtc.UtcDateTime;
        plugin.UpdatedAtUtc = DateTime.UtcNow;

        opEntity.Status = PluginOperationStatus.Succeeded.ToString();
        opEntity.Sequence = 2;
        opEntity.SnapshotId = snapshotId;
        opEntity.ResultMessage = $"Discovered {discoveredCapabilities.Count} capabilities.";
        opEntity.UpdatedAtUtc = DateTime.UtcNow;

        if (diff is { TriggersNeedsRevalidation: true })
        {
            await MarkDependentDefinitionsNeedsRevalidationAsync(plugin.Id, ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var acceptedDto = new PluginOperationAcceptedDto(
            operationId,
            "discover",
            PluginOperationStatus.Succeeded,
            opEntity.Sequence,
            opEntity.Version,
            ReusedFromClientRequestId: false);

        return BusinessServiceResult<DiscoverPluginResultDto>.Ok(
            new DiscoverPluginResultDto(acceptedDto, newSnapshotDto, diff),
            202);
    }

    public async Task<BusinessServiceResult<CapabilityPageResponseDto>> QueryCapabilitiesAsync(
        string pluginId,
        string? snapshotId,
        string? search,
        string? groupTag,
        bool executableOnly,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var targetSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? plugin.ActiveSnapshotId : snapshotId.Trim();
        if (string.IsNullOrWhiteSpace(targetSnapshotId))
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Plugin has no catalog snapshot yet.");
        }

        var snap = await _db.CatalogSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == targetSnapshotId && s.PluginId == pluginId, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{targetSnapshotId}' not found for plugin '{pluginId}'.");
        }

        var query = _db.CatalogCapabilities.AsNoTracking().Where(c => c.SnapshotId == targetSnapshotId);

        if (!string.IsNullOrWhiteSpace(groupTag))
        {
            var g = groupTag.Trim();
            query = query.Where(c => c.GroupTag == g);
        }

        if (executableOnly)
        {
            query = query.Where(c => c.Executability == nameof(CapabilityExecutabilityStatus.Executable));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c =>
                c.Title.Contains(term) ||
                c.Description.Contains(term) ||
                c.NativeIdentity.Contains(term));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var filteredCount = await query.CountAsync(ct).ConfigureAwait(false);

        var rows = await query
            .OrderBy(c => c.GroupTag)
            .ThenBy(c => c.Title)
            .ThenBy(c => c.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = rows.Select(MapCapabilitySummary).ToArray();
        return BusinessServiceResult<CapabilityPageResponseDto>.Ok(new CapabilityPageResponseDto(
            pluginId,
            snap.Id,
            snap.Complete,
            snap.TotalCapabilities,
            filteredCount,
            snap.ExecutableCount,
            snap.NeedsConfigurationCount,
            snap.UnsupportedCount,
            safePage,
            safePageSize,
            items));
    }

    public async Task<BusinessServiceResult<CapabilitySummaryDto>> UpdateCapabilityRiskAsync(
        string pluginId,
        string capabilityId,
        UpdateCapabilityRiskRequest request,
        CancellationToken ct = default)
    {
        if (request is null)
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "Request body is required.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin?.ActiveSnapshotId is null)
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Active catalog snapshot not found for plugin '{pluginId}'.");
        }

        var cap = await _db.CatalogCapabilities
            .FirstOrDefaultAsync(c => c.SnapshotId == plugin.ActiveSnapshotId && c.Id == capabilityId, ct)
            .ConfigureAwait(false);
        if (cap is null)
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Capability '{capabilityId}' not found in active snapshot.");
        }

        cap.Risk = request.Risk.ToString();
        await MarkDependentDefinitionsNeedsRevalidationAsync(pluginId, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return BusinessServiceResult<CapabilitySummaryDto>.Ok(MapCapabilitySummary(cap));
    }

    public async Task<BusinessServiceResult<CapabilityDetailDto>> GetCapabilityDetailAsync(
        string pluginId,
        string? snapshotId,
        string capabilityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(422, BusinessSkillErrorCodes.ValidationFailed, "capabilityId is required.");
        }

        var plugin = await _db.BusinessPlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var targetSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? plugin.ActiveSnapshotId : snapshotId.Trim();
        if (string.IsNullOrWhiteSpace(targetSnapshotId))
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Plugin has no catalog snapshot yet.");
        }

        var snap = await _db.CatalogSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == targetSnapshotId && s.PluginId == pluginId, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{targetSnapshotId}' not found for plugin '{pluginId}'.");
        }

        var cap = await _db.CatalogCapabilities.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SnapshotId == targetSnapshotId && c.Id == capabilityId.Trim(), ct)
            .ConfigureAwait(false);
        if (cap is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Capability '{capabilityId}' not found in snapshot '{targetSnapshotId}'.");
        }

        var invocation = JsonSerializer.Deserialize<CapabilityInvocationSpecDto>(cap.InvocationJson, JsonOptions)
                         ?? new CapabilityInvocationSpecDto();

        return BusinessServiceResult<CapabilityDetailDto>.Ok(new CapabilityDetailDto
        {
            Summary = MapCapabilitySummary(cap),
            InputSchemaJson = cap.InputSchemaJson,
            OutputSchemaJson = cap.OutputSchemaJson,
            Invocation = invocation
        });
    }

    public async Task<BusinessServiceResult<PluginOperationStatusDto>> GetOperationStatusAsync(
        string operationId,
        CancellationToken ct = default)
    {
        var op = await _db.PluginOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct)
            .ConfigureAwait(false);
        if (op is null)
        {
            return BusinessServiceResult<PluginOperationStatusDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Operation '{operationId}' not found.");
        }

        BusinessSkillErrorDto? err = null;
        if (!string.IsNullOrEmpty(op.ErrorJson))
        {
            err = JsonSerializer.Deserialize<BusinessSkillErrorDto>(op.ErrorJson, JsonOptions);
        }

        return BusinessServiceResult<PluginOperationStatusDto>.Ok(new PluginOperationStatusDto(
            op.OperationId,
            op.Kind,
            Enum.TryParse<PluginOperationStatus>(op.Status, out var st) ? st : PluginOperationStatus.Succeeded,
            op.Sequence,
            op.Version,
            op.TargetId,
            op.SnapshotId,
            op.ResultMessage,
            err,
            new DateTimeOffset(op.CreatedAtUtc, TimeSpan.Zero),
            new DateTimeOffset(op.UpdatedAtUtc, TimeSpan.Zero)));
    }

    private static CatalogDiffDto ComputeCatalogDiff(
        string baseSnapshotId,
        string targetSnapshotId,
        IReadOnlyList<CatalogCapabilityEntity> previous,
        IReadOnlyList<CapabilityDetailDto> current)
    {
        var prevMap = previous.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var currMap = current.ToDictionary(c => c.Summary.Id, StringComparer.Ordinal);
        var entries = new List<CatalogDiffEntryDto>();
        var needsRevalidation = false;

        foreach (var curr in current)
        {
            if (!prevMap.TryGetValue(curr.Summary.Id, out var old))
            {
                entries.Add(new(curr.Summary.Id, curr.Summary.Title, CatalogDiffChangeKind.Added, false, "Added capability (not auto-added to existing skills)."));
                continue;
            }

            var currentInvJson = JsonSerializer.Serialize(curr.Invocation, JsonOptions);
            if (!string.Equals(old.InputSchemaJson, curr.InputSchemaJson, StringComparison.Ordinal) ||
                !string.Equals(old.InvocationJson, currentInvJson, StringComparison.Ordinal))
            {
                needsRevalidation = true;
                entries.Add(new(curr.Summary.Id, curr.Summary.Title, CatalogDiffChangeKind.InputChanged, true, "Input schema or invocation definition changed."));
            }
            else if (!string.Equals(old.OutputSchemaJson, curr.OutputSchemaJson, StringComparison.Ordinal))
            {
                needsRevalidation = true;
                entries.Add(new(curr.Summary.Id, curr.Summary.Title, CatalogDiffChangeKind.OutputChanged, true, "Output schema changed."));
            }
            else if (!string.Equals(old.Description, curr.Summary.Description, StringComparison.Ordinal))
            {
                entries.Add(new(curr.Summary.Id, curr.Summary.Title, CatalogDiffChangeKind.DescriptionChanged, false, "Documentation text changed."));
            }
        }

        foreach (var old in previous)
        {
            if (!currMap.ContainsKey(old.Id))
            {
                needsRevalidation = true;
                entries.Add(new(old.Id, old.Title, CatalogDiffChangeKind.Removed, true, "Capability removed in new snapshot."));
            }
        }

        return new CatalogDiffDto(baseSnapshotId, targetSnapshotId, needsRevalidation, entries);
    }

    private async Task MarkDependentDefinitionsNeedsRevalidationAsync(string pluginId, CancellationToken ct)
    {
        var drafts = await _db.SkillDrafts.Where(d => d.PluginId == pluginId && !d.NeedsRevalidation).ToListAsync(ct).ConfigureAwait(false);
        foreach (var d in drafts)
        {
            d.NeedsRevalidation = true;
        }

        var skillIds = await _db.PublishedSkills.AsNoTracking()
            .Where(s => s.PluginId == pluginId)
            .Select(s => s.Id)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        if (skillIds.Length > 0)
        {
            var versions = await _db.PublishedSkillVersions
                .Where(v => skillIds.Contains(v.SkillId) && !v.NeedsRevalidation)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var v in versions)
            {
                v.NeedsRevalidation = true;
            }
        }

        if (_approvalCoordinator is not null)
        {
            await _approvalCoordinator.InvalidatePluginApprovalsAndRunsAsync(
                pluginId,
                $"Plugin '{pluginId}' connection, credential, risk, or catalog changed; pending approvals invalidated.",
                ct).ConfigureAwait(false);
        }
    }

    private async Task<(BusinessAuthKind AuthKind, string? SecretJson)> ResolveDocumentCredentialSecretAsync(
        PluginConnectionEntity connection,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.DocumentCredentialRef))
        {
            return (BusinessAuthKind.None, null);
        }

        var meta = await _db.BusinessCredentialMetas.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CredentialRef == connection.DocumentCredentialRef, ct)
            .ConfigureAwait(false);

        if (meta is null || !Enum.TryParse<BusinessAuthKind>(meta.AuthKind, out var authKind) || authKind == BusinessAuthKind.None)
        {
            return (BusinessAuthKind.None, null);
        }

        if (!_credentialStore.IsPersistentProtectionAvailable)
        {
            return (authKind, null);
        }

        var secretJson = await _credentialStore.ReadSecretAsync(meta.CredentialRef, meta.CipherFileName, ct).ConfigureAwait(false);
        return (authKind, secretJson);
    }

    private async Task<Dictionary<string, BusinessCredentialMetaEntity>> LoadConnectionCredMetasAsync(
        PluginConnectionEntity connection,
        CancellationToken ct)
    {
        var refs = new[] { connection.DocumentCredentialRef, connection.ExecutionCredentialRef }
            .Where(static r => !string.IsNullOrEmpty(r))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (refs.Length == 0)
        {
            return new Dictionary<string, BusinessCredentialMetaEntity>(StringComparer.Ordinal);
        }

        return await _db.BusinessCredentialMetas.AsNoTracking()
            .Where(m => refs.Contains(m.CredentialRef))
            .ToDictionaryAsync(m => m.CredentialRef, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
    }

    private static BusinessAuthKind ResolveEffectiveAuthKind(
        PluginConnectionEntity connection,
        IReadOnlyDictionary<string, BusinessCredentialMetaEntity> metas)
    {
        foreach (var r in new[] { connection.DocumentCredentialRef, connection.ExecutionCredentialRef })
        {
            if (!string.IsNullOrEmpty(r) &&
                metas.TryGetValue(r, out var meta) &&
                Enum.TryParse<BusinessAuthKind>(meta.AuthKind, out var kind) &&
                kind != BusinessAuthKind.None)
            {
                return kind;
            }
        }

        return BusinessAuthKind.None;
    }

    private static EndpointPolicyDto NormalizePolicy(EndpointPolicyDto? incoming, Uri sourceUri)
    {
        var origin = string.IsNullOrWhiteSpace(incoming?.AllowedOrigin)
            ? OutboundTargetPolicyGuard.GetOriginString(sourceUri)
            : incoming.AllowedOrigin.Trim();

        return new EndpointPolicyDto
        {
            AllowedOrigin = origin,
            AllowedPathPrefix = string.IsNullOrWhiteSpace(incoming?.AllowedPathPrefix) ? "/" : incoming.AllowedPathPrefix.Trim(),
            AllowPlainHttpForIntranet = incoming?.AllowPlainHttpForIntranet ?? false,
            AllowExternalOpenApiRefs = incoming?.AllowExternalOpenApiRefs ?? false,
            AllowedExternalRefOrigins = incoming?.AllowedExternalRefOrigins ?? [],
            CustomCaThumbprint = incoming?.CustomCaThumbprint
        };
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

    private static bool IsValidCredentialSlot(string? slot, out string normalized)
    {
        normalized = slot?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "document" or "execution";
    }

    private static BusinessPluginDto MapPluginDto(
        BusinessPluginEntity plugin,
        PluginConnectionEntity? conn,
        IReadOnlyDictionary<string, BusinessCredentialMetaEntity> credMetas,
        int capabilityCount)
    {
        PluginConnectionDto? connDto = null;
        if (conn is not null)
        {
            CredentialRefSummaryDto? docCred = null;
            if (!string.IsNullOrEmpty(conn.DocumentCredentialRef) && credMetas.TryGetValue(conn.DocumentCredentialRef, out var dm))
            {
                docCred = MapCredentialSummary(dm);
            }

            CredentialRefSummaryDto? execCred = null;
            if (!string.IsNullOrEmpty(conn.ExecutionCredentialRef) && credMetas.TryGetValue(conn.ExecutionCredentialRef, out var em))
            {
                execCred = MapCredentialSummary(em);
            }

            connDto = new PluginConnectionDto
            {
                Id = conn.Id,
                SourceUrl = conn.SourceUrl,
                Environment = conn.Environment,
                EndpointPolicy = DeserializePolicy(conn.EndpointPolicyJson, conn.SourceUrl),
                DocumentCredential = docCred,
                ExecutionCredential = execCred,
                Revision = conn.Revision,
                CredentialRevision = conn.CredentialRevision,
                IsEncryptedTransport = conn.IsEncryptedTransport
            };
        }

        return new BusinessPluginDto
        {
            Id = plugin.Id,
            Name = plugin.Name,
            SourceType = Enum.Parse<BusinessPluginSourceType>(plugin.SourceType),
            ConnectionId = plugin.ConnectionId,
            Connection = connDto,
            Enabled = plugin.Enabled,
            ActiveSnapshotId = plugin.ActiveSnapshotId,
            CapabilityCount = capabilityCount,
            LastDiscoveredAtUtc = plugin.LastDiscoveredAtUtc.HasValue
                ? new DateTimeOffset(plugin.LastDiscoveredAtUtc.Value, TimeSpan.Zero)
                : null
        };
    }

    private static CredentialRefSummaryDto MapCredentialSummary(BusinessCredentialMetaEntity meta) => new()
    {
        CredentialRef = meta.CredentialRef,
        AuthKind = Enum.TryParse<BusinessAuthKind>(meta.AuthKind, out var k) ? k : BusinessAuthKind.None,
        MaskedHint = meta.MaskedHint,
        Revision = meta.Revision,
        UpdatedAtUtc = new DateTimeOffset(meta.UpdatedAtUtc, TimeSpan.Zero)
    };

    private static CatalogSnapshotDto MapSnapshotDto(CatalogSnapshotEntity s) => new()
    {
        Id = s.Id,
        PluginId = s.PluginId,
        SourceHash = s.SourceHash,
        SchemaHash = s.SchemaHash,
        FetchedAtUtc = new DateTimeOffset(s.FetchedAtUtc, TimeSpan.Zero),
        ProtocolVersion = s.ProtocolVersion,
        Complete = s.Complete,
        TotalCapabilities = s.TotalCapabilities,
        ExecutableCount = s.ExecutableCount,
        NeedsConfigurationCount = s.NeedsConfigurationCount,
        UnsupportedCount = s.UnsupportedCount,
        Diagnostics = JsonSerializer.Deserialize<string[]>(s.DiagnosticsJson, JsonOptions) ?? []
    };

    private static CapabilitySummaryDto MapCapabilitySummary(CatalogCapabilityEntity c) => new()
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
    };
}
