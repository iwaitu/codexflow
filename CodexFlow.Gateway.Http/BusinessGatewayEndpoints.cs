// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.Browser;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Controllers;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CodexFlow.Gateway.Http;

internal static class BusinessGatewayEndpoints
{
    public static void ConfigureJson(JsonSerializerOptions options)
        => options.TypeInfoResolverChain.Insert(0, BusinessGatewayJsonContext.Default);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var bearer = new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme };
        var plugins = endpoints.MapGroup("").RequireAuthorization(bearer).WithTags("BusinessPlugins");
        plugins.AddEndpointFilter(CheckBusinessSkillsAsync);
        var skills = endpoints.MapGroup("").RequireAuthorization(bearer).WithTags("BusinessSkills");
        skills.AddEndpointFilter(CheckBusinessSkillsAsync);
        var browser = endpoints.MapGroup("").RequireAuthorization(bearer).WithTags("BrowserSession");
        var computer = endpoints.MapGroup("").RequireAuthorization(bearer).WithTags("ComputerSession");

        plugins.MapGet(
            "/api/plugins",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.ListPlugins(ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<IReadOnlyList<BusinessPluginDto>>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status503ServiceUnavailable, "application/json", "text/json", "text/plain");

        plugins.MapPost(
            "/api/plugins",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromBody] CreateBusinessPluginRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.CreatePlugin(request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BusinessPluginDto>(StatusCodes.Status201Created, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapPut(
            "/api/plugins/{id}/connection",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] UpdatePluginConnectionRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateConnection(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BusinessPluginDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapPut(
            "/api/plugins/{id}/credentials/{slot}",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromRoute] string slot,
                [FromBody] SetPluginCredentialRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.SetCredential(id, slot, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<CredentialRefSummaryDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status503ServiceUnavailable, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapDelete(
            "/api/plugins/{id}/credentials/{slot}",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromRoute] string slot,
                CancellationToken ct,
                [FromQuery] bool unbindIfReferenced = false) =>
            {
                handler.Bind(httpContext);
                return handler.DeleteCredential(id, slot, unbindIfReferenced, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .WithMetadata(GatewayBinding.QueryValue<bool>("unbindIfReferenced"))
            .Produces<DeleteCredentialResponseDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<DeleteCredentialResponseDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapPost(
            "/api/plugins/{id}/disable",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.DisablePlugin(id, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<DisablePluginResponseDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapPost(
            "/api/plugins/{id}/discover",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] DiscoverPluginRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.DiscoverPlugin(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<DiscoverPluginResultDto>(StatusCodes.Status202Accepted, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapGet(
            "/api/plugins/{id}/capabilities",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct,
                [FromQuery] string? snapshotId = null,
                [FromQuery] string? search = null,
                [FromQuery] string? groupTag = null,
                [FromQuery] bool executableOnly = false,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 50) =>
            {
                handler.Bind(httpContext);
                return handler.QueryCapabilities(id, snapshotId, search, groupTag, executableOnly, page, pageSize, ct);
            })
            .RequireAuthorization("ActiveUser")
            .WithMetadata(
                GatewayBinding.OptionalEmptyQuery("snapshotId"),
                GatewayBinding.OptionalEmptyQuery("search"),
                GatewayBinding.OptionalEmptyQuery("groupTag"),
                GatewayBinding.QueryValue<bool>("executableOnly"),
                GatewayBinding.QueryValue<int>("page"),
                GatewayBinding.QueryValue<int>("pageSize"))
            .Produces<CapabilityPageResponseDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        plugins.MapGet(
            "/api/plugins/{id}/capabilities/detail",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromQuery] string capabilityId,
                CancellationToken ct,
                [FromQuery] string? snapshotId = null) =>
            {
                handler.Bind(httpContext);
                return handler.GetCapabilityDetail(id, capabilityId, snapshotId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .WithMetadata(
                new RequiredBusinessQuery("capabilityId"),
                GatewayBinding.OptionalEmptyQuery("capabilityId"),
                GatewayBinding.OptionalEmptyQuery("snapshotId"))
            .Produces<CapabilityDetailDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain");

        plugins.MapPut(
            "/api/plugins/{id}/capabilities/risk",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] UpdateCapabilityRiskRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateCapabilityRisk(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<CapabilitySummaryDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        plugins.MapGet(
            "/api/plugin-operations/{id}",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetOperationStatus(id, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<PluginOperationStatusDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        plugins.MapPost(
            "/api/plugins/maintenance/prune",
            static (
                [FromServices] BusinessPluginsController handler,
                HttpContext httpContext,
                [FromServices] IBusinessSkillRetentionService retentionService,
                CancellationToken ct,
                [FromBody] BusinessSkillRetentionPruneRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.PruneRetention(retentionService, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BusinessSkillRetentionPruneResultDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-drafts",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromBody] CreateSkillDraftRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.CreateDraft(request, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<SkillDraftDto>(StatusCodes.Status201Created, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skill-drafts/{id}",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetDraft(id, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<SkillDraftDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        skills.MapPut(
            "/api/skill-drafts/{id}",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] UpdateSkillDraftRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateDraft(id, request, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<SkillDraftDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain");

        skills.MapPut(
            "/api/skill-drafts/{id}/selection",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] PutDraftSelectionRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateSelection(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<SkillDraftDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-drafts/{id}/generate",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] GenerateSkillCandidateRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GenerateCandidate(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<GenerateSkillCandidateResultDto>(StatusCodes.Status202Accepted, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status503ServiceUnavailable, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-drafts/{id}/validate",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.ValidateDraft(id, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<SkillDraftDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-drafts/{id}/test-runs",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] StartSkillRunRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.StartTestRun(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<SkillRunDto>(StatusCodes.Status202Accepted, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-drafts/{id}/publish",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] PublishSkillDraftRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.PublishDraft(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<PublishedSkillDto>(StatusCodes.Status201Created, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skills",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.ListPublishedSkills(ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<IReadOnlyList<PublishedSkillDto>>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skills/{id}",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetPublishedSkill(id, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<PublishedSkillDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skills/{id}/markdown",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct,
                [FromQuery] int? version = null) =>
            {
                handler.Bind(httpContext);
                return handler.GetPublishedSkillMarkdown(id, version, ct);
            })
            .RequireAuthorization("ActiveUser")
            .WithMetadata(GatewayBinding.QueryValue<int>("version", allowEmpty: true))
            .Produces<SkillMarkdownPreviewDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skills/{id}/activate-version",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] ActivateSkillVersionRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.ActivateVersion(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<PublishedSkillDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skills/{id}/runs",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                [FromBody] StartSkillRunRequest request,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.StartPublishedRun(id, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<SkillRunDto>(StatusCodes.Status202Accepted, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status409Conflict, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status422UnprocessableEntity, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skills/{id}/runs",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string id,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.ListSkillRuns(id, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<IReadOnlyList<SkillRunDto>>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain");

        skills.MapGet(
            "/api/skill-runs/{runId}",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string runId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetRun(runId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<SkillRunDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        skills.MapPost(
            "/api/skill-runs/{runId}/cancel",
            static (
                [FromServices] BusinessSkillsController handler,
                HttpContext httpContext,
                [FromRoute] string runId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.CancelRun(runId, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<SkillRunDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BusinessSkillErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain");

        browser.MapGet(
            "/api/gateway/session/{sessionId}/browser",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetBrowserState(sessionId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<BrowserSessionStateDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        browser.MapGet(
            "/api/gateway/session/{sessionId}/browser/screenshot",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetBrowserScreenshot(sessionId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status304NotModified)
            .Produces<BrowserApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        browser.MapPost(
            "/api/gateway/session/{sessionId}/browser/close",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.CloseBrowserSession(sessionId, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BrowserSessionCloseResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        browser.MapPost(
            "/api/gateway/session/{sessionId}/browser/takeover/start",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct,
                [FromBody] BrowserTakeoverStartRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.StartBrowserTakeover(sessionId, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BrowserTakeoverResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status400BadRequest, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        browser.MapPost(
            "/api/gateway/session/{sessionId}/browser/takeover/complete",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct,
                [FromBody] BrowserTakeoverCompleteRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.CompleteBrowserTakeover(sessionId, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BrowserTakeoverResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        browser.MapGet(
            "/api/settings/browser",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext) =>
            {
                handler.Bind(httpContext);
                return handler.GetBrowserSettings();
            })
            .RequireAuthorization("ActiveUser")
            .Produces<BrowserSettingsDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain");

        browser.MapPut(
            "/api/settings/browser",
            static (
                [FromServices] BrowserSessionController handler,
                HttpContext httpContext,
                [FromBody] UpdateBrowserSettingsRequest request) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateBrowserSettings(request);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<BrowserSettingsDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status400BadRequest, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain")
            .Produces<BrowserApiErrorDto>(StatusCodes.Status503ServiceUnavailable, "application/json", "text/json", "text/plain");

        computer.MapGet(
            "/api/gateway/session/{sessionId}/computer",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetComputerState(sessionId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces<ComputerSessionStateDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapGet(
            "/api/gateway/session/{sessionId}/computer/screenshot",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.GetComputerScreenshot(sessionId, ct);
            })
            .RequireAuthorization("ActiveUser")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status304NotModified)
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapPost(
            "/api/gateway/session/{sessionId}/computer/close",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct) =>
            {
                handler.Bind(httpContext);
                return handler.CloseComputerSession(sessionId, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<ComputerSessionCloseResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapPost(
            "/api/gateway/session/{sessionId}/computer/takeover/start",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct,
                [FromBody] ComputerTakeoverStartRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.StartComputerTakeover(sessionId, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<ComputerTakeoverResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status400BadRequest, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapPost(
            "/api/gateway/session/{sessionId}/computer/takeover/complete",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromRoute] string sessionId,
                CancellationToken ct,
                [FromBody] ComputerTakeoverCompleteRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.CompleteComputerTakeover(sessionId, request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<ComputerTakeoverResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapPost(
            "/api/gateway/computer/emergency-stop",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                CancellationToken ct,
                [FromBody] ComputerEmergencyStopRequest? request = null) =>
            {
                handler.Bind(httpContext);
                return handler.EmergencyStop(request, ct);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<ComputerEmergencyStopResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status404NotFound, "application/json", "text/json", "text/plain");

        computer.MapPost(
            "/api/gateway/computer/protected-windows",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromBody] RegisterProtectedWindowsRequest request) =>
            {
                handler.Bind(httpContext);
                return handler.RegisterProtectedWindows(request);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<RegisterProtectedWindowsResponse>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status400BadRequest, "application/json", "text/json", "text/plain");

        computer.MapGet(
            "/api/settings/computer",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext) =>
            {
                handler.Bind(httpContext);
                return handler.GetComputerSettings();
            })
            .RequireAuthorization("ActiveUser")
            .Produces<ComputerSettingsDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain");

        computer.MapPut(
            "/api/settings/computer",
            static (
                [FromServices] ComputerSessionController handler,
                HttpContext httpContext,
                [FromBody] UpdateComputerSettingsRequest request) =>
            {
                handler.Bind(httpContext);
                return handler.UpdateComputerSettings(request);
            })
            .RequireAuthorization(ControlPlaneAuthConstants.ControlPlanePolicy)
            .Produces<ComputerSettingsDto>(StatusCodes.Status200OK, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status400BadRequest, "application/json", "text/json", "text/plain")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/json", "text/json", "text/plain")
            .Produces<ComputerApiErrorDto>(StatusCodes.Status503ServiceUnavailable, "application/json", "text/json", "text/plain");

    }

    private static ValueTask<object?> CheckBusinessSkillsAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var requiredQuery = httpContext.GetEndpoint()?.Metadata.GetMetadata<RequiredBusinessQuery>();
        if (requiredQuery is { } query && string.IsNullOrWhiteSpace(httpContext.Request.Query[query.Name].ToString()))
        {
            return ValueTask.FromResult<object?>(GatewayBinding.ValidationError(
                httpContext, query.Name, $"The {query.Name} field is required."));
        }

        var options = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<BusinessSkillsOptions>>();
        if (options.CurrentValue.Enabled)
        {
            return next(context);
        }

        var error = new BusinessSkillErrorDto(
            BusinessSkillErrorCodes.ConnectionUnavailable,
            "Business skill builder is disabled by host configuration (BusinessSkills:Enabled=false).",
            [],
            false,
            httpContext.TraceIdentifier);
        return ValueTask.FromResult<object?>(TypedResults.Json(
            error, BusinessGatewayJsonContext.Default.BusinessSkillErrorDto,
            statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    private sealed record RequiredBusinessQuery(string Name);
}
