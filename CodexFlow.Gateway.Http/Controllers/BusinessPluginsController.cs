// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;
using CodexFlow.Gateway.Http;

namespace CodexFlow.Controllers;

/// <summary>
/// P1 企业插件连接、凭据管理、目录快照发现与能力查询控制面接口。
/// </summary>

public sealed class BusinessPluginsController(
    IBusinessPluginControlService pluginService,
    IControlPlaneStateProvider? controlPlane = null) : GatewayEndpointHandler
{

    public async Task<IResult> ListPlugins(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var list = await pluginService.ListPluginsAsync(ct).ConfigureAwait(false);
        return Ok(list);
    }

    public async Task<IResult> CreatePlugin(
        CreateBusinessPluginRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.CreatePluginAsync(request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> UpdateConnection(
        string id,
        UpdatePluginConnectionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.UpdateConnectionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> SetCredential(
        string id,
        string slot,
        SetPluginCredentialRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.SetCredentialAsync(id, slot, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> DeleteCredential(
        string id,
        string slot,
        bool unbindIfReferenced = false,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DeleteCredentialAsync(id, slot, unbindIfReferenced, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> DisablePlugin(
        string id,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DisablePluginAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> DiscoverPlugin(
        string id,
        DiscoverPluginRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DiscoverAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> QueryCapabilities(
        string id,
        string? snapshotId = null,
        string? search = null,
        string? groupTag = null,
        bool executableOnly = false,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var res = await pluginService.QueryCapabilitiesAsync(id, snapshotId, search, groupTag, executableOnly, page, pageSize, ct)
            .ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GetCapabilityDetail(
        string id,
        string capabilityId,
        string? snapshotId = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new BusinessSkillErrorDto(
                BusinessSkillErrorCodes.ValidationFailed,
                "capabilityId is required.",
                [],
                false,
                HttpContext.TraceIdentifier));
        }

        var res = await pluginService.GetCapabilityDetailAsync(id, snapshotId, capabilityId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> UpdateCapabilityRisk(
        string id,
        UpdateCapabilityRiskRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.CapabilityId))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new BusinessSkillErrorDto(
                BusinessSkillErrorCodes.ValidationFailed,
                "capabilityId is required.",
                [],
                false,
                HttpContext.TraceIdentifier));
        }

        var res = await pluginService.UpdateCapabilityRiskAsync(id, request.CapabilityId, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> GetOperationStatus(
        string id,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var res = await pluginService.GetOperationStatusAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    public async Task<IResult> PruneRetention(
        IBusinessSkillRetentionService retentionService,
        BusinessSkillRetentionPruneRequest? request = null,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var result = await retentionService.PruneExpiredAsync(request, ct: ct).ConfigureAwait(false);
        return Ok(result);
    }

    private IResult? EnsureControlPlane()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        return null;
    }

    private IResult ToActionResult<T>(BusinessServiceResult<T> result)
    {
        if (result.Success || result.Value is not null)
        {
            return StatusCode(result.StatusCode, result.Value);
        }

        return StatusCode(result.StatusCode, result.Error);
    }
}
