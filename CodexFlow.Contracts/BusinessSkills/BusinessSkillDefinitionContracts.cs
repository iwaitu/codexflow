// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Contracts.BusinessSkills;

/// <summary>
/// Skill JSON v1 —— 唯一机器可执行定义（设计文档第 7.2 节）。
/// SKILL.md 由同版本定义生成只读说明，不作为独立第二真相。
/// </summary>
public sealed record SkillDefinitionV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SkillId { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public SkillConnectionBinding ConnectionBinding { get; init; } = new();
    public string CatalogSnapshotId { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedCapabilityIds { get; init; } = [];
    public IReadOnlyList<string> UsedCapabilityIds { get; init; } = [];
    public SkillInputSchemaDto Inputs { get; init; } = new();
    public SkillOutputSchemaDto Outputs { get; init; } = new();
    public IReadOnlyList<SkillStepDefinition> Steps { get; init; } = [];
    public SkillSuccessCriteriaDto SuccessCriteria { get; init; } = new();
    public SkillDataPolicyDto DataPolicy { get; init; } = new();
    public SkillBudgetPolicyDto Budgets { get; init; } = new();
    public SkillValidationEvidenceDto ValidationEvidence { get; init; } = new();
    public string ContentHash { get; init; } = string.Empty;
}

public sealed record SkillConnectionBinding
{
    public string ConnectionId { get; init; } = string.Empty;
    public string Environment { get; init; } = "default";
    public int ConnectionRevision { get; init; } = 1;
    public string CredentialRevision { get; init; } = string.Empty;
    public string SelectedServerUrl { get; init; } = string.Empty;
}

public sealed record SkillInputSchemaDto
{
    public string Type { get; init; } = "object";
    public IReadOnlyList<string> Required { get; init; } = [];
    public IReadOnlyDictionary<string, SkillInputPropertyDto> Properties { get; init; }
        = new Dictionary<string, SkillInputPropertyDto>(StringComparer.Ordinal);
}

public sealed record SkillInputPropertyDto
{
    public string Type { get; init; } = "string";
    public string? Format { get; init; }
    public string? Description { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
}

public sealed record SkillOutputSchemaDto
{
    public string Type { get; init; } = "object";
    public IReadOnlyList<SkillOutputFieldDto> Fields { get; init; } = [];
}

public sealed record SkillOutputFieldDto(
    string Name,
    string Type,
    string? Description = null);

public sealed record SkillSuccessCriteriaDto
{
    public IReadOnlyList<string> RequiredSteps { get; init; } = [];
    public bool RequireCompletePagination { get; init; } = true;
    public bool FailOnPartialData { get; init; } = true;
}

public sealed record SkillDataPolicyDto
{
    public IReadOnlyList<string> AllowedModelFields { get; init; } = [];
    public bool IncludeSanitizedSamplesForGeneration { get; init; }
    public bool RedactLogsAndExports { get; init; } = true;
}

public sealed record SkillBudgetPolicyDto
{
    public int MaxExternalRequests { get; init; } = BusinessSkillDefaults.MaxExternalRequestsPerRun;
    public int MaxReadRecords { get; init; } = BusinessSkillDefaults.MaxReadRecordsPerRun;
    public int MaxJoinOutputRows { get; init; } = BusinessSkillDefaults.MaxJoinOutputRows;
    public int MaxWriteBatchItems { get; init; } = BusinessSkillDefaults.MaxWriteBatchItems;
    public int MaxReadOnlyRetries { get; init; } = BusinessSkillDefaults.MaxReadOnlyRetries;
    public int RequestTimeoutSeconds { get; init; } = BusinessSkillDefaults.RequestTimeoutSeconds;
}

public sealed record SkillValidationEvidenceDto
{
    public int DraftRevision { get; init; }
    public string CatalogHash { get; init; } = string.Empty;
    public string StaticValidationHash { get; init; } = string.Empty;
    public string? ReadOnlyTestRunId { get; init; }
    public string? ReadOnlyTestRunEvidenceHash { get; init; }
    public bool WriteStepVerified { get; init; }
    public DateTimeOffset? VerifiedAtUtc { get; init; }
}

/// <summary>
/// 结构化值引用：允许常量、{ input }、{ step, pointer } 或循环内的 { item, pointer }。
/// </summary>
public sealed record SkillValueBinding
{
    public string? Input { get; init; }
    public string? Step { get; init; }
    public string? Item { get; init; }
    public string? Pointer { get; init; }
    public JsonElement? Literal { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SkillToolStep), typeDiscriminator: "tool")]
[JsonDerivedType(typeof(SkillTransformStep), typeDiscriminator: "transform")]
[JsonDerivedType(typeof(SkillBranchStep), typeDiscriminator: "branch")]
[JsonDerivedType(typeof(SkillForeachStep), typeDiscriminator: "foreach")]
[JsonDerivedType(typeof(SkillGenerateTextStep), typeDiscriminator: "generateText")]
[JsonDerivedType(typeof(SkillApprovalStep), typeDiscriminator: "approval")]
[JsonDerivedType(typeof(SkillOutputStep), typeDiscriminator: "output")]
public abstract record SkillStepDefinition
{
    public string Id { get; init; } = string.Empty;
    public string? Title { get; init; }
}

public sealed record SkillPaginationConfig
{
    public string Mode { get; init; } = "page"; // page | cursor
    public string? PageParam { get; init; }
    public string? PageSizeParam { get; init; }
    public int PageSize { get; init; } = 100;
    public string? CursorParam { get; init; }
    public string? NextCursorPointer { get; init; }
    public string? HasMorePointer { get; init; }
    public string? TotalCountPointer { get; init; }
    public string ItemsPointer { get; init; } = "/items";
    public int MaxPages { get; init; } = 50;
}

public sealed record SkillResponseContract
{
    public string? BusinessCodePointer { get; init; }
    public IReadOnlyList<string> SuccessCodeValues { get; init; } = [];
    public string? CreatedIdPointer { get; init; }
    public string? IdempotencyKeyTemplate { get; init; }
    public string? StatusCheckCapabilityId { get; init; }
}

public sealed record SkillToolStep : SkillStepDefinition
{
    public string CapabilityId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, SkillValueBinding> Arguments { get; init; }
        = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal);
    public SkillPaginationConfig? Pagination { get; init; }
    public SkillResponseContract? ResponseContract { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillTransformOperation
{
    Filter = 0,
    Sort = 1,
    Project = 2,
    Aggregate = 3,
    InnerJoin = 4,
    LeftJoin = 5,
    Lookup = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillDuplicateKeyPolicy
{
    Reject = 0,
    RequirePreAggregation = 1,
    FirstMatchOnly = 2
}

public sealed record SkillConditionClause(
    string Field,
    string Operator, // eq, ne, gt, gte, lt, lte, in, contains
    SkillValueBinding Value);

public sealed record SkillConditionExpr
{
    public string Combinator { get; init; } = "and"; // and | or
    public IReadOnlyList<SkillConditionClause> Clauses { get; init; } = [];
}

public sealed record SkillAggregationMetric(
    string OutputField,
    string Function, // sum | count | min | max
    string? SourceField = null);

public sealed record SkillJoinSpec
{
    public string LeftKey { get; init; } = string.Empty;
    public string RightKey { get; init; } = string.Empty;
    public string KeyType { get; init; } = "string"; // string | integer
    public bool RightKeyUniqueExpected { get; init; } = true;
    public SkillDuplicateKeyPolicy DuplicateKeyPolicy { get; init; } = SkillDuplicateKeyPolicy.Reject;
    public string MissingMatchPolicy { get; init; } = "zero_or_empty"; // drop | null | zero_or_empty | fail
    public IReadOnlyList<string> RightFieldsToInclude { get; init; } = [];
    public string? TargetPrefix { get; init; }
}

public sealed record SkillSortSpec(
    string Field,
    bool Descending = false);

public sealed record SkillProjectionField(
    string OutputField,
    string SourcePointer,
    string ValueType = "string", // string | decimal | integer | date | boolean
    string? CurrencyField = null,
    string? DateTimezone = null);

public sealed record SkillTransformStep : SkillStepDefinition
{
    public SkillTransformOperation Operation { get; init; }
    public SkillValueBinding Source { get; init; } = new();
    public SkillValueBinding? SecondarySource { get; init; }
    public SkillConditionExpr? Predicate { get; init; }
    public IReadOnlyList<string> GroupByFields { get; init; } = [];
    public IReadOnlyList<SkillAggregationMetric> Aggregations { get; init; } = [];
    public SkillJoinSpec? JoinSpec { get; init; }
    public IReadOnlyList<SkillSortSpec> SortBy { get; init; } = [];
    public IReadOnlyList<SkillProjectionField> Projections { get; init; } = [];
    public int OutputLimit { get; init; } = BusinessSkillDefaults.MaxJoinOutputRows;
}

public sealed record SkillBranchStep : SkillStepDefinition
{
    public SkillConditionExpr Condition { get; init; } = new();
    public IReadOnlyList<string> ThenStepIds { get; init; } = [];
    public IReadOnlyList<string> ElseStepIds { get; init; } = [];
}

public sealed record SkillForeachStep : SkillStepDefinition
{
    public SkillValueBinding Source { get; init; } = new();
    public string ItemVariable { get; init; } = "item";
    public IReadOnlyList<string> BodyStepIds { get; init; } = [];
    public int MaxItems { get; init; } = BusinessSkillDefaults.MaxWriteBatchItems;
    public bool Sequential { get; init; } = true;
}

public sealed record SkillGenerateTextStep : SkillStepDefinition
{
    public SkillValueBinding Source { get; init; } = new();
    public IReadOnlyList<string> AllowedInputFields { get; init; } = [];
    public string PromptTemplate { get; init; } = string.Empty;
}

public sealed record SkillApprovalStep : SkillStepDefinition
{
    public string TargetCapabilityId { get; init; } = string.Empty;
    public SkillValueBinding ItemsSource { get; init; } = new();
    public string PlannedMutation { get; init; } = string.Empty;
    public string? KeyAmountField { get; init; }
    public string? CurrencyField { get; init; }
    public IReadOnlyList<string> TargetRecordFields { get; init; } = [];
}

public sealed record SkillOutputStep : SkillStepDefinition
{
    public SkillValueBinding Source { get; init; } = new();
    public IReadOnlyDictionary<string, SkillValueBinding> NamedOutputs { get; init; }
        = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal);
}
