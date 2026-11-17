// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;

namespace CodexFlow.Contracts.BusinessSkills;

/// <summary>
/// P2 统一受控能力调用请求模型（OpenAPI 与 MCP 共用）。
/// </summary>
public sealed record CapabilityExecutionRequest
{
    public string PluginId { get; init; } = string.Empty;
    public string SnapshotId { get; init; } = string.Empty;
    public CapabilityDetailDto Capability { get; init; } = new();
    public CapabilityRiskLevel EffectiveRisk { get; init; } = CapabilityRiskLevel.Unknown;
    public EndpointPolicyDto EndpointPolicy { get; init; } = new();
    public string SourceUrl { get; init; } = string.Empty;
    public BusinessAuthKind AuthKind { get; init; } = BusinessAuthKind.None;
    public string? SecretPayloadJson { get; init; }
    public string? SelectedServerUrl { get; init; }
    public IReadOnlyDictionary<string, string> ServerVariables { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public JsonElement? RequestBodyJson { get; init; }
    public SkillPaginationConfig? Pagination { get; init; }
    public SkillResponseContract? ResponseContract { get; init; }
    public int RemainingRequestBudget { get; init; } = BusinessSkillDefaults.MaxExternalRequestsPerRun;
    public int RemainingRecordBudget { get; init; } = BusinessSkillDefaults.MaxReadRecordsPerRun;
    public int MaxReadOnlyRetries { get; init; } = BusinessSkillDefaults.MaxReadOnlyRetries;
    public int RequestTimeoutSeconds { get; init; } = BusinessSkillDefaults.RequestTimeoutSeconds;
}

/// <summary>
/// P2 统一受控能力调用响应模型（明确区分 PartialData、OutcomeUnknown、IsToolError 与预算耗用）。
/// </summary>
public sealed record CapabilityExecutionResult
{
    public bool Success { get; init; }
    public bool PartialData { get; init; }
    public bool OutcomeUnknown { get; init; }
    public bool IsToolError { get; init; }
    public int HttpStatusCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonElement? PayloadJson { get; init; }
    public string? RawText { get; init; }
    public string? CreatedId { get; init; }
    public IReadOnlyList<JsonElement> CollectedItems { get; init; } = [];
    public int RequestsConsumed { get; init; }
    public int RetriesConsumed { get; init; }
    public int PagesFetched { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
