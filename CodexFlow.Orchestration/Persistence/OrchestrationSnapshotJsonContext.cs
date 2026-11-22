// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Gateway;
using CodexFlow.Infrastructure.Services;
using CodexFlow.Orchestration.Services;
using CodexFlow.Services.ArtifactAudit;
using CodexFlow.Services.Background;

namespace CodexFlow.Orchestration.Persistence;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RedisTaskListService.TaskListSnapshot), TypeInfoPropertyName = "RedisTaskList")]
[JsonSerializable(typeof(PlanTaskDispatchService.TaskListSnapshot), TypeInfoPropertyName = "PlanTaskList")]
[JsonSerializable(typeof(GatewayMessageProcessor.GatewayTaskListSnapshot), TypeInfoPropertyName = "GatewayTaskList")]
[JsonSerializable(typeof(GatewayChannelToolResultSnapshot[]), TypeInfoPropertyName = "ToolResults")]
[JsonSerializable(typeof(GatewayChannelToolOutputSnapshot[]), TypeInfoPropertyName = "ToolOutputs")]
[JsonSerializable(typeof(GatewayToolHistoryEnvelope))]
[JsonSerializable(typeof(UploadedAttachmentResult))]
[JsonSerializable(typeof(IReadOnlyList<ValidatedChatAttachment>), TypeInfoPropertyName = "AttachmentList")]
[JsonSerializable(typeof(List<ValidatedChatAttachment>), TypeInfoPropertyName = "Attachments")]
[JsonSerializable(typeof(HostManagedNuGetAdvisoryProvider.CacheDocument), TypeInfoPropertyName = "NuGetAdvisoryCache")]
[JsonSerializable(typeof(BrowserApprovalDigestPayload))]
[JsonSerializable(typeof(ComputerApprovalDigestPayload))]
[JsonSerializable(typeof(PlanModeAuditPayload))]
[JsonSerializable(typeof(ReviewedDependencyList))]
[JsonSerializable(typeof(ForgeArtifactManifest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(PlannerSummaryUpdate))]
internal sealed partial class OrchestrationSnapshotJsonContext : JsonSerializerContext
{
    internal static OrchestrationSnapshotJsonContext CamelCase { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    internal static OrchestrationSnapshotJsonContext CaseInsensitive { get; } = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    internal static OrchestrationSnapshotJsonContext LegacyManifest { get; } = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    });
}

internal sealed record GatewayToolHistoryEnvelope(
    [property: JsonPropertyName("results")] IReadOnlyList<GatewayChannelToolResultSnapshot> Results,
    [property: JsonPropertyName("resultsTruncated")] bool ResultsTruncated,
    [property: JsonPropertyName("outputs")] IReadOnlyList<GatewayChannelToolOutputSnapshot> Outputs,
    [property: JsonPropertyName("outputsTruncated")] bool OutputsTruncated);

internal sealed record BrowserApprovalDigestPayload(
    string V, string SessionId, string Tool, string Action, string TargetOrigin, string TargetUrl,
    string TargetDescription, string ExpressionOrPath, IReadOnlyList<string> AllowedDecisions);

internal sealed record ComputerApprovalDigestPayload(
    string V, string SessionId, string Tool, string Action, string TargetProcessName,
    string TargetWindowTitle, string TargetWindowHandle, string ActionSummary,
    int? TargetX, int? TargetY, IReadOnlyList<string> AllowedDecisions);

internal sealed record PlanModeAuditPayload(
    string UserId, string SessionId, string WorkMode, int ModeVersion, bool PlanModeActive, int PlanModeVersion,
    string ChangedBy, string? Reason, string? MessageId, DateTime? ChangedAtUtc);

internal sealed record ReviewedDependencyList(
    int Schema, string ReviewedBy, string ReviewedAtUtc, string TargetFramework,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewedDependencyPackage[]? Packages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewedDependencyGroup[]? Groups);

internal sealed record ReviewedDependencyPackage(string Id, string Version);

internal sealed record ReviewedDependencyGroup(
    string Name, ReviewedDependencyPackage[] Packages, ReviewedDependencyPackage[] SdkPackages);
