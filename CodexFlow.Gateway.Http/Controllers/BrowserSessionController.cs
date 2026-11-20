// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodexFlow.Controllers;

public sealed record BrowserSessionCloseResponse(
    bool Closed,
    string SessionId);

public sealed record BrowserApiErrorDto(
    string Code,
    string Message);

/// <summary>
/// Endpoints for inspecting per-session managed browser state/screenshots, closing browser contexts,
/// and reading/updating browser settings (with control-plane protection).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class BrowserSessionController(
    IBrowserSessionManager browserSessionManager,
    IChatService chatService,
    IBrowserExecutableLocator executableLocator,
    IOptionsMonitor<BrowserOptions> browserOptions,
    BrowserPermissionClamp permissionClamp,
    IBrowserSettingsService? browserSettingsService = null,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    [HttpGet("api/gateway/session/{sessionId}/browser")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(BrowserSessionStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrowserSessionStateDto>> GetBrowserState(
        [FromRoute] string sessionId,
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
            return NotFound(new BrowserApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        return Ok(browserSessionManager.GetSessionState(sessionId));
    }

    [HttpGet("api/gateway/session/{sessionId}/browser/screenshot")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBrowserScreenshot(
        [FromRoute] string sessionId,
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
            return NotFound(new BrowserApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        if (!browserSessionManager.TryGetLatestScreenshot(sessionId, out var screenshot) || screenshot is null)
        {
            return NoContent();
        }

        var generationId = browserSessionManager.GetSessionGenerationId(sessionId) ?? "g0";
        var etag = string.Create(CultureInfo.InvariantCulture, $"\"cf-bshot-{generationId}-{screenshot.ScreenshotSeq}\"");
        if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) &&
            string.Equals(ifNoneMatch.ToString().Trim(), etag, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        Response.Headers["X-Screenshot-Seq"] = screenshot.ScreenshotSeq.ToString(CultureInfo.InvariantCulture);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "no-store";
        return File(screenshot.Bytes.ToArray(), screenshot.MimeType);
    }

    [HttpPost("api/gateway/session/{sessionId}/browser/close")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BrowserSessionCloseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrowserSessionCloseResponse>> CloseBrowserSession(
        [FromRoute] string sessionId,
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
            return NotFound(new BrowserApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        await browserSessionManager.CloseSessionAsync(sessionId, "closed_by_user", ct).ConfigureAwait(false);
        return Ok(new BrowserSessionCloseResponse(true, sessionId));
    }

    [HttpPost("api/gateway/session/{sessionId}/browser/takeover/start")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BrowserTakeoverResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrowserTakeoverResponse>> StartBrowserTakeover(
        [FromRoute] string sessionId,
        [FromBody] BrowserTakeoverStartRequest? request = null,
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
            return NotFound(new BrowserApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        var result = await browserSessionManager
            .StartTakeoverAsync(sessionId, request?.Reason, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var code = result.ErrorCode ?? "takeover_failed";
            if (string.Equals(code, "session_not_found", StringComparison.Ordinal))
            {
                return NotFound(new BrowserApiErrorDto(code, result.Summary ?? result.Output));
            }

            return BadRequest(new BrowserApiErrorDto(code, result.Summary ?? result.Output));
        }

        return Ok(new BrowserTakeoverResponse(true, sessionId, result.Summary));
    }

    [HttpPost("api/gateway/session/{sessionId}/browser/takeover/complete")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BrowserTakeoverResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrowserTakeoverResponse>> CompleteBrowserTakeover(
        [FromRoute] string sessionId,
        [FromBody] BrowserTakeoverCompleteRequest? request = null,
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
            return NotFound(new BrowserApiErrorDto("session_not_found", "Session does not exist or is not owned by the caller."));
        }

        var result = await browserSessionManager
            .CompleteTakeoverAsync(sessionId, request?.Note, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return NotFound(new BrowserApiErrorDto(result.ErrorCode ?? "takeover_complete_failed", result.Summary ?? result.Output));
        }

        return Ok(new BrowserTakeoverResponse(false, sessionId, result.Summary));
    }

    [HttpGet("api/settings/browser")]
    [Authorize(Policy = "ActiveUser")]
    [ProducesResponseType(typeof(BrowserSettingsDto), StatusCodes.Status200OK)]
    public ActionResult<BrowserSettingsDto> GetBrowserSettings()
    {
        var effective = browserOptions.CurrentValue;
        var detected = executableLocator.Detect(effective.ExecutablePath);
        return Ok(ToDto(effective, detected));
    }

    [HttpPut("api/settings/browser")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    [ProducesResponseType(typeof(BrowserSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(BrowserApiErrorDto), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<BrowserSettingsDto> UpdateBrowserSettings([FromBody] UpdateBrowserSettingsRequest request)
    {
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        if (request is null)
        {
            return BadRequest(new BrowserApiErrorDto("invalid_request", "Request body is required."));
        }

        if (browserSettingsService is null)
        {
            return StatusCode(503, new BrowserApiErrorDto("settings_unavailable", "Browser settings persistence is not configured on this host."));
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            var probe = executableLocator.Detect(request.ExecutablePath);
            if (!probe.Available)
            {
                return BadRequest(new BrowserApiErrorDto("invalid_executable_path", probe.DiagnosticMessage ?? "指定的浏览器可执行文件不存在或无效。"));
            }
        }

        try
        {
            var updated = browserSettingsService.Update(request);
            permissionClamp.ApplyControlPlaneUpdate(updated);
            var detected = executableLocator.Detect(updated.ExecutablePath);
            return Ok(ToDto(updated, detected));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new BrowserApiErrorDto("invalid_browser_settings", ex.Message));
        }
    }

    private static BrowserSettingsDto ToDto(BrowserOptions opts, BrowserExecutableStatusDto detected)
        => new(
            Enabled: opts.Enabled,
            ExecutablePath: opts.ExecutablePath,
            Headless: opts.Headless,
            ProfileMode: opts.ProfileMode.ToString(),
            InteractionApprovalPolicy: opts.InteractionApprovalPolicy.ToString(),
            AllowLocalhostInDailyMode: opts.AllowLocalhostInDailyMode,
            DevelopmentAllowedLoopbackPorts: opts.DevelopmentAllowedLoopbackPorts.ToArray(),
            AllowedDomains: opts.AllowedDomains.ToArray(),
            BlockedDomains: opts.BlockedDomains.ToArray(),
            IdleTimeoutMinutes: opts.IdleTimeoutMinutes,
            MaxConcurrentSessions: opts.MaxConcurrentSessions,
            DetectedBrowser: detected,
            AllowControlledDownloads: opts.AllowControlledDownloads);
}
