// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Contracts;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Controllers;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models.Jobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CodexFlow.Gateway.Http;

internal static class SimpleGatewayEndpoints
{
    private const string ActiveUser = "ActiveUser";
    private const string ControlPlane = ControlPlaneAuthConstants.ControlPlanePolicy;
    private const string Json = "application/json";

    public static void ConfigureJson(JsonSerializerOptions options)
        => options.TypeInfoResolverChain.Insert(0, SimpleGatewayJsonContext.Default);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        MapJobs(endpoints);
        MapApprovals(endpoints);
        MapArtifactVerification(endpoints);
        MapSettings(endpoints);
    }

    private static void MapJobs(IEndpointRouteBuilder endpoints)
    {
        var jobs = endpoints.MapGroup("/api/jobs").WithTags("BackgroundJob")
            .RequireAuthorization(Bearer(ActiveUser));

        jobs.MapPost("", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromBody] CreateBackgroundJobRequest request) =>
        {
            handler.Bind(context);
            return handler.CreateJob(request);
        }).Accepts<CreateBackgroundJobRequest>(Json, "text/json", "application/*+json")
            .Produces(StatusCodes.Status200OK);

        jobs.MapGet("/{jobId}", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId) =>
        {
            handler.Bind(context);
            return handler.GetStatus(jobId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapGet("/{jobId}/status", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId) =>
        {
            handler.Bind(context);
            return handler.GetStatusAlias(jobId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapGet("/session/{sessionId}", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string sessionId) =>
        {
            handler.Bind(context);
            return handler.GetSessionJobs(sessionId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapGet("/{jobId}/history", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId, [FromQuery] long? afterSeq = null) =>
        {
            handler.Bind(context);
            return handler.GetHistory(jobId, afterSeq);
        }).WithMetadata(GatewayBinding.QueryValue<long>("afterSeq", allowEmpty: true))
            .Produces(StatusCodes.Status200OK);

        jobs.MapPost("/{jobId}/cancel", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId) =>
        {
            handler.Bind(context);
            return handler.CancelJob(jobId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapDelete("/{jobId}", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId) =>
        {
            handler.Bind(context);
            return handler.CancelJobAlias(jobId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapPost("/{jobId}/retry", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId) =>
        {
            handler.Bind(context);
            return handler.RetryJob(jobId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapPost("/{jobId}/resume", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
            ResumeJobRequest? request) =>
        {
            handler.Bind(context);
            return handler.ResumeJob(jobId, request);
        }).Accepts<ResumeJobRequest>(isOptional: true, Json, "text/json", "application/*+json")
            .Produces(StatusCodes.Status200OK);

        jobs.MapPost("/{jobId}/continue", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string jobId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
            ContinueWorkerHttpRequest? request) =>
        {
            handler.Bind(context);
            return handler.ContinueWorker(jobId, request);
        }).Accepts<ContinueWorkerHttpRequest>(isOptional: true, Json, "text/json", "application/*+json")
            .Produces(StatusCodes.Status200OK);

        jobs.MapPost("/session/{sessionId}/cancel-all", static (HttpContext context, [FromServices] BackgroundJobController handler,
            [FromRoute] string sessionId) =>
        {
            handler.Bind(context);
            return handler.CancelSessionJobs(sessionId);
        }).Produces(StatusCodes.Status200OK);

        jobs.MapPost("/my/cancel-all", static (HttpContext context, [FromServices] BackgroundJobController handler) =>
        {
            handler.Bind(context);
            return handler.CancelAllMyJobs();
        }).Produces(StatusCodes.Status200OK);
    }

    private static void MapApprovals(IEndpointRouteBuilder endpoints)
    {
        var approvals = endpoints.MapGroup("/api/gateway").WithTags("GatewayApproval").RequireAuthorization(Bearer());

        approvals.MapGet("/session/{sessionId}/approvals", static (HttpContext context,
            [FromServices] GatewayApprovalController handler, [FromRoute] string sessionId, CancellationToken ct) =>
        {
            handler.Bind(context);
            return handler.GetPendingApprovals(sessionId, ct);
        }).RequireAuthorization(ActiveUser).ProducesJson<PendingApprovalsResponse>();

        approvals.MapGet("/approvals/{approvalId}/script", static (HttpContext context,
            [FromServices] GatewayApprovalController handler, [FromRoute] string approvalId, CancellationToken ct) =>
        {
            handler.Bind(context);
            return handler.GetPendingApprovalScript(approvalId, ct);
        }).RequireAuthorization(ControlPlane).ProducesJson<PendingApprovalScriptSnapshot>();

        approvals.MapPost("/approvals/{approvalId}", static (HttpContext context, [FromServices] GatewayApprovalController handler,
            [FromRoute] string approvalId, [FromBody] SubmitExecutionApprovalRequest request, CancellationToken ct) =>
        {
            handler.Bind(context);
            return handler.SubmitApprovalDecision(approvalId, request, ct);
        }).RequireAuthorization(ControlPlane).Accepts<SubmitExecutionApprovalRequest>(Json, "text/json", "application/*+json")
            .Produces(StatusCodes.Status200OK);
    }

    private static void MapArtifactVerification(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/gateway/artifacts/verify", static (HttpContext context,
            [FromServices] GatewayArtifactController handler, [FromBody] VerifyForgeArtifactRequest request, CancellationToken ct) =>
        {
            handler.Bind(context);
            return handler.Verify(request, ct);
        }).WithTags("GatewayArtifact").RequireAuthorization(Bearer(ActiveUser))
            .Accepts<VerifyForgeArtifactRequest>(Json, "text/json", "application/*+json")
            .Produces(StatusCodes.Status200OK);
    }

    private static void MapSettings(IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api").WithTags("ExecutionSettings").RequireAuthorization(Bearer());

        settings.MapGet("/settings/execution", static (HttpContext context, [FromServices] ExecutionSettingsController handler) =>
        {
            handler.Bind(context);
            return handler.GetExecutionSettings();
        }).RequireAuthorization(ActiveUser).ProducesJson<ExecutionSettingsDto>();

        settings.MapPut("/settings/execution", static (HttpContext context, [FromServices] ExecutionSettingsController handler,
            [FromBody] UpdateExecutionSettingsRequest request) =>
        {
            handler.Bind(context);
            return handler.UpdateExecutionSettings(request);
        }).RequireAuthorization(ActiveUser).Accepts<UpdateExecutionSettingsRequest>(Json, "text/json", "application/*+json")
            .ProducesJson<ExecutionSettingsDto>();

        settings.MapGet("/settings/permissions", static (HttpContext context, [FromServices] ExecutionSettingsController handler) =>
        {
            handler.Bind(context);
            return handler.ListPermissions();
        }).RequireAuthorization(ControlPlane).ProducesJson<PermissionRuleListResponse>();

        settings.MapDelete("/settings/permissions/{ruleId}", static (HttpContext context,
            [FromServices] ExecutionSettingsController handler, [FromRoute] string ruleId) =>
        {
            handler.Bind(context);
            return handler.DeletePermission(ruleId);
        }).RequireAuthorization(ControlPlane).ProducesJson<DeletePermissionRuleResponse>();

        settings.MapGet("/settings/model", static (HttpContext context, [FromServices] ExecutionSettingsController handler) =>
        {
            handler.Bind(context);
            return handler.GetModelSettings();
        }).RequireAuthorization(ActiveUser).ProducesJson<ModelSettingsDto>()
            .ProducesJson<SettingsErrorDto>(StatusCodes.Status503ServiceUnavailable);

        settings.MapPut("/settings/model", static (HttpContext context, [FromServices] ExecutionSettingsController handler,
            [FromBody] UpdateModelSettingsRequest request, CancellationToken ct) =>
        {
            handler.Bind(context);
            return handler.UpdateModelSettings(request, ct);
        }).RequireAuthorization(ControlPlane).Accepts<UpdateModelSettingsRequest>(Json, "text/json", "application/*+json")
            .ProducesJson<ModelSettingsDto>().ProducesJson<SettingsErrorDto>(StatusCodes.Status400BadRequest)
            .ProducesJson<ProblemDetails>(StatusCodes.Status403Forbidden).ProducesJson<SettingsErrorDto>(StatusCodes.Status503ServiceUnavailable);

        settings.MapGet("/system/status", static (HttpContext context, [FromServices] ExecutionSettingsController handler,
            CancellationToken ct, [FromQuery] bool refresh = false) =>
        {
            handler.Bind(context);
            return handler.GetSystemStatus(refresh, ct);
        }).RequireAuthorization(ActiveUser).WithMetadata(GatewayBinding.QueryValue<bool>("refresh"))
            .ProducesJson<SystemStatusSummaryDto>();
    }

    // Retain the media types documented by MVC for its typed response declarations.
    private static RouteHandlerBuilder ProducesJson<T>(this RouteHandlerBuilder route, int status = StatusCodes.Status200OK)
        => route.Produces<T>(status, Json, "text/plain", "text/json");

    private static AuthorizeAttribute Bearer(string? policy = null)
        => new() { Policy = policy, AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateBackgroundJobRequest))]
[JsonSerializable(typeof(ResumeJobRequest))]
[JsonSerializable(typeof(ContinueWorkerHttpRequest))]
[JsonSerializable(typeof(JobReasonResponse))]
[JsonSerializable(typeof(JobCreatedResponse))]
[JsonSerializable(typeof(JobCommandResponse))]
[JsonSerializable(typeof(JobNoOpResponse))]
[JsonSerializable(typeof(JobRetryResponse))]
[JsonSerializable(typeof(JobCommandFailureResponse))]
[JsonSerializable(typeof(JobRetryFailureResponse))]
[JsonSerializable(typeof(JobContinueResponse))]
[JsonSerializable(typeof(CancelJobsResponse))]
[JsonSerializable(typeof(BackgroundJobDto))]
[JsonSerializable(typeof(BackgroundJobDto[]))]
[JsonSerializable(typeof(JobEventLogEntry[]))]
[JsonSerializable(typeof(PendingApprovalsResponse))]
[JsonSerializable(typeof(ComputerApprovalRequestDto))]
[JsonSerializable(typeof(IReadOnlyList<ComputerApprovalRequestDto>))]
[JsonSerializable(typeof(PendingApprovalScriptSnapshot))]
[JsonSerializable(typeof(SubmitExecutionApprovalRequest))]
[JsonSerializable(typeof(SubmitExecutionApprovalResponse))]
[JsonSerializable(typeof(ExecutionApprovalErrorResponse))]
[JsonSerializable(typeof(VerifyForgeArtifactRequest))]
[JsonSerializable(typeof(ForgeArtifactVerificationReceipt))]
[JsonSerializable(typeof(ExecutionSettingsDto))]
[JsonSerializable(typeof(UpdateExecutionSettingsRequest))]
[JsonSerializable(typeof(PermissionRuleListResponse))]
[JsonSerializable(typeof(DeletePermissionRuleResponse))]
[JsonSerializable(typeof(ModelSettingsDto))]
[JsonSerializable(typeof(UpdateModelSettingsRequest))]
[JsonSerializable(typeof(SettingsErrorDto))]
[JsonSerializable(typeof(SystemStatusSummaryDto))]
internal partial class SimpleGatewayJsonContext : JsonSerializerContext;
