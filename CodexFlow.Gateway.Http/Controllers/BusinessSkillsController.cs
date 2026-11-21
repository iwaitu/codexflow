// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.AspNetCore.Http;
using CodexFlow.Gateway.Http;

namespace CodexFlow.Controllers;

/// <summary>
/// P3 业务 Skill 草稿、选择冻结、受控生成、静态校验、试运行、版本发布/切换与正式运行控制面接口。
/// </summary>

public sealed class BusinessSkillsController(
    IBusinessSkillBuilderService builderService,
    IBusinessSkillRunService runService,
    IControlPlaneStateProvider? controlPlane = null) : GatewayEndpointHandler
{

    public async Task<IResult> CreateDraft(
        CreateSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.CreateDraftAsync(request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GetDraft(
        string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetDraftAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> UpdateDraft(
        string id,
        UpdateSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.UpdateDraftAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> UpdateSelection(
        string id,
        PutDraftSelectionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.UpdateSelectionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GenerateCandidate(
        string id,
        GenerateSkillCandidateRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GenerateCandidateAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> ValidateDraft(
        string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.ValidateDraftAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> StartTestRun(
        string id,
        StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.StartDraftTestRunAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> PublishDraft(
        string id,
        PublishSkillDraftRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.PublishDraftAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> ListPublishedSkills(CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return Unauthorized();
        }

        var items = await builderService.ListPublishedSkillsAsync(ct).ConfigureAwait(false);
        return Ok(items);
    }

    public async Task<IResult> GetPublishedSkill(
        string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetPublishedSkillAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GetPublishedSkillMarkdown(
        string id,
        int? version = null,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await builderService.GetPublishedSkillMarkdownAsync(id, version, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> ActivateVersion(
        string id,
        ActivateSkillVersionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await builderService.ActivateVersionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> StartPublishedRun(
        string id,
        StartSkillRunRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.StartPublishedSkillRunAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> ListSkillRuns(
        string id,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await runService.ListRunsForSkillAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GetRun(
        string runId,
        CancellationToken ct = default)
    {
        if (EnsureAuthenticatedUser() is { } denied)
        {
            return denied;
        }

        var res = await runService.GetRunAsync(runId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> CancelRun(
        string runId,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await runService.CancelRunAsync(runId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    private IResult? EnsureAuthenticatedUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(userId) ? Unauthorized() : null;
    }

    private IResult? EnsureControlPlane()
    {
        if (EnsureAuthenticatedUser() is { } unauth)
        {
            return unauth;
        }

        return ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane) ? null : Forbid();
    }

    private IResult ToActionResult<T>(BusinessServiceResult<T> result)
        => result.Success || result.Value is not null
            ? StatusCode(result.StatusCode, result.Value)
            : StatusCode(result.StatusCode, result.Error);
}
