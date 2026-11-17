// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.BusinessSkills;

public class BusinessPluginEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceType { get; set; } = "OpenApi";
    public string ConnectionId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? ActiveSnapshotId { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public DateTime? LastDiscoveredAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PluginConnectionEntity
{
    public string Id { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Environment { get; set; } = "default";
    public string EndpointPolicyJson { get; set; } = "{}";
    public string? DocumentCredentialRef { get; set; }
    public string? ExecutionCredentialRef { get; set; }
    public int Revision { get; set; } = 1;
    public string CredentialRevision { get; set; } = string.Empty;
    public bool IsEncryptedTransport { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 凭据元数据表：仅保存凭据引用、脱敏掩码、轮换 Revision 与本地加密文件名，绝不存储明文密钥。
/// </summary>
public class BusinessCredentialMetaEntity
{
    public string CredentialRef { get; set; } = string.Empty;
    public string AuthKind { get; set; } = "None";
    public string MaskedHint { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string CipherFileName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CatalogSnapshotEntity
{
    public string Id { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public int ConnectionRevision { get; set; }
    public string CredentialRevision { get; set; } = string.Empty;
    public string Environment { get; set; } = "default";
    public string ProtocolVersion { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string SchemaHash { get; set; } = string.Empty;
    public bool Complete { get; set; }
    public int TotalCapabilities { get; set; }
    public int ExecutableCount { get; set; }
    public int NeedsConfigurationCount { get; set; }
    public int UnsupportedCount { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CatalogCapabilityEntity
{
    public string SnapshotId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string NativeIdentity { get; set; } = string.Empty;
    public string ModelAlias { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool SourceDescriptionMissing { get; set; }
    public string GroupTag { get; set; } = "default";
    public string Risk { get; set; } = "Unknown";
    public string? SuggestedSourceRiskHint { get; set; }
    public string Executability { get; set; } = "Executable";
    public string DiagnosticsJson { get; set; } = "[]";
    public string? InputSchemaJson { get; set; }
    public string? OutputSchemaJson { get; set; }
    public string InvocationJson { get; set; } = "{}";
}

public class PluginOperationEntity
{
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string ClientRequestId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public long Sequence { get; set; } = 1;
    public int Version { get; set; } = 1;
    public string? SnapshotId { get; set; }
    public string? ResultMessage { get; set; }
    public string? ErrorJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class SkillDraftEntity
{
    public string Id { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string CatalogSnapshotId { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string BusinessGoal { get; set; } = string.Empty;
    public string SelectedCapabilityIdsJson { get; set; } = "[]";
    public string? CandidateDefinitionJson { get; set; }
    public string ClarificationQuestionsJson { get; set; } = "[]";
    public string ValidationErrorsJson { get; set; } = "[]";
    public bool NeedsRevalidation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PublishedSkillEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string Environment { get; set; } = "default";
    public int ActiveVersion { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PublishedSkillVersionEntity
{
    public string SkillId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string CatalogSnapshotId { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
    public bool NeedsRevalidation { get; set; }
    public bool WriteStepVerified { get; set; }
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
}

public class SkillRunEntity
{
    public string RunId { get; set; } = string.Empty;
    public string SkillId { get; set; } = string.Empty;
    public int SkillVersion { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public bool StopRequested { get; set; }
    public int ExternalRequestCount { get; set; }
    public int ReadOnlyRetryCount { get; set; }
    public string InputsJson { get; set; } = "{}";
    public string? OutputPayloadJson { get; set; }
    public string? PendingApprovalJson { get; set; }
    public string? ErrorJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class SkillRunStepEntity
{
    public string RunId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public string State { get; set; } = "Prepared";
    public string? RequestSummary { get; set; }
    public string? ResponseSummary { get; set; }
    public string? Evidence { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}
