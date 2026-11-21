// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Gateway.Http;
using Microsoft.Extensions.Options;

namespace CodexFlow.Controllers;

/// <summary>
/// Minimal API endpoint handler for desktop Computer Use state, screenshot streaming,
/// human takeover handoff, emergency stop, self-UI protected window registration, and settings.
/// </summary>
public sealed class ComputerSessionController(
    IComputerSessionManager computerSessionManager,
    IChatService chatService,
    IOptionsMonitor<ComputerOptions> computerOptions,
    ComputerPermissionClamp permissionClamp,
    IComputerSettingsService? computerSettingsService = null,
    IControlPlaneStateProvider? controlPlane = null) : GatewayEndpointHandler
{
    public async Task<IResult> GetComputerState(
        string sessionId,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        return Ok(computerSessionManager.GetSessionState(sessionId));
    }

    public async Task<IResult> GetComputerScreenshot(
        string sessionId,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        if (!computerSessionManager.TryGetLatestScreenshot(sessionId, out var screenshot) || screenshot is null)
        {
            return NoContent();
        }

        var generationId = computerSessionManager.GetSessionGenerationId(sessionId) ?? "g0";
        var etag = string.Create(CultureInfo.InvariantCulture, $"\"cf-cshot-{generationId}-{screenshot.ScreenshotSeq}\"");
        if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) &&
            string.Equals(ifNoneMatch.ToString().Trim(), etag, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers["ETag"] = etag;
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Screenshot-Seq"] = screenshot.ScreenshotSeq.ToString(CultureInfo.InvariantCulture);
        return File(screenshot.Bytes.ToArray(), screenshot.MimeType);
    }

    public async Task<IResult> CloseComputerSession(
        string sessionId,
        CancellationToken ct = default)
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

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        await computerSessionManager.CloseSessionAsync(sessionId, "closed_by_user", ct).ConfigureAwait(false);
        return Ok(new ComputerSessionCloseResponse(true, sessionId));
    }

    public async Task<IResult> StartComputerTakeover(
        string sessionId,
        ComputerTakeoverStartRequest? request = null,
        CancellationToken ct = default)
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

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        var result = await computerSessionManager
            .StartTakeoverAsync(sessionId, request?.Reason, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return BadRequest(new ComputerApiErrorDto(result.ErrorCode ?? "takeover_failed", result.Summary ?? result.Output));
        }

        return Ok(new ComputerTakeoverResponse(true, sessionId, result.Summary));
    }

    public async Task<IResult> CompleteComputerTakeover(
        string sessionId,
        ComputerTakeoverCompleteRequest? request = null,
        CancellationToken ct = default)
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

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        var result = await computerSessionManager
            .CompleteTakeoverAsync(sessionId, request?.Note, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return NotFound(new ComputerApiErrorDto(result.ErrorCode ?? "takeover_complete_failed", result.Summary ?? result.Output));
        }

        return Ok(new ComputerTakeoverResponse(false, sessionId, result.Summary));
    }

    public async Task<IResult> EmergencyStop(
        ComputerEmergencyStopRequest? request = null,
        CancellationToken ct = default)
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

        if (!string.IsNullOrWhiteSpace(request?.SessionId))
        {
            var session = await chatService.GetSession(request.SessionId).ConfigureAwait(false);
            if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
            {
                return NotFound(new ComputerApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
            }
        }

        var result = await computerSessionManager
            .EmergencyStopAsync(request?.SessionId, request?.Reason, ct)
            .ConfigureAwait(false);
        return Ok(result);
    }

    public IResult RegisterProtectedWindows(RegisterProtectedWindowsRequest request)
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        if (request is null || request.ProcessId <= 0)
        {
            return BadRequest(new ComputerApiErrorDto("invalid_request", "A positive processId is required."));
        }

        var result = computerSessionManager.RegisterProtectedWindows(request.ProcessId, request.WindowHandles);
        return Ok(result);
    }

    public IResult GetComputerSettings()
    {
        var effective = computerOptions.CurrentValue;
        var driverStatus = computerSessionManager.GetDriverStatus();
        return Ok(ToDto(effective, driverStatus));
    }

    public IResult UpdateComputerSettings(UpdateComputerSettingsRequest request)
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        if (request is null)
        {
            return BadRequest(new ComputerApiErrorDto("invalid_request", "Request body is required."));
        }

        if (computerSettingsService is null)
        {
            return StatusCode(503, new ComputerApiErrorDto("settings_unavailable", "Computer Use settings persistence is not configured on this host."));
        }

        try
        {
            var updated = computerSettingsService.Update(request);
            permissionClamp.ApplyControlPlaneUpdate(updated);
            var driverStatus = computerSessionManager.GetDriverStatus();
            return Ok(ToDto(updated, driverStatus));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ComputerApiErrorDto("invalid_computer_settings", ex.Message));
        }
    }

    private static ComputerSettingsDto ToDto(ComputerOptions opts, ComputerDriverStatusDto driverStatus)
        => new(
            Enabled: opts.Enabled,
            InteractionApprovalPolicy: opts.InteractionApprovalPolicy.ToString(),
            CoordinateMode: opts.CoordinateMode.ToString(),
            MaxImageLongEdge: opts.MaxImageLongEdge,
            MaxScreenshotsPerTurn: opts.MaxScreenshotsPerTurn,
            EnableGridOverlay: opts.EnableGridOverlay,
            AutoMinimizeHostWindow: opts.AutoMinimizeHostWindow,
            BlockedProcessNames: opts.BlockedProcessNames.ToArray(),
            EmergencyStopShortcut: opts.EmergencyStopShortcut,
            IdleTimeoutMinutes: opts.IdleTimeoutMinutes,
            DriverStatus: driverStatus);
}
