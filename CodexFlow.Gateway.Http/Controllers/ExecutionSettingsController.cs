// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Orchestration.Hosting;
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

public sealed record ModelSettingsDto(
    string? ApiUrl,
    string? ApiMode,
    string? Model,
    int? MaxTokensLength,
    float? Temperature,
    float? TopP,
    bool ApiKeySet,
    string? ApiKeyLast4,
    bool SupportsMultimodal = false,
    string? MultimodalProbeStatus = null,
    string? MultimodalProbeMessage = null);

public sealed record UpdateModelSettingsRequest(
    string? ApiUrl = null,
    string? ApiMode = null,
    string? Model = null,
    int? MaxTokensLength = null,
    float? Temperature = null,
    float? TopP = null,
    string? ApiKey = null,
    bool? SupportsMultimodal = null);

public sealed record PermissionRuleListResponse(
    int Count,
    IReadOnlyList<PersistedPermissionRule> Rules);

public sealed record DeletePermissionRuleResponse(
    bool Removed,
    string RuleId);

public sealed record PreflightIssueDto(
    string Component,
    string Message);

public sealed record SystemStatusSummaryDto(
    string Version,
    string ExecutionBackend,
    string PermissionMode,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection,
    string TokenDeliveryMode,
    bool BackendAvailable = true,
    string? BackendAvailabilityReason = null,
    bool DockerAvailable = false,
    string? DockerAvailabilityReason = null,
    bool ModelConfigured = false,
    bool? ModelReachable = false,
    string ModelProbeStatus = "Unknown",
    string PreflightMode = "Fail",
    IReadOnlyList<PreflightIssueDto>? PreflightIssues = null);

public sealed record SettingsErrorDto(
    string Code,
    string Message);

