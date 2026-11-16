// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodexFlow.Controllers;

public sealed record ExecutionSettingsDto(
    string Backend,
    string PermissionMode,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection,
    string TokenDeliveryMode);

public sealed record UpdateExecutionSettingsRequest(
    string? Backend = null,
    string? PermissionMode = null);

public sealed record PermissionRuleListResponse(
    int Count,
    IReadOnlyList<PersistedPermissionRule> Rules);

public sealed record DeletePermissionRuleResponse(
    bool Removed,
    string RuleId);

public sealed record SystemStatusSummaryDto(
    string Version,
    string ExecutionBackend,
    string PermissionMode,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection,
    string TokenDeliveryMode);

public sealed record SettingsErrorDto(
    string Code,
    string Message);

/// <summary>
/// Execution backend, permission mode, persistent <c>allow_always</c> rules, and system security posture endpoints.
/// Relaxing <see cref="ExecutionPermissionMode"/> or managing persistent rules requires the control plane.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class ExecutionSettingsController(
    IOptionsMonitor<ExecutionOptions> executionOptions,
    ExecutionPermissionClamp permissionClamp,
    IExecutionPermissionStore permissionStore,
    IExecutionSettingsService? settingsService = null,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    [HttpGet("api/settings/execution")]
    [Authorize(Policy = "ActiveUser")]
    public ActionResult<ExecutionSettingsDto> GetExecutionSettings()
    {
        var (backend, mode) = permissionClamp.ResolveEffective(executionOptions);
        return Ok(new ExecutionSettingsDto(
            backend.ToString(),
            mode.ToString(),
            controlPlane?.IsControlPlaneAvailable ?? true,
            controlPlane?.SelfApprovalProtection ?? true,
            controlPlane?.TokenDeliveryMode ?? "Unknown"));
    }

    [HttpPut("api/settings/execution")]
    [Authorize(Policy = "ActiveUser")]
    public ActionResult<ExecutionSettingsDto> UpdateExecutionSettings([FromBody] UpdateExecutionSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest(new SettingsErrorDto("invalid_request", "Request body is required."));
        }

        if (settingsService is null)
        {
            return StatusCode(503, new SettingsErrorDto("settings_unavailable", "Execution settings persistence is not configured on this host."));
        }

        ExecutionBackend? nextBackend = null;
        if (!string.IsNullOrWhiteSpace(request.Backend))
        {
            if (!Enum.TryParse<ExecutionBackend>(request.Backend, ignoreCase: true, out var parsedBackend) || !Enum.IsDefined(parsedBackend))
            {
                return BadRequest(new SettingsErrorDto("invalid_backend", "Backend must be Docker or Local."));
            }

            nextBackend = parsedBackend;
        }

        ExecutionPermissionMode? nextMode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
        {
            if (!Enum.TryParse<ExecutionPermissionMode>(request.PermissionMode, ignoreCase: true, out var parsedMode) || !Enum.IsDefined(parsedMode))
            {
                return BadRequest(new SettingsErrorDto("invalid_permission_mode", "PermissionMode must be Ask, AutoInWorkspace, FullAuto, or Deny."));
            }

            nextMode = parsedMode;
        }

        var isControlPlaneCaller = ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane);

        // B6-1 & B7-1: Atomically verify live state, enforce normal-plane tighten-only authorization, persist to disk,
        // and update in-memory clamp without ever reading OptionsMonitor inside `_gate`.
        var applied = permissionClamp.TryApplySettingsUpdateAtomically(
            executionOptions,
            nextBackend,
            nextMode,
            isControlPlaneCaller,
            out var effectiveBackend,
            out var effectiveMode,
            (b, m) => _ = settingsService.Update(new ExecutionSettingsUpdate(b, m)));

        if (!applied)
        {
            return Forbid();
        }

        return Ok(new ExecutionSettingsDto(
            effectiveBackend.ToString(),
            effectiveMode.ToString(),
            controlPlane?.IsControlPlaneAvailable ?? true,
            controlPlane?.SelfApprovalProtection ?? true,
            controlPlane?.TokenDeliveryMode ?? "Unknown"));
    }

    [HttpGet("api/settings/permissions")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    public ActionResult<PermissionRuleListResponse> ListPermissions()
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        var rules = permissionStore.ListRules();
        return Ok(new PermissionRuleListResponse(rules.Count, rules));
    }

    [HttpDelete("api/settings/permissions/{ruleId}")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    public ActionResult<DeletePermissionRuleResponse> DeletePermission([FromRoute] string ruleId)
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        var removed = permissionStore.RemoveRule(ruleId);
        if (!removed)
        {
            return NotFound(new SettingsErrorDto("rule_not_found", $"Permission rule '{ruleId}' was not found."));
        }

        return Ok(new DeletePermissionRuleResponse(true, ruleId));
    }

    [HttpGet("api/system/status")]
    [Authorize(Policy = "ActiveUser")]
    public ActionResult<SystemStatusSummaryDto> GetSystemStatus()
    {
        var current = executionOptions.CurrentValue;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        return Ok(new SystemStatusSummaryDto(
            version,
            current.Backend.ToString(),
            current.PermissionMode.ToString(),
            controlPlane?.IsControlPlaneAvailable ?? true,
            controlPlane?.SelfApprovalProtection ?? true,
            controlPlane?.TokenDeliveryMode ?? "Unknown"));
    }
}
