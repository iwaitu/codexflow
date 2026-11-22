// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Application;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Remote;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CodexFlow.Gateway.Http.Remote;

public sealed record RemoteRevocationApplyRequest(
    long? MinAccountRevocationSeq,
    long? MinDeviceRevocationSeq,
    IReadOnlyList<Guid>? RevokedPairingIds);

public sealed record RemoteRevocationApplyResponse(
    bool Applied,
    int CancelledActiveRemoteTurns);

public sealed record RemoteWorkspaceAuthorizationListResponse(
    IReadOnlyList<RemoteWorkspaceAuthorizationEntry> Items);

public sealed record RemoteWorkspaceAuthorizationUpdateRequest(
    string WorkspacePath,
    string? SessionId,
    bool Authorized);

public sealed record RemoteWorkspaceAuthorizationUpdateResponse(
    string WorkspaceRealPath,
    bool Authorized);

public sealed record RemoteHostEventsResponse(
    long SummaryEpoch,
    IReadOnlyList<RemotePendingEventEnvelope> Events);

public sealed record RemoteInternalErrorResponse(
    string Error,
    string Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RemoteRevocationApplyRequest))]
[JsonSerializable(typeof(RemoteRevocationApplyResponse))]
[JsonSerializable(typeof(RemoteWorkspaceAuthorizationListResponse))]
[JsonSerializable(typeof(RemoteWorkspaceAuthorizationEntry))]
[JsonSerializable(typeof(IReadOnlyList<RemoteWorkspaceAuthorizationEntry>))]
[JsonSerializable(typeof(RemoteWorkspaceAuthorizationUpdateRequest))]
[JsonSerializable(typeof(RemoteWorkspaceAuthorizationUpdateResponse))]
[JsonSerializable(typeof(RemoteHostEventsResponse))]
[JsonSerializable(typeof(RemotePendingEventEnvelope))]
[JsonSerializable(typeof(IReadOnlyList<RemotePendingEventEnvelope>))]
[JsonSerializable(typeof(RemoteInternalErrorResponse))]
internal partial class RemoteInternalJsonContext : JsonSerializerContext;

/// <summary>
/// Internal loopback control-plane endpoints invoked solely by the managed Electron remote daemon.
/// Protected by both <c>ControlPlane</c> JWT policy and the Pipe-injected <c>remoteBridgeToken</c>,
/// and excluded from public OpenAPI documentation.
/// </summary>
public static class RemoteInternalEndpoints
{
    public static void ConfigureJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.TypeInfoResolverChain.Insert(0, RemoteInternalJsonContext.Default);
    }

    public static IEndpointRouteBuilder MapCodexFlowRemoteInternalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/internal/remote")
            .ExcludeFromDescription()
            .RequireAuthorization(new AuthorizeAttribute
            {
                Policy = ControlPlaneAuthConstants.ControlPlanePolicy,
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
            })
            .AddEndpointFilter(RequireManagedBridgeTokenAsync);

        group.MapPost("/revocations", static async (
            [FromBody] RemoteRevocationApplyRequest request,
            [FromServices] IRemoteRevocationStore revocationStore,
            [FromServices] ICodexGateway gateway,
            CancellationToken ct) =>
        {
            await revocationStore.ApplyRevocationAsync(
                request.MinAccountRevocationSeq,
                request.MinDeviceRevocationSeq,
                request.RevokedPairingIds,
                ct).ConfigureAwait(false);

            var cancelled = await gateway.CancelActiveRemoteTurnsAsync(
                request.MinAccountRevocationSeq,
                request.MinDeviceRevocationSeq,
                request.RevokedPairingIds,
                ct).ConfigureAwait(false);

            return Results.Ok(new RemoteRevocationApplyResponse(true, cancelled));
        });

        group.MapGet("/workspace-authorizations", static async (
            [FromServices] IRemoteWorkspaceAuthorizationStore authStore,
            CancellationToken ct) =>
        {
            var items = await authStore.ListAuthorizedWorkspacesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new RemoteWorkspaceAuthorizationListResponse(items));
        });

        group.MapPut("/workspace-authorizations", static async (
            [FromBody] RemoteWorkspaceAuthorizationUpdateRequest request,
            [FromServices] IRemoteWorkspaceAuthorizationStore authStore,
            CancellationToken ct) =>
        {
            if (request is null ||
                string.IsNullOrWhiteSpace(request.WorkspacePath) ||
                !RemotePathSecurityHelper.TryNormalizeRealDirectory(request.WorkspacePath, out var realPath))
            {
                return Results.BadRequest(new RemoteInternalErrorResponse("invalid_workspace_path", "无效的工作区目录路径。"));
            }

            if (request.Authorized && !Directory.Exists(realPath))
            {
                return Results.BadRequest(new RemoteInternalErrorResponse("workspace_not_found", "指定的外部工作区目录不存在。"));
            }

            await authStore.SetExternalWorkspaceAuthorizedAsync(realPath, request.SessionId, request.Authorized, ct)
                .ConfigureAwait(false);

            return Results.Ok(new RemoteWorkspaceAuthorizationUpdateResponse(realPath, request.Authorized));
        });

        group.MapGet("/host-events", static async (
            [FromServices] IRemotePendingProjection projection,
            [FromServices] IRemoteWorkspaceAuthorizationGuard workspaceGuard,
            [FromServices] IChatService chatService,
            [FromQuery] long afterEventId = 0,
            [FromQuery] int limit = 100,
            CancellationToken ct = default) =>
        {
            var events = await projection.ReadHostEventsAfterAsync(
                Math.Max(0, afterEventId),
                Math.Clamp(limit, 1, 200),
                async (sessionId, token) =>
                {
                    var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
                    if (session is null)
                    {
                        return false;
                    }

                    var decision = await workspaceGuard
                        .AuthorizeSessionAsync(sessionId, session.WorkspacePath, session.ProjectUrl, token)
                        .ConfigureAwait(false);
                    return decision.Allowed;
                },
                ct).ConfigureAwait(false);

            return Results.Ok(new RemoteHostEventsResponse(projection.SummaryEpoch, events));
        });

        return endpoints;
    }

    private static async ValueTask<object?> RequireManagedBridgeTokenAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var bridgeAuth = context.HttpContext.RequestServices.GetService<IRemoteBridgeAuthenticator>();
        var candidate = context.HttpContext.Request.Headers[RemoteBridgeHeaderNames.BridgeToken].ToString();
        if (bridgeAuth is null || !bridgeAuth.IsBridgeAvailable || !bridgeAuth.ValidateBridgeToken(candidate))
        {
            return Results.Problem(
                title: "remote_bridge_unauthorized",
                detail: "Internal remote endpoints require a valid managed Pipe bridge token.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context).ConfigureAwait(false);
    }
}
