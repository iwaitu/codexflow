// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Orchestration.BusinessSkills;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SkillDefinitionV1))]
[JsonSerializable(typeof(BusinessSkillErrorDto))]
[JsonSerializable(typeof(BusinessApprovalRequestDto))]
[JsonSerializable(typeof(EndpointPolicyDto))]
[JsonSerializable(typeof(CapabilityInvocationSpecDto))]
[JsonSerializable(typeof(string[]), TypeInfoPropertyName = "StringArray")]
[JsonSerializable(typeof(IReadOnlyList<string>), TypeInfoPropertyName = "StringList")]
[JsonSerializable(typeof(BusinessSkillFieldError[]), TypeInfoPropertyName = "FieldErrorArray")]
[JsonSerializable(typeof(IReadOnlyList<BusinessSkillFieldError>), TypeInfoPropertyName = "FieldErrors")]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>), TypeInfoPropertyName = "InputValues")]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(SortedDictionary<string, JsonElement>), TypeInfoPropertyName = "SortedInputValues")]
[JsonSerializable(typeof(BusinessSkillStructuredErrorPayload))]
[JsonSerializable(typeof(BusinessSkillRunSummaryPayload))]
[JsonSerializable(typeof(BusinessSkillDetailPayload))]
[JsonSerializable(typeof(BusinessSkillListPayload))]
[JsonSerializable(typeof(BusinessSkillPendingPayload))]
internal sealed partial class BusinessSkillJsonContext : JsonSerializerContext
{
    internal static BusinessSkillJsonContext Builder { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true
    });

    internal static BusinessSkillJsonContext Run { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    internal static BusinessSkillJsonContext Wire { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed record BusinessSkillStructuredErrorPayload(
    bool Ok, string Code, string Message, bool Retryable, string UserAction,
    IReadOnlyList<BusinessSkillFieldError>? FieldErrors, SkillInputSchemaDto? InputsSchema);

internal sealed record BusinessSkillRunSummaryPayload(
    bool Ok, string RunId, string SkillId, int SkillVersion, string Status, bool CatalogFreshnessChecked,
    string? ErrorCode, string? ErrorMessage, int ExternalRequestsConsumed, BusinessSkillStepSummaryPayload[] Steps,
    JsonElement? Output);

internal sealed record BusinessSkillStepSummaryPayload(
    string StepId, string StepType, string State, string? ErrorCode, string? ResponseSummary);

internal sealed record BusinessSkillDetailPayload(
    bool Ok, string Id, string Name, string Description, string Environment, int ActiveVersion,
    bool PluginEnabled, bool NeedsRevalidation, bool HasWriteSteps, SkillInputSchemaDto Inputs,
    SkillOutputSchemaDto Outputs, BusinessSkillStepTitlePayload[] StepSummary);

internal sealed record BusinessSkillStepTitlePayload(string Id, string? Title);

internal sealed record BusinessSkillListPayload(bool Ok, int Count, string? Query, List<BusinessSkillListItemPayload> Skills);

internal sealed record BusinessSkillListItemPayload(
    string Id, string Name, string Description, int ActiveVersion, string Environment, bool NeedsRevalidation,
    bool HasWriteSteps, string[] InputKeys);

internal sealed record BusinessSkillPendingPayload(
    bool Ok, bool Pending, string RunId, string SkillId, int SkillVersion, string Status,
    bool CatalogFreshnessChecked, string UserAction);