/// <summary>
/// Execution backend, permission mode, model settings, persistent <c>allow_always</c> rules, and system status endpoints.
/// Relaxing <see cref="ExecutionPermissionMode"/>, modifying model endpoint/keys, or managing persistent rules requires the control plane.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class ExecutionSettingsController(
    IOptionsMonitor<ExecutionOptions> executionOptions,
    ExecutionPermissionClamp permissionClamp,
    IExecutionPermissionStore permissionStore,
    IExecutionSettingsService? settingsService = null,
    IControlPlaneStateProvider? controlPlane = null,
    IModelSettingsService? modelSettingsService = null,
    IHostPreflightService? preflightService = null,
    IConfiguration? configuration = null,
    ContextCompressionTriggerOptions? contextCompressionTriggerOptions = null,
    IMultimodalCapabilityProbe? multimodalProbe = null) : ControllerBase
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

    [HttpGet("api/settings/model")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(ModelSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SettingsErrorDto), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ModelSettingsDto> GetModelSettings()
    {
        if (modelSettingsService is null)
        {
            return StatusCode(503, new SettingsErrorDto("settings_unavailable", "Model settings persistence is not configured on this host."));
        }

        return Ok(ToModelDto(modelSettingsService.Read()));
    }

    [HttpPut("api/settings/model")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(ModelSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SettingsErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(SettingsErrorDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ModelSettingsDto>> UpdateModelSettings(
        [FromBody] UpdateModelSettingsRequest request,
        CancellationToken ct = default)
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        if (request is null)
        {
            return BadRequest(new SettingsErrorDto("invalid_request", "Request body is required."));
        }

        if (modelSettingsService is null)
        {
            return StatusCode(503, new SettingsErrorDto("settings_unavailable", "Model settings persistence is not configured on this host."));
        }

        try
        {
            var existingCreds = modelSettingsService.ReadProbeCredentials();
            var updated = modelSettingsService.Update(new ModelSettingsUpdate(
                ApiUrl: request.ApiUrl,
                ApiMode: request.ApiMode,
                Model: request.Model,
                MaxTokensLength: request.MaxTokensLength,
                Temperature: request.Temperature,
                TopP: request.TopP,
                ApiKey: request.ApiKey,
                SupportsMultimodal: request.SupportsMultimodal));

            // 写盘后立刻同步内存中的上下文窗口上限，避免等待配置文件 FileSystemWatcher 重载期间读到旧阈值。
            if (contextCompressionTriggerOptions is not null && updated.MaxTokensLength is > 0)
            {
                contextCompressionTriggerOptions.ModelMaxContextTokens = updated.MaxTokensLength.Value;
            }

            var effectiveApiUrl = request.ApiUrl ?? existingCreds.ApiUrl;
            var effectiveApiMode = request.ApiMode ?? existingCreds.ApiMode;
            var effectiveModel = request.Model ?? existingCreds.Model;
            var effectiveApiKey = request.ApiKey ?? existingCreds.ApiKey;

            if (multimodalProbe is not null
                && !string.IsNullOrWhiteSpace(effectiveApiUrl)
                && !string.IsNullOrWhiteSpace(effectiveModel)
                && !string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                var probe = await multimodalProbe
                    .ProbeAsync(effectiveApiUrl, effectiveApiMode, effectiveModel, effectiveApiKey, ct)
                    .ConfigureAwait(false);

                updated = modelSettingsService.Update(new ModelSettingsUpdate(
                    SupportsMultimodal: probe.IsSupported,
                    MultimodalProbeStatus: probe.Status,
                    MultimodalProbeMessage: probe.Message));
            }
            else if (multimodalProbe is not null)
            {
                updated = modelSettingsService.Update(new ModelSettingsUpdate(
                    SupportsMultimodal: false,
                    MultimodalProbeStatus: "NotProbed",
                    MultimodalProbeMessage: "模型地址、API Key 或模型名称不完整，未执行多模态测试。"));
            }

            return Ok(ToModelDto(updated));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new SettingsErrorDto("invalid_model_settings", ex.Message));
        }
    }

    [HttpGet("api/system/status")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(SystemStatusSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemStatusSummaryDto>> GetSystemStatus(
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        var (backend, mode) = permissionClamp.ResolveEffective(executionOptions);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

        if (preflightService is not null)
        {
            var preflight = await preflightService.GetStatusAsync(backend, refresh, ct).ConfigureAwait(false);
            return Ok(new SystemStatusSummaryDto(
                Version: version,
                ExecutionBackend: backend.ToString(),
                PermissionMode: mode.ToString(),
                ControlPlaneAvailable: controlPlane?.IsControlPlaneAvailable ?? true,
                SelfApprovalProtection: controlPlane?.SelfApprovalProtection ?? true,
                TokenDeliveryMode: controlPlane?.TokenDeliveryMode ?? "Unknown",
                BackendAvailable: preflight.ActiveBackendAvailable,
                BackendAvailabilityReason: preflight.ActiveBackendReason,
                DockerAvailable: preflight.DockerAvailable,
                DockerAvailabilityReason: preflight.DockerReason,
                ModelConfigured: preflight.ModelConfigured,
                ModelReachable: preflight.ModelReachable,
                ModelProbeStatus: preflight.ModelProbeStatus,
                PreflightMode: preflight.PreflightMode.ToString(),
                PreflightIssues: preflight.Issues.Select(i => new PreflightIssueDto(i.Component, i.Message)).ToArray()));
        }

        var modelSection = configuration?.GetSection("VllmAgent");
        var modelConfigured = modelSettingsService?.Read() is { ApiUrl.Length: > 0, Model.Length: > 0, ApiKeySet: true }
            || (modelSection is not null &&
                !string.IsNullOrWhiteSpace(modelSection["ApiUrl"]) &&
                !string.IsNullOrWhiteSpace(modelSection["ApiKey"]) &&
                !string.IsNullOrWhiteSpace(modelSection["Model"]));
        var preflightMode = configuration is not null
            ? HostPreflight.ResolvePreflightMode(configuration).ToString()
            : LocalPreflightMode.Fail.ToString();

        return Ok(new SystemStatusSummaryDto(
            Version: version,
            ExecutionBackend: backend.ToString(),
            PermissionMode: mode.ToString(),
            ControlPlaneAvailable: controlPlane?.IsControlPlaneAvailable ?? true,
            SelfApprovalProtection: controlPlane?.SelfApprovalProtection ?? true,
            TokenDeliveryMode: controlPlane?.TokenDeliveryMode ?? "Unknown",
            BackendAvailable: backend == CodexFlow.Core.Execution.ExecutionBackend.Local,
            BackendAvailabilityReason: null,
            DockerAvailable: false,
            DockerAvailabilityReason: null,
            ModelConfigured: modelConfigured,
            ModelReachable: modelConfigured ? null : false,
            ModelProbeStatus: modelConfigured ? "NotProbed" : "NotConfigured",
            PreflightMode: preflightMode,
            PreflightIssues: []));
    }

    private static ModelSettingsDto ToModelDto(ModelSettingsView view)
        => new(
            view.ApiUrl,
            view.ApiMode,
            view.Model,
            view.MaxTokensLength,
            view.Temperature,
            view.TopP,
            view.ApiKeySet,
            view.ApiKeyLast4,
            view.SupportsMultimodal,
            view.MultimodalProbeStatus,
            view.MultimodalProbeMessage);
}
