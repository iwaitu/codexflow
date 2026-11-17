// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CodexFlow.Controllers;

/// <summary>
/// P3 业务 Skill 草稿、选择冻结、受控生成、静态校验、试运行、版本发布/切换与正式运行控制面接口。
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[BusinessSkillsFeatureGate]
public sealed class BusinessSkillsController(
    IBusinessSkillBuilderService builderService,
    IBusinessSkillRunService runService,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    [HttpPost("api/skill-drafts")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillDraftDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateDraft(
        [FromBody] CreateSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.CreateDraftAsync(request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/skill-drafts/{id}")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDraft(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetDraftAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPut("api/skill-drafts/{id}")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateDraft(
        [FromRoute] string id,
        [FromBody] UpdateSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.UpdateDraftAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPut("api/skill-drafts/{id}/selection")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(SkillDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateSelection(
        [FromRoute] string id,
        [FromBody] PutDraftSelectionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.UpdateSelectionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skill-drafts/{id}/generate")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(GenerateSkillCandidateResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GenerateCandidate(
        [FromRoute] string id,
        [FromBody] GenerateSkillCandidateRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GenerateCandidateAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skill-drafts/{id}/validate")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ValidateDraft(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.ValidateDraftAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skill-drafts/{id}/test-runs")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(SkillRunDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartTestRun(
        [FromRoute] string id,
        [FromBody] StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.StartDraftTestRunAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skill-drafts/{id}/publish")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(PublishedSkillDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PublishDraft(
        [FromRoute] string id,
        [FromBody] PublishSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.PublishDraftAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/skills")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(IReadOnlyList<PublishedSkillDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PublishedSkillDto>>> ListPublishedSkills(CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return Unauthorized();
        }

        var items = await builderService.ListPublishedSkillsAsync(ct).ConfigureAwait(false);
        return Ok(items);
    }

    [HttpGet("api/skills/{id}")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(PublishedSkillDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublishedSkill(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetPublishedSkillAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/skills/{id}/markdown")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillMarkdownPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublishedSkillMarkdown(
        [FromRoute] string id,
        [FromQuery] int? version = null,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetPublishedSkillMarkdownAsync(id, version, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skills/{id}/activate-version")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(PublishedSkillDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ActivateVersion(
        [FromRoute] string id,
        [FromBody] ActivateSkillVersionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.ActivateVersionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skills/{id}/runs")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(SkillRunDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartPublishedRun(
        [FromRoute] string id,
        [FromBody] StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.StartPublishedSkillRunAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/skills/{id}/runs")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(IReadOnlyList<SkillRunDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSkillRuns(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await runService.ListRunsForSkillAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/skill-runs/{runId}")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SkillRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(
        [FromRoute] string runId,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await runService.GetRunAsync(runId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/skill-runs/{runId}/cancel")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(SkillRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelRun(
        [FromRoute] string runId,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.CancelRunAsync(runId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    private IActionResult? EnsureAuthenticatedUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(userId) ? Unauthorized() : null;
    }

    private IActionResult? EnsureControlPlane()
    {
        if (EnsureAuthenticatedUser() is { } unauth)
        {
            return unauth;
        }

        return ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane) ? null : Forbid();
    }

    private IActionResult ToActionResult<T>(BusinessServiceResult<T> result)
        => result.Success || result.Value is not null
            ? StatusCode(result.StatusCode, result.Value)
            : StatusCode(result.StatusCode, result.Error);
}
