// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using Microsoft.Extensions.Logging;

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
    private static BusinessSkillJsonContext Json => BusinessSkillJsonContext.Default;

    private readonly IBusinessPluginStore _store;
    private readonly IBusinessCredentialStore _credentialStore;
    private readonly OutboundTargetPolicyGuard _targetGuard;
    private readonly OpenApiBusinessPluginAdapter _openApiAdapter;
    private readonly McpBusinessPluginAdapter _mcpAdapter;
    private readonly IBusinessSkillApprovalCoordinator? _approvalCoordinator;
    private readonly ILogger<BusinessPluginControlService>? _logger;

    public BusinessPluginControlService(
        IBusinessPluginStore store,
        IBusinessCredentialStore credentialStore,
        OutboundTargetPolicyGuard targetGuard,
        OpenApiBusinessPluginAdapter? openApiAdapter = null,
        McpBusinessPluginAdapter? mcpAdapter = null,
        IBusinessSkillApprovalCoordinator? approvalCoordinator = null,
        ILogger<BusinessPluginControlService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(targetGuard);
        _store = store;
        _credentialStore = credentialStore;
        _targetGuard = targetGuard;
        _openApiAdapter = openApiAdapter ?? new OpenApiBusinessPluginAdapter(targetGuard);
        _mcpAdapter = mcpAdapter ?? new McpBusinessPluginAdapter(targetGuard);
        _approvalCoordinator = approvalCoordinator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BusinessPluginDto>> ListPluginsAsync(CancellationToken ct = default)
    {
        var (plugins, connections, credMetas, capabilityCounts) = await _store.LoadDirectoryAsync(ct).ConfigureAwait(false);
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
            EndpointPolicyJson = JsonSerializer.Serialize(policy, Json.EndpointPolicyDto),
            Revision = 1,
            CredentialRevision = "none",
            IsEncryptedTransport = eval.IsEncryptedTransport,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _store.CreatePluginAsync(plugin, connection, ct).ConfigureAwait(false);

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

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _store.FindConnectionAsync(plugin.ConnectionId, ct).ConfigureAwait(false);
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
        connection.EndpointPolicyJson = JsonSerializer.Serialize(policy, Json.EndpointPolicyDto);
        connection.IsEncryptedTransport = eval.IsEncryptedTransport;
        connection.Revision += 1;
        connection.UpdatedAtUtc = DateTime.UtcNow;
        plugin.UpdatedAtUtc = connection.UpdatedAtUtc;

        var saved = await _store.TrySaveConnectionAsync(plugin, connection, request.ExpectedRevision, null, null, ct).ConfigureAwait(false);
        if (!saved.Updated)
        {
            return BusinessServiceResult<BusinessPluginDto>.Fail(
                409,
                BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent connection update detected; reload latest connection state.");
        }

        await InvalidateApprovalsAsync(pluginId, ct).ConfigureAwait(false);

        var capCount = plugin.ActiveSnapshotId is null
            ? 0
            : await _store.CountCapabilitiesAsync(plugin.ActiveSnapshotId, ct).ConfigureAwait(false);

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

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _store.FindConnectionAsync(plugin.ConnectionId, ct).ConfigureAwait(false);
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
        var previous = await _store.FindCredentialAsync(credentialRef, ct).ConfigureAwait(false);
        var previousRevision = connection.Revision;
        var newCredentialRevision = Guid.NewGuid().ToString("N")[..12];
        var stored = await _credentialStore.StoreSecretAsync(
            credentialRef,
            request.AuthKind,
            request.SecretPayload ?? string.Empty,
            newCredentialRevision,
            ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var meta = new BusinessCredentialMetaEntity
        {
            CredentialRef = credentialRef,
            AuthKind = request.AuthKind.ToString(),
            MaskedHint = stored.Summary.MaskedHint,
            Revision = newCredentialRevision,
            CipherFileName = stored.CipherFileName,
            CreatedAtUtc = previous?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        };

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

        var saved = await _store.TrySaveConnectionAsync(plugin, connection, previousRevision, meta, null, ct).ConfigureAwait(false);
        if (!saved.Updated)
        {
            await DeleteSupersededCipherAsync(credentialRef, meta.CipherFileName, previous?.CipherFileName).ConfigureAwait(false);
            return BusinessServiceResult<CredentialRefSummaryDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent connection update detected; reload latest connection state.");
        }

        await DeleteSupersededCipherAsync(credentialRef, previous?.CipherFileName, meta.CipherFileName).ConfigureAwait(false);
        await InvalidateApprovalsAsync(pluginId, ct).ConfigureAwait(false);
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

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var connection = await _store.FindConnectionAsync(plugin.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Connection for plugin '{pluginId}' not found.");
        }

        var credRef = normalizedSlot == "document" ? connection.DocumentCredentialRef : connection.ExecutionCredentialRef;
        if (string.IsNullOrEmpty(credRef))
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(404, BusinessSkillErrorCodes.AuthRequired, $"No credential bound to '{normalizedSlot}' slot.");
        }

        var referencedByOthers = await _store.FindOtherCredentialUsersAsync(pluginId, credRef, ct).ConfigureAwait(false);

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

        var previousRevision = connection.Revision;
        connection.CredentialRevision = Guid.NewGuid().ToString("N")[..12];
        connection.Revision += 1;
        connection.UpdatedAtUtc = DateTime.UtcNow;
        var meta = await _store.FindCredentialAsync(credRef, ct).ConfigureAwait(false);
        var remaining = referencedByOthers.Count + (otherSlotStillUsesSameRef ? 1 : 0);
        var saved = await _store.TrySaveConnectionAsync(
            plugin, connection, previousRevision, null, remaining == 0 ? credRef : null, ct).ConfigureAwait(false);
        if (!saved.Updated)
        {
            return BusinessServiceResult<DeleteCredentialResponseDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                "Concurrent connection update detected; reload latest connection state.");
        }

        if (saved.CredentialRemoved && meta is not null)
        {
            await DeleteSupersededCipherAsync(credRef, meta.CipherFileName, null).ConfigureAwait(false);
        }

        await InvalidateApprovalsAsync(pluginId, ct).ConfigureAwait(false);
        return BusinessServiceResult<DeleteCredentialResponseDto>.Ok(
            new DeleteCredentialResponseDto(true, credRef, remaining, referencedByOthers));
    }

    public async Task<BusinessServiceResult<DisablePluginResponseDto>> DisablePluginAsync(
        string pluginId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (!await _store.DisablePluginAsync(pluginId, now, ct).ConfigureAwait(false))
        {
            return BusinessServiceResult<DisablePluginResponseDto>.Fail(
                404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        if (_approvalCoordinator is not null)
        {
            await _approvalCoordinator.InvalidatePluginApprovalsAndRunsAsync(
                pluginId,
                $"Plugin '{pluginId}' was disabled; subsequent dispatches and pending approvals cancelled.",
                ct).ConfigureAwait(false);
        }

        return BusinessServiceResult<DisablePluginResponseDto>.Ok(
            new DisablePluginResponseDto(pluginId, false, new DateTimeOffset(now, TimeSpan.Zero)));
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
        var existingOp = await _store.FindRequestAsync("discover", pluginId, normalizedClientRequestId, ct).ConfigureAwait(false);
        if (existingOp is not null)
        {
            return await ReplayDiscoveryAsync(existingOp, ct).ConfigureAwait(false);
        }

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        if (!plugin.Enabled)
        {
            return BusinessServiceResult<DiscoverPluginResultDto>.Fail(422, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' is disabled.");
        }

        var connection = await _store.FindConnectionAsync(plugin.ConnectionId, ct).ConfigureAwait(false);
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
        var replay = await _store.TryRegisterOperationAsync(opEntity, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            return await ReplayDiscoveryAsync(replay, ct).ConfigureAwait(false);
        }

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
            opEntity.ErrorJson = JsonSerializer.Serialize(effectiveErr, Json.BusinessSkillErrorDto);
            opEntity.UpdatedAtUtc = DateTime.UtcNow;
            await _store.SaveOperationResultAsync(opEntity, ct).ConfigureAwait(false);

            var statusCode = effectiveErr.Code switch
            {
                BusinessSkillErrorCodes.AuthRequired => 401,
                BusinessSkillErrorCodes.UnsupportedProtocol or BusinessSkillErrorCodes.UnsupportedSchema or BusinessSkillErrorCodes.SelectionViolation => 422,
                _ => 502
            };
            return new BusinessServiceResult<DiscoverPluginResultDto>(false, statusCode, null, effectiveErr);
        }

        var previousSnapshotId = plugin.ActiveSnapshotId;
        var diff = string.IsNullOrEmpty(previousSnapshotId)
            ? null
            : ComputeCatalogDiff(previousSnapshotId, snapshotId,
                await _store.ReadCapabilitiesAsync(previousSnapshotId, ct).ConfigureAwait(false), discoveredCapabilities);

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
            DiagnosticsJson = JsonSerializer.Serialize(newSnapshotDto.Diagnostics, Json.StringList),
            FetchedAtUtc = newSnapshotDto.FetchedAtUtc.UtcDateTime
        };

        var capabilityRows = discoveredCapabilities.Select(cap => new CatalogCapabilityEntity
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
                DiagnosticsJson = JsonSerializer.Serialize(cap.Summary.Diagnostics, Json.StringList),
                InputSchemaJson = cap.InputSchemaJson,
                OutputSchemaJson = cap.OutputSchemaJson,
                InvocationJson = JsonSerializer.Serialize(cap.Invocation, Json.CapabilityInvocationSpecDto)
        }).ToArray();

        plugin.ActiveSnapshotId = snapshotId;
        plugin.LastDiscoveredAtUtc = newSnapshotDto.FetchedAtUtc.UtcDateTime;
        plugin.UpdatedAtUtc = DateTime.UtcNow;

        opEntity.Status = PluginOperationStatus.Succeeded.ToString();
        opEntity.Sequence = 2;
        opEntity.SnapshotId = snapshotId;
        opEntity.ResultMessage = $"Discovered {discoveredCapabilities.Count} capabilities.";
        opEntity.UpdatedAtUtc = DateTime.UtcNow;

        var needsRevalidation = diff is { TriggersNeedsRevalidation: true };
        if (!await _store.TryActivateCatalogAsync(plugin, previousSnapshotId, snapEntity, capabilityRows, opEntity, needsRevalidation, ct)
            .ConfigureAwait(false))
        {
            var conflict = new BusinessSkillErrorDto(BusinessSkillErrorCodes.CatalogChanged,
                "The plugin catalog or connection changed during discovery. Discover again with a new client request id.", [],
                true, operationId);
            opEntity.Status = PluginOperationStatus.Failed.ToString();
            opEntity.SnapshotId = null;
            opEntity.ResultMessage = conflict.Message;
            opEntity.ErrorJson = JsonSerializer.Serialize(conflict, Json.BusinessSkillErrorDto);
            await _store.SaveOperationResultAsync(opEntity, ct).ConfigureAwait(false);
            return new BusinessServiceResult<DiscoverPluginResultDto>(false, 409, null, conflict);
        }

        if (needsRevalidation)
        {
            await InvalidateApprovalsAsync(plugin.Id, ct).ConfigureAwait(false);
        }

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
        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var targetSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? plugin.ActiveSnapshotId : snapshotId.Trim();
        if (string.IsNullOrWhiteSpace(targetSnapshotId))
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Plugin has no catalog snapshot yet.");
        }

        var snap = await _store.FindSnapshotAsync(targetSnapshotId, pluginId, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<CapabilityPageResponseDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{targetSnapshotId}' not found for plugin '{pluginId}'.");
        }

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var capabilityPage = await _store.QueryCapabilitiesAsync(
            targetSnapshotId, string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim(), executableOnly,
            safePage, safePageSize, ct).ConfigureAwait(false);
        var items = capabilityPage.Capabilities.Select(MapCapabilitySummary).ToArray();
        return BusinessServiceResult<CapabilityPageResponseDto>.Ok(new CapabilityPageResponseDto(
            pluginId,
            snap.Id,
            snap.Complete,
            snap.TotalCapabilities,
            capabilityPage.FilteredCount,
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

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin?.ActiveSnapshotId is null)
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Active catalog snapshot not found for plugin '{pluginId}'.");
        }

        var cap = await _store.FindCapabilityAsync(plugin.ActiveSnapshotId, capabilityId, ct)
            .ConfigureAwait(false);
        if (cap is null)
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Capability '{capabilityId}' not found in active snapshot.");
        }

        cap.Risk = request.Risk.ToString();
        if (!await _store.TryUpdateCapabilityRiskAsync(pluginId, plugin.ActiveSnapshotId, capabilityId, cap.Risk, ct)
            .ConfigureAwait(false))
        {
            return BusinessServiceResult<CapabilitySummaryDto>.Fail(409, BusinessSkillErrorCodes.CatalogChanged,
                "Active catalog snapshot changed; reload the capability.");
        }

        await InvalidateApprovalsAsync(pluginId, ct).ConfigureAwait(false);

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

        var plugin = await _store.FindPluginAsync(pluginId, ct).ConfigureAwait(false);
        if (plugin is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.ConnectionUnavailable, $"Plugin '{pluginId}' not found.");
        }

        var targetSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? plugin.ActiveSnapshotId : snapshotId.Trim();
        if (string.IsNullOrWhiteSpace(targetSnapshotId))
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, "Plugin has no catalog snapshot yet.");
        }

        var snap = await _store.FindSnapshotAsync(targetSnapshotId, pluginId, ct)
            .ConfigureAwait(false);
        if (snap is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.CatalogIncomplete, $"Snapshot '{targetSnapshotId}' not found for plugin '{pluginId}'.");
        }

        var cap = await _store.FindCapabilityAsync(targetSnapshotId, capabilityId.Trim(), ct)
            .ConfigureAwait(false);
        if (cap is null)
        {
            return BusinessServiceResult<CapabilityDetailDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Capability '{capabilityId}' not found in snapshot '{targetSnapshotId}'.");
        }

        var invocation = JsonSerializer.Deserialize(cap.InvocationJson, Json.CapabilityInvocationSpecDto)
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
        var op = await _store.FindOperationAsync(operationId, ct)
            .ConfigureAwait(false);
        if (op is null)
        {
            return BusinessServiceResult<PluginOperationStatusDto>.Fail(404, BusinessSkillErrorCodes.ValidationFailed, $"Operation '{operationId}' not found.");
        }

        BusinessSkillErrorDto? err = null;
        if (!string.IsNullOrEmpty(op.ErrorJson))
        {
            err = JsonSerializer.Deserialize(op.ErrorJson, Json.BusinessSkillErrorDto);
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

            var currentInvJson = JsonSerializer.Serialize(curr.Invocation, Json.CapabilityInvocationSpecDto);
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

    private Task InvalidateApprovalsAsync(string pluginId, CancellationToken ct)
        => _approvalCoordinator?.InvalidatePluginApprovalsAndRunsAsync(
            pluginId, $"Plugin '{pluginId}' connection, credential, risk, or catalog changed; pending approvals invalidated.", ct)
            ?? Task.CompletedTask;

    private async Task DeleteSupersededCipherAsync(string credentialRef, string? candidate, string? retained)
    {
        if (candidate is null || string.Equals(candidate, retained, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _credentialStore.DeleteSecretAsync(credentialRef, candidate, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not remove superseded encrypted credential for {CredentialRef}.", credentialRef);
        }
    }

    private async Task<BusinessServiceResult<DiscoverPluginResultDto>> ReplayDiscoveryAsync(
        PluginOperationEntity operation, CancellationToken ct)
    {
        var accepted = new PluginOperationAcceptedDto(operation.OperationId, operation.Kind,
            Enum.TryParse<PluginOperationStatus>(operation.Status, out var status) ? status : PluginOperationStatus.Succeeded,
            operation.Sequence, operation.Version, ReusedFromClientRequestId: true);
        var snapshot = string.IsNullOrEmpty(operation.SnapshotId)
            ? null
            : await _store.FindSnapshotAsync(operation.SnapshotId, null, ct).ConfigureAwait(false);

        return BusinessServiceResult<DiscoverPluginResultDto>.Ok(
            new DiscoverPluginResultDto(accepted, snapshot is null ? null : MapSnapshotDto(snapshot), null), 202);
    }

    private async Task<(BusinessAuthKind AuthKind, string? SecretJson)> ResolveDocumentCredentialSecretAsync(
        PluginConnectionEntity connection,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.DocumentCredentialRef))
        {
            return (BusinessAuthKind.None, null);
        }

        var meta = await _store.FindCredentialAsync(connection.DocumentCredentialRef, ct)
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

        var credentials = new Dictionary<string, BusinessCredentialMetaEntity>(StringComparer.Ordinal);
        foreach (var credentialRef in refs)
        {
            var credential = await _store.FindCredentialAsync(credentialRef, ct).ConfigureAwait(false);
            if (credential is not null)
            {
                credentials.Add(credentialRef, credential);
            }
        }

        return credentials;
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
        var parsed = JsonSerializer.Deserialize(json, Json.EndpointPolicyDto);
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
        Diagnostics = JsonSerializer.Deserialize(s.DiagnosticsJson, Json.StringArray) ?? []
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
        Diagnostics = JsonSerializer.Deserialize(c.DiagnosticsJson, Json.StringArray) ?? []
    };
}
