// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Agents.Tools;

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(WorkflowRunToolResponse))]
[JsonSerializable(typeof(WorkflowErrorToolResponse))]
[JsonSerializable(typeof(WorkflowListToolResponse))]
[JsonSerializable(typeof(WorkflowRunRecord))]
[JsonSerializable(typeof(CronCreateToolResponse))]
[JsonSerializable(typeof(CronListToolResponse))]
[JsonSerializable(typeof(AuthorizedDirectoryToolResponse))]
[JsonSerializable(typeof(ToolSearchResponse))]
[JsonSerializable(typeof(ScopedToolSearchResponse))]
internal sealed partial class BuiltInToolJsonContext : JsonSerializerContext
{
    internal static BuiltInToolJsonContext Web { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // Scoped search sends concise, model-facing text with the original relaxed escaping.
    internal static BuiltInToolJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed record WorkflowRunToolResponse(string workflow_id, bool completed, WorkflowRunRecord run, string? output);
internal sealed record WorkflowErrorToolResponse(string workflow_id, bool completed, WorkflowRunRecord run, string error);
internal sealed record WorkflowListToolResponse(int count, IReadOnlyList<WorkflowRunRecord> runs);
internal sealed record CronCreateToolResponse(bool created, CronScheduleDefinition schedule);
internal sealed record CronListToolResponse(int count, IReadOnlyList<CronScheduleDefinition> schedules);

internal sealed record AuthorizedDirectoryToolResponse(
    string directory,
    int totalMatched,
    int cursor,
    int limit,
    int? nextCursor,
    IReadOnlyList<ListAuthorizedDirectoryTool.AuthorizedEntryDto> entries);

internal sealed record ToolSearchResponse(int found, string query, List<ToolSearchActivation> tools);

internal sealed record ToolSearchActivation(
    string name,
    string description,
    string category,
    bool activated,
    bool available_now,
    string activation_reason,
    IReadOnlyList<string> tags,
    IReadOnlyList<string> surface,
    string risk,
    IReadOnlyList<string> examples);

internal sealed record ScopedToolSearchResponse(
    int found,
    int shown,
    string query,
    List<ScopedToolSearchMatch> tools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? hint);

internal sealed record ScopedToolSearchMatch(
    string name,
    string description,
    bool available_now,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? note);
