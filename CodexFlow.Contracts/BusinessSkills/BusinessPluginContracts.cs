// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Serialization;

namespace CodexFlow.Contracts.BusinessSkills;

/// <summary>
/// 业务 Skill 构建器统一错误码常量（设计文档第 11 节冻结清单）。
/// </summary>
public static class BusinessSkillErrorCodes
{
    public const string AuthRequired = nameof(AuthRequired);
    public const string ConnectionUnavailable = nameof(ConnectionUnavailable);
    public const string UnsupportedProtocol = nameof(UnsupportedProtocol);
    public const string UnsupportedSchema = nameof(UnsupportedSchema);
    public const string CatalogIncomplete = nameof(CatalogIncomplete);
    public const string CatalogChanged = nameof(CatalogChanged);
    public const string SelectionViolation = nameof(SelectionViolation);
    public const string ValidationFailed = nameof(ValidationFailed);
    public const string ApprovalRequired = nameof(ApprovalRequired);
    public const string ApprovalExpired = nameof(ApprovalExpired);
    public const string BudgetExceeded = nameof(BudgetExceeded);
    public const string PartialData = nameof(PartialData);
    public const string OutcomeUnknown = nameof(OutcomeUnknown);

    public static IReadOnlyList<string> All { get; } =
    [
        AuthRequired,
        ConnectionUnavailable,
        UnsupportedProtocol,
        UnsupportedSchema,
        CatalogIncomplete,
        CatalogChanged,
        SelectionViolation,
        ValidationFailed,
        ApprovalRequired,
        ApprovalExpired,
        BudgetExceeded,
        PartialData,
        OutcomeUnknown
    ];
}

public sealed record BusinessSkillFieldError(
    string Field,
    string Code,
    string Message);

public sealed record BusinessSkillErrorDto(
    string Code,
    string Message,
    IReadOnlyList<BusinessSkillFieldError> FieldErrors,
    bool Retryable,
    string CorrelationId);

[JsonConverter(typeof(JsonStringEnumConverter<BusinessApiAuthPlane>))]
public enum BusinessApiAuthPlane
{
    ActiveUser = 0,
    ControlPlane = 1
}

public sealed record BusinessApiRouteContract(
    string Method,
    string Template,
    string Purpose,
    BusinessApiAuthPlane AuthPlane,
    bool IsAsyncAccepted = false);

/// <summary>
/// 设计文档第 11 节与现有 CodexFlow.Gateway.Http 核对后的规范路由目录。
/// </summary>
public static class BusinessPluginApiRoutes
{
    public static IReadOnlyList<BusinessApiRouteContract> CanonicalRoutes { get; } =
    [
        new("GET", "/api/plugins", "插件列表与连接状态", BusinessApiAuthPlane.ActiveUser),
        new("POST", "/api/plugins", "创建连接并授权初始目标", BusinessApiAuthPlane.ControlPlane),
        new("PUT", "/api/plugins/{id}/connection", "修改环境、认证引用与目标策略", BusinessApiAuthPlane.ControlPlane),
        new("POST", "/api/plugins/{id}/discover", "对已授权目标刷新描述/工具目录", BusinessApiAuthPlane.ControlPlane, IsAsyncAccepted: true),
        new("GET", "/api/plugins/{id}/capabilities", "搜索、分组、分页能力目录并绑定 snapshotId", BusinessApiAuthPlane.ActiveUser),
        new("POST", "/api/skill-drafts", "创建 Skill 草稿", BusinessApiAuthPlane.ActiveUser),
        new("PUT", "/api/skill-drafts/{id}", "更新业务需求（带 expectedRevision）", BusinessApiAuthPlane.ActiveUser),
        new("PUT", "/api/skill-drafts/{id}/selection", "冻结选择或调整可用能力范围", BusinessApiAuthPlane.ControlPlane),
        new("POST", "/api/skill-drafts/{id}/generate", "生成结构化 Skill 候选", BusinessApiAuthPlane.ControlPlane, IsAsyncAccepted: true),
        new("POST", "/api/skill-drafts/{id}/validate", "执行静态校验", BusinessApiAuthPlane.ActiveUser),
        new("POST", "/api/skill-drafts/{id}/test-runs", "只读或明确授权写入试运行", BusinessApiAuthPlane.ControlPlane, IsAsyncAccepted: true),
        new("POST", "/api/skill-drafts/{id}/publish", "发布不可变 Skill 版本", BusinessApiAuthPlane.ControlPlane),
        new("GET", "/api/skills", "查询已发布 Skill 列表", BusinessApiAuthPlane.ActiveUser),
        new("GET", "/api/skills/{id}", "查询已发布 Skill 详情及版本列表", BusinessApiAuthPlane.ActiveUser),
        new("POST", "/api/skills/{id}/runs", "触发正式运行（带 version/inputs/clientRequestId）", BusinessApiAuthPlane.ControlPlane, IsAsyncAccepted: true),
        new("GET", "/api/skill-runs/{runId}", "查询持久化运行快照与步骤摘要", BusinessApiAuthPlane.ActiveUser),
        new("POST", "/api/skill-runs/{runId}/cancel", "停止后续未发送操作", BusinessApiAuthPlane.ControlPlane),
        new("POST", "/api/gateway/approvals/{approvalId}", "复用现有审批端点提交企业调用批准决策", BusinessApiAuthPlane.ControlPlane),
        new("POST", "/api/skills/{id}/activate-version", "切换/回退后续运行默认版本", BusinessApiAuthPlane.ControlPlane),
        new("POST", "/api/plugins/{id}/disable", "停用连接及阻止后续派发", BusinessApiAuthPlane.ControlPlane),
        new("GET", "/api/plugin-operations/{id}", "查询异步发现或生成任务状态", BusinessApiAuthPlane.ActiveUser)
    ];
}

