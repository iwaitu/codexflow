// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Orchestration.Persistence;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Gateway;

internal static class GatewayPayloadFields
{
    public static string? Text(object? payload, string name) => Read(payload, name)?.ToString();

    public static bool Bool(object? payload, string name) => Read(payload, name) is true;

    public static int? Int(object? payload, string name) => Read(payload, name) switch
    {
        int number => number,
        long number when payload is JObject && number is >= int.MinValue and <= int.MaxValue => (int)number,
        _ => null
    };

    private static object? Read(object? payload, string name) => payload switch
    {
        IReadOnlyDictionary<string, object?> dictionary => dictionary.TryGetValue(name, out var value) ? value : null,
        IDictionary<string, object?> dictionary => dictionary.TryGetValue(name, out var value) ? value : null,
        GitCloneMetadata clone when name == nameof(clone.Path) => clone.Path,
        PlanApprovalRequestMetadata plan => name switch
        {
            nameof(plan.plan_artifact_id) => plan.plan_artifact_id,
            nameof(plan.plan_version) => plan.plan_version,
            nameof(plan.markdown_hash) => plan.markdown_hash,
            nameof(plan.goal) => plan.goal,
            nameof(plan.task_count) => plan.task_count,
            nameof(plan.status) => plan.status,
            nameof(plan.markdown) => plan.markdown,
            _ => null
        },
        JObject json => json[name] is JValue scalar ? scalar.Value : json[name],
        JsonElement { ValueKind: JsonValueKind.Object } json =>
            json.TryGetProperty(name, out var value) ? ReadElement(value) : null,
        null => null,
        _ => ManagedPayloadReflection.TryReadProperty(payload, name)
    };

    private static object? ReadElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.Null => null,
        _ => value
    };
}
