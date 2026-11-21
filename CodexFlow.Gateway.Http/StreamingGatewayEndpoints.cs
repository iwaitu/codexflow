// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Controllers;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

namespace CodexFlow.Gateway.Http;

internal static class StreamingGatewayEndpoints
{
    public static void ConfigureJson(JsonSerializerOptions options)
        => options.TypeInfoResolverChain.Insert(0, StreamingGatewayJsonContext.Default);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var gateway = endpoints.MapGroup("/api/gateway")
            .RequireAuthorization(new AuthorizeAttribute("ActiveUser")
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
            });

        MapChat(gateway.MapGroup(string.Empty).WithTags("CodexGateway"));
        MapSessions(gateway.MapGroup(string.Empty).WithTags("GatewaySessionWorkspace"));
    }

    private static void MapChat(RouteGroupBuilder endpoints)
    {
        endpoints.MapPost("/session/bootstrap", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromBody] BootstrapRequest? request,
                CancellationToken ct) =>
            {
                handler.Bind(http);
                return handler.BootstrapSession(request, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/workspaces", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetWorkspaces(ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapPost("/workspaces", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromBody] OpenLocalWorkspaceRequest request,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.OpenLocalWorkspace(request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<OpenLocalWorkspaceResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status503ServiceUnavailable, "text/plain", "application/json", "text/json");

        endpoints.MapPost("/session/daily-assistant", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetOrCreateDailyAssistantSession(ct);
            })
            .Produces<SessionBootstrapResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json");

        endpoints.MapPost("/session", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromQuery] string name = "New Workspace",
                [FromQuery] string? workMode = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.CreateSession(name, workMode, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/session/{sessionId}/tasklist", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetTaskList(sessionId, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/session/{sessionId}/context-window", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetContextWindowSnapshot(sessionId, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapPost("/session/{sessionId}/compact", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                string sessionId,
                [FromBody] CompactSessionContextRequest? request = null,
                [FromServices] IChatClient? chatClient = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.CompactSessionContext(sessionId, request, chatClient, ct);
            })
            .Produces<CompactSessionContextResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status409Conflict, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/history/{conversationId}", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                string conversationId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetHistory(conversationId, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapPost("/session/{sessionId}/attachments", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromRoute] string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.UploadAttachment(sessionId, ct);
            })
            .Produces<UploadedAttachmentDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json")
            .Accepts<UploadAttachmentForm>("multipart/form-data");

        endpoints.MapPost("/chat", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromBody] GatewayChatRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(http);
                return handler.Chat(request, ct);
            })
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/sse", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromQuery] string? sessionId,
                [FromQuery] long lastEventId = 0,
                CancellationToken ct = default,
                [FromQuery] string? epoch = null) =>
            {
                handler.Bind(http);
                return string.IsNullOrWhiteSpace(sessionId)
                    ? GatewayBinding.ValidationError(http, nameof(sessionId), "The sessionId field is required.").ExecuteAsync(http)
                    : handler.Sse(sessionId, lastEventId, ct, epoch);
            })
            .WithMetadata(GatewayBinding.OptionalInt64Query("lastEventId"))
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        endpoints.MapGet("/status", static (
                HttpContext http,
                [FromServices] CodexGatewayController handler,
                [FromQuery] string? sessionId) =>
            {
                handler.Bind(http);
                return string.IsNullOrWhiteSpace(sessionId)
                    ? Task.FromResult(GatewayBinding.ValidationError(http, nameof(sessionId), "The sessionId field is required."))
                    : handler.GetGatewayStatus(sessionId);
            })
            .Produces(StatusCodes.Status200OK);
    }

    private static void MapSessions(RouteGroupBuilder endpoints)
    {
        endpoints.MapGet("/sessions", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 20,
                [FromQuery] bool includeArchived = false,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.ListSessions(page, pageSize, includeArchived, ct);
            })
            .Produces<PagedResponseDto<SessionSummaryDto>>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json");

        endpoints.MapPatch("/session/{sessionId}", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromBody] UpdateSessionRequest request,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.UpdateSession(sessionId, request, ct);
            })
            .Produces<SessionSummaryDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapPost("/session/{sessionId}/plan-mode", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromBody] UpdatePlanModeRequest request,
                [FromServices] CodexFlow.Orchestration.Chat.SessionPolicyResolver? policyResolver = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.UpdatePlanMode(sessionId, request, policyResolver, ct);
            })
            .Produces<PlanModeResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status409Conflict, "text/plain", "application/json", "text/json");

        endpoints.MapDelete("/session/{sessionId}", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.DeleteSession(sessionId, ct);
            })
            .Produces<DeleteResourceResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/conversations", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 20,
                [FromQuery] bool includeArchived = false,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.ListSessionConversations(sessionId, page, pageSize, includeArchived, ct);
            })
            .Produces<PagedResponseDto<ConversationSummaryDto>>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapPost("/session/{sessionId}/conversations", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromBody] CreateConversationRequest? request = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.CreateConversation(sessionId, request, ct);
            })
            .Produces<ConversationSummaryDto>(StatusCodes.Status201Created, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapPatch("/conversations/{conversationId}", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string conversationId,
                [FromBody] UpdateConversationRequest request,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.UpdateConversation(conversationId, request, ct);
            })
            .Produces<ConversationSummaryDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapDelete("/conversations/{conversationId}", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string conversationId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.DeleteConversation(conversationId, ct);
            })
            .Produces<DeleteResourceResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapPost("/session/{sessionId}/cancel", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.CancelTurn(sessionId, ct);
            })
            .Produces<CancelTurnResponse>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/plan", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetPlan(sessionId, ct);
            })
            .Produces<SessionPlanDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/files", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromQuery] string? path = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.ListWorkspaceFiles(sessionId, path, ct);
            })
            .Produces<WorkspaceDirectoryListingDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/files/content", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromQuery] string? path = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.ReadWorkspaceFileContent(sessionId, path, ct);
            })
            .Produces<WorkspaceFileContentDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status400BadRequest, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/changes", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetWorkspaceChanges(sessionId, ct);
            })
            .Produces<WorkspaceChangesDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");

        endpoints.MapGet("/session/{sessionId}/snapshot", static (
                HttpContext http,
                [FromServices] GatewaySessionWorkspaceController handler,
                [FromRoute] string sessionId,
                [FromQuery] string? conversationId = null,
                CancellationToken ct = default) =>
            {
                handler.Bind(http);
                return handler.GetSnapshot(sessionId, conversationId, ct);
            })
            .Produces<SessionSnapshotDto>(StatusCodes.Status200OK, "text/plain", "application/json", "text/json")
            .Produces<GatewayErrorResponse>(StatusCodes.Status404NotFound, "text/plain", "application/json", "text/json");
    }

    internal sealed class UploadAttachmentForm
    {
        public IFormFile File { get; init; } = null!;
    }
}
