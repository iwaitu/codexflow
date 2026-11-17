// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace CodexFlow.Controllers;

/// <summary>
/// 业务 Skill 构建器功能开关门控（默认关闭 BusinessSkills:Enabled=false 时拦截并返回 503）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BusinessSkillsFeatureGateAttribute : TypeFilterAttribute
{
    public BusinessSkillsFeatureGateAttribute() : base(typeof(BusinessSkillsFeatureGateFilter))
    {
    }

    private sealed class BusinessSkillsFeatureGateFilter(IOptionsMonitor<BusinessSkillsOptions> options) : IAsyncActionFilter
    {
        public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!options.CurrentValue.Enabled)
            {
                context.Result = new ObjectResult(new BusinessSkillErrorDto(
                    BusinessSkillErrorCodes.ConnectionUnavailable,
                    "Business skill builder is disabled by host configuration (BusinessSkills:Enabled=false).",
                    [],
                    false,
                    context.HttpContext.TraceIdentifier))
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
                return Task.CompletedTask;
            }

            return next();
        }
    }
}

/// <summary>
/// P1 企业插件连接、凭据管理、目录快照发现与能力查询控制面接口。
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[BusinessSkillsFeatureGate]
public sealed class BusinessPluginsController(
    IBusinessPluginControlService pluginService,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    [HttpGet("api/plugins")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(IReadOnlyList<BusinessPluginDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<BusinessPluginDto>>> ListPlugins(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var list = await pluginService.ListPluginsAsync(ct).ConfigureAwait(false);
        return Ok(list);
    }

    [HttpPost("api/plugins")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BusinessPluginDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePlugin(
        [FromBody] CreateBusinessPluginRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.CreatePluginAsync(request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPut("api/plugins/{id}/connection")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BusinessPluginDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateConnection(
        [FromRoute] string id,
        [FromBody] UpdatePluginConnectionRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.UpdateConnectionAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPut("api/plugins/{id}/credentials/{slot}")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(CredentialRefSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetCredential(
        [FromRoute] string id,
        [FromRoute] string slot,
        [FromBody] SetPluginCredentialRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.SetCredentialAsync(id, slot, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpDelete("api/plugins/{id}/credentials/{slot}")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(DeleteCredentialResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DeleteCredentialResponseDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteCredential(
        [FromRoute] string id,
        [FromRoute] string slot,
        [FromQuery] bool unbindIfReferenced = false,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DeleteCredentialAsync(id, slot, unbindIfReferenced, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/plugins/{id}/disable")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(DisablePluginResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DisablePlugin(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DisablePluginAsync(id, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPost("api/plugins/{id}/discover")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(DiscoverPluginResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DiscoverPlugin(
        [FromRoute] string id,
        [FromBody] DiscoverPluginRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var res = await pluginService.DiscoverAsync(id, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/plugins/{id}/capabilities")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(CapabilityPageResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> QueryCapabilities(
        [FromRoute] string id,
        [FromQuery] string? snapshotId = null,
        [FromQuery] string? search = null,
        [FromQuery] string? groupTag = null,
        [FromQuery] bool executableOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
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

    [HttpGet("api/plugins/{id}/capabilities/detail")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(CapabilityDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetCapabilityDetail(
        [FromRoute] string id,
        [FromQuery] string capabilityId,
        [FromQuery] string? snapshotId = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return UnprocessableEntity(new BusinessSkillErrorDto(
                BusinessSkillErrorCodes.ValidationFailed,
                "capabilityId is required.",
                [],
                false,
                HttpContext.TraceIdentifier));
        }

        var res = await pluginService.GetCapabilityDetailAsync(id, snapshotId, capabilityId, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpPut("api/plugins/{id}/capabilities/risk")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(CapabilitySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateCapabilityRisk(
        [FromRoute] string id,
        [FromBody] UpdateCapabilityRiskRequest request,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.CapabilityId))
        {
            return UnprocessableEntity(new BusinessSkillErrorDto(
                BusinessSkillErrorCodes.ValidationFailed,
                "capabilityId is required.",
                [],
                false,
                HttpContext.TraceIdentifier));
        }

        var res = await pluginService.UpdateCapabilityRiskAsync(id, request.CapabilityId, request, ct).ConfigureAwait(false);
        return ToActionResult(res);
    }

    [HttpGet("api/plugin-operations/{id}")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(PluginOperationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BusinessSkillErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOperationStatus(
        [FromRoute] string id,
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

    [HttpPost("api/plugins/maintenance/prune")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BusinessSkillRetentionPruneResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PruneRetention(
        [FromServices] IBusinessSkillRetentionService retentionService,
        [FromBody] BusinessSkillRetentionPruneRequest? request = null,
        CancellationToken ct = default)
    {
        if (EnsureControlPlane() is { } denied)
        {
            return denied;
        }

        var result = await retentionService.PruneExpiredAsync(request, ct: ct).ConfigureAwait(false);
        return Ok(result);
    }

    private IActionResult? EnsureControlPlane()
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

    private IActionResult ToActionResult<T>(BusinessServiceResult<T> result)
    {
        if (result.Success || result.Value is not null)
        {
            return StatusCode(result.StatusCode, result.Value);
        }

        return StatusCode(result.StatusCode, result.Error);
    }
}