public sealed record CredentialRefSummaryDto
{
    public string CredentialRef { get; init; } = string.Empty;
    public BusinessAuthKind AuthKind { get; init; }
    public string MaskedHint { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record EndpointPolicyDto
{
    public string AllowedOrigin { get; init; } = string.Empty;
    public string AllowedPathPrefix { get; init; } = "/";
    public bool AllowPlainHttpForIntranet { get; init; }
    public bool AllowExternalOpenApiRefs { get; init; }
    public IReadOnlyList<string> AllowedExternalRefOrigins { get; init; } = [];
    public string? CustomCaThumbprint { get; init; }
}

public sealed record PluginConnectionDto
{
    public string Id { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string Environment { get; init; } = "default";
    public EndpointPolicyDto EndpointPolicy { get; init; } = new();
    public CredentialRefSummaryDto? DocumentCredential { get; init; }
    public CredentialRefSummaryDto? ExecutionCredential { get; init; }
    public int Revision { get; init; } = 1;
    public string CredentialRevision { get; init; } = string.Empty;
    public bool IsEncryptedTransport { get; init; } = true;
}

public sealed record BusinessPluginDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public BusinessPluginSourceType SourceType { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public PluginConnectionDto? Connection { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ActiveSnapshotId { get; init; }
    public int CapabilityCount { get; init; }
    public DateTimeOffset? LastDiscoveredAtUtc { get; init; }
}

public sealed record CatalogSnapshotDto
{
    public string Id { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string SchemaHash { get; init; } = string.Empty;
    public DateTimeOffset FetchedAtUtc { get; init; }
    public string ProtocolVersion { get; init; } = string.Empty;
    public bool Complete { get; init; }
    public int TotalCapabilities { get; init; }
    public int ExecutableCount { get; init; }
    public int NeedsConfigurationCount { get; init; }
    public int UnsupportedCount { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<CatalogDiffChangeKind>))]
public enum CatalogDiffChangeKind
{
    Added = 0,
    Removed = 1,
    InputChanged = 2,
    OutputChanged = 3,
    TargetOrAuthChanged = 4,
    DescriptionChanged = 5
}

public sealed record CatalogDiffEntryDto(
    string CapabilityId,
    string Title,
    CatalogDiffChangeKind ChangeKind,
    bool RequiresRevalidation,
    string Summary);

public sealed record CatalogDiffDto(
    string BaseSnapshotId,
    string TargetSnapshotId,
    bool TriggersNeedsRevalidation,
    IReadOnlyList<CatalogDiffEntryDto> Entries);

public sealed record CapabilityParameterSpecDto
{
    public string Name { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty; // path, query, header, body
    public string SchemaType { get; init; } = "string";
    public bool Required { get; init; }
    public string? Style { get; init; }
    public bool? Explode { get; init; }
    public string? Description { get; init; }
}

public sealed record CapabilitySecuritySchemeRequirementDto(
    string SchemeId,
    string SchemeType, // ApiKey | Http | OAuth2 | OpenIdConnect
    string? InLocation, // header | query | cookie
    string? HeaderOrParamName,
    string? HttpScheme, // bearer | basic
    IReadOnlyList<string> Scopes);

public sealed record CapabilitySecurityRequirementDto(
    IReadOnlyList<CapabilitySecuritySchemeRequirementDto> Schemes);

public sealed record CapabilityInvocationSpecDto
{
    public BusinessPluginSourceType SourceType { get; init; }
    public string? HttpMethod { get; init; }
    public string? NormalizedPath { get; init; }
    public string? McpToolName { get; init; }
    public string? SelectedServerUrl { get; init; }
    public IReadOnlyList<CapabilityParameterSpecDto> Parameters { get; init; } = [];
    public string? RequestContentType { get; init; }
    public string? ResponseContentType { get; init; }
    public IReadOnlyList<string> AuthSchemeRefs { get; init; } = [];
    public IReadOnlyList<CapabilitySecurityRequirementDto> SecurityAlternatives { get; init; } = [];
    public string? RequestBodySchemaJson { get; init; }
    public string? ResponseSchemaJson { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ServerVariableAllowedValues { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

public sealed record CapabilitySummaryDto
{
    public string Id { get; init; } = string.Empty;
    public string SnapshotId { get; init; } = string.Empty;
    public string NativeIdentity { get; init; } = string.Empty;
    public string ModelAlias { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool SourceDescriptionMissing { get; init; }
    public string GroupTag { get; init; } = "default";
    public CapabilityRiskLevel Risk { get; init; } = CapabilityRiskLevel.Unknown;
    public CapabilityRiskLevel? SuggestedSourceRiskHint { get; init; }
    public CapabilityExecutabilityStatus Executability { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record CapabilityDetailDto
{
    public CapabilitySummaryDto Summary { get; init; } = new();
    public string? InputSchemaJson { get; init; }
    public string? OutputSchemaJson { get; init; }
    public CapabilityInvocationSpecDto Invocation { get; init; } = new();
}

public sealed record CapabilitySelectionDto
{
    public string DraftId { get; init; } = string.Empty;
    public string CatalogSnapshotId { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedCapabilityIds { get; init; } = [];
    public int Revision { get; init; } = 1;
}

public sealed record PluginOperationAcceptedDto(
    string OperationId,
    string Kind,
    PluginOperationStatus Status,
    long Sequence,
    int Version,
    bool ReusedFromClientRequestId);

public sealed record PluginOperationStatusDto(
    string OperationId,
    string Kind,
    PluginOperationStatus Status,
    long Sequence,
    int Version,
    string? TargetId,
    string? SnapshotId,
    string? ResultMessage,
    BusinessSkillErrorDto? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
