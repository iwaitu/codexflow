// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Core.BusinessSkills;

internal sealed record WorkflowBranchPayload(bool Matched);
internal sealed record WorkflowPagePayload(IReadOnlyList<JsonElement> Items, int Count, bool PartialData);
internal sealed record WorkflowCapabilityFallbackPayload(string? CreatedId, string? Text);
internal sealed record WorkflowGeneratedTextPayload(string Text, string[] DisclosedFields);
internal sealed record WorkflowApprovalPayload(string TargetCapabilityId, IReadOnlyDictionary<string, JsonElement> Inputs, int BatchCount);
internal sealed record WorkflowPartialLoopPayload(List<JsonElement> Results, bool Partial);
internal sealed record WorkflowLoopPayload(List<JsonElement> Results, int Count);
internal sealed record WorkflowOmittedPayload(bool Omitted);
internal sealed record BusinessTransformRowsPayload(List<JsonElement> Rows, int Count);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowBranchPayload))]
[JsonSerializable(typeof(WorkflowPagePayload))]
[JsonSerializable(typeof(WorkflowCapabilityFallbackPayload))]
[JsonSerializable(typeof(WorkflowGeneratedTextPayload))]
[JsonSerializable(typeof(WorkflowApprovalPayload))]
[JsonSerializable(typeof(WorkflowPartialLoopPayload))]
[JsonSerializable(typeof(WorkflowLoopPayload))]
[JsonSerializable(typeof(WorkflowOmittedPayload))]
[JsonSerializable(typeof(BusinessTransformRowsPayload))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<Dictionary<string, JsonElement>>))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class BusinessRuntimeJsonContext : JsonSerializerContext;
