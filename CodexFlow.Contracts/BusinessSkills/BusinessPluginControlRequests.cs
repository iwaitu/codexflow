// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts.BusinessSkills;

public sealed record CreateBusinessPluginRequest(
    string Name,
    BusinessPluginSourceType SourceType,
    string SourceUrl,
    string Environment,
    EndpointPolicyDto EndpointPolicy);

public sealed record UpdatePluginConnectionRequest(
    int ExpectedRevision,
    string SourceUrl,
    string Environment,
    EndpointPolicyDto EndpointPolicy);

public sealed record SetPluginCredentialRequest(
    BusinessAuthKind AuthKind,
    string SecretPayload);

public sealed record DeleteCredentialResponseDto(
    bool Deleted,
    string CredentialRef,
    int RemainingReferenceCount,
    IReadOnlyList<string> ReferencedPluginIds);

public sealed record DiscoverPluginRequest(
    string ClientRequestId);

public sealed record DiscoverPluginResultDto(
    PluginOperationAcceptedDto Operation,
    CatalogSnapshotDto? Snapshot,
    CatalogDiffDto? Diff);

public sealed record UpdateCapabilityRiskRequest(
    string CapabilityId,
    CapabilityRiskLevel Risk,
    string? Note = null);

public sealed record CapabilityPageResponseDto(
    string PluginId,
    string SnapshotId,
    bool Complete,
    int TotalCount,
    int FilteredCount,
    int ExecutableCount,
    int NeedsConfigurationCount,
    int UnsupportedCount,
    int Page,
    int PageSize,
    IReadOnlyList<CapabilitySummaryDto> Items);

public sealed record DisablePluginResponseDto(
    string PluginId,
    bool Enabled,
    DateTimeOffset? DisabledAtUtc);

public sealed record BusinessSkillRetentionPruneRequest(
    int? RunHistoryRetentionDays = null,
    int? DiagnosticsRetentionDays = null,
    int? OperationHistoryRetentionDays = null);

public sealed record BusinessSkillRetentionPruneResultDto(
    int TrimmedStepDiagnosticsCount,
    int DeletedTerminalRunsCount,
    int DeletedOrphanSnapshotsCount,
    int DeletedOperationsCount,
    int RetainedOutcomeUnknownRunsCount,
    int RetainedProtectedSnapshotsCount,
    DateTimeOffset ExecutedAtUtc);
