// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.Contracts;
using CodexFlow.Contracts.Browser;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Controllers;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Remote;
using CodexFlow.Orchestration.Persistence;

namespace CodexFlow.Gateway.Http;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BootstrapRequest))]
[JsonSerializable(typeof(BootstrapErrorResponse))]
[JsonSerializable(typeof(SessionBootstrapResponse))]
[JsonSerializable(typeof(List<HistoryViewModel>))]
[JsonSerializable(typeof(OpenLocalWorkspaceRequest))]
[JsonSerializable(typeof(OpenLocalWorkspaceResponse))]
[JsonSerializable(typeof(WorkspaceOpenedAuditPayload))]
[JsonSerializable(typeof(SessionCreationResponse))]
[JsonSerializable(typeof(SessionContextWindowResponse))]
[JsonSerializable(typeof(CompactSessionContextRequest))]
[JsonSerializable(typeof(CompactSessionContextResponse))]
[JsonSerializable(typeof(UploadedAttachmentDto))]
[JsonSerializable(typeof(RemoteAttachmentUploadStatusResponse))]
[JsonSerializable(typeof(RemoteChatOpStatusResponse))]
[JsonSerializable(typeof(RemotePendingSummarySnapshotResponse))]
[JsonSerializable(typeof(RemotePendingTodoItem))]
[JsonSerializable(typeof(IReadOnlyList<RemotePendingTodoItem>))]
[JsonSerializable(typeof(StreamingGatewayEndpoints.UploadAttachmentForm))]
[JsonSerializable(typeof(IFormFile))]
[JsonSerializable(typeof(GatewayChatRequest))]
[JsonSerializable(typeof(GatewayChatAcceptedResponse))]
[JsonSerializable(typeof(GatewayChatRejectedResponse))]
[JsonSerializable(typeof(GatewayStatusResponse))]
[JsonSerializable(typeof(GatewayErrorResponse))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(UpdateSessionRequest))]
[JsonSerializable(typeof(UpdatePlanModeRequest))]
[JsonSerializable(typeof(PlanModeResponse))]
[JsonSerializable(typeof(CreateConversationRequest))]
[JsonSerializable(typeof(UpdateConversationRequest))]
[JsonSerializable(typeof(PagedResponseDto<SessionSummaryDto>))]
[JsonSerializable(typeof(PagedResponseDto<ConversationSummaryDto>))]
[JsonSerializable(typeof(SessionSummaryDto))]
[JsonSerializable(typeof(ConversationSummaryDto))]
[JsonSerializable(typeof(DeleteResourceResponse))]
[JsonSerializable(typeof(CancelTurnResponse))]
[JsonSerializable(typeof(SessionPlanDto))]
[JsonSerializable(typeof(WorkspaceDirectoryListingDto))]
[JsonSerializable(typeof(WorkspaceFileContentDto))]
[JsonSerializable(typeof(WorkspaceChangesDto))]
[JsonSerializable(typeof(SessionSnapshotDto))]
[JsonSerializable(typeof(GatewayChannelToolResultSnapshot[]))]
[JsonSerializable(typeof(GatewayChannelToolOutputSnapshot[]))]
[JsonSerializable(typeof(BrowserApprovalRequestDto))]
[JsonSerializable(typeof(BrowserSessionStateDto))]
[JsonSerializable(typeof(ComputerApprovalRequestDto))]
[JsonSerializable(typeof(ComputerSessionStateDto))]
internal partial class StreamingGatewayJsonContext : JsonSerializerContext;

internal static class StreamingGatewayEventJson
{
    public static string Serialize(object? payload) => payload switch
    {
        BrowserApprovalRequestDto request => JsonSerializer.Serialize(request,
            StreamingGatewayJsonContext.Default.BrowserApprovalRequestDto),
        BrowserSessionStateDto state => JsonSerializer.Serialize(state,
            StreamingGatewayJsonContext.Default.BrowserSessionStateDto),
        ComputerApprovalRequestDto computerApproval => JsonSerializer.Serialize(computerApproval,
            StreamingGatewayJsonContext.Default.ComputerApprovalRequestDto),
        ComputerSessionStateDto computerState => JsonSerializer.Serialize(computerState,
            StreamingGatewayJsonContext.Default.ComputerSessionStateDto),
        _ => OrchestrationPayloadJson.Token(payload).ToString(Newtonsoft.Json.Formatting.None)
    };
}
