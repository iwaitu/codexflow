// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Agents.Tools;

internal sealed record PushNotificationPayload(
    bool pushed,
    string? notification_id,
    IReadOnlyList<string> delivered_channels,
    PushNotificationRequest request);

internal sealed record RemoteTriggerRecordedPayload(
    bool recorded,
    RemoteTriggerEventDefinition trigger,
    string? worker_job_id);

internal sealed record RemoteTriggerListPayload(int count, IReadOnlyList<RemoteTriggerEventDefinition> events);

internal sealed record MonitorStatusPayload(string? Session, string? Job, int Workers);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PushNotificationPayload))]
[JsonSerializable(typeof(RemoteTriggerRecordedPayload))]
[JsonSerializable(typeof(RemoteTriggerListPayload))]
[JsonSerializable(typeof(MonitorStatusPayload))]
internal sealed partial class AutomationToolJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AutomationToolArgumentJsonContext : JsonSerializerContext;
