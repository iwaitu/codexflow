// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodexFlow.Controllers;

public sealed record SubmitExecutionApprovalRequest(
    string RequestDigest,
    string Decision,
    string? Note = null);

public sealed record SubmitExecutionApprovalResponse(
    bool Success,
    string ApprovalId,
    string Decision,
    string Message);

public sealed record ExecutionApprovalErrorResponse(
    bool Success,
    string Code,
    string Message,
    string? ExpectedRequestDigest = null);

public sealed record PendingApprovalsResponse(
    string SessionId,
    int Count,
    IReadOnlyList<PendingApprovalItem> Items,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection);

/// <summary>
/// Interactive execution approval reconciliation (normal plane) and decision submission (control plane).
/// </summary>
[ApiController]
[Route("api/gateway")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class GatewayApprovalController(
    IExecutionApprovalBroker approvalBroker,
    IChatService chatService,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    /// <summary>
    /// Returns all currently pending execution approval items for the owned session so clients can reconcile
    /// after SSE reconnects, <c>replay_gap</c> markers, or heartbeat count mismatches.
    /// </summary>
    [HttpGet("session/{sessionId}/approvals")]
    [Authorize(Policy = "ActiveUser")]
    public async Task<ActionResult<PendingApprovalsResponse>> GetPendingApprovals(
        [FromRoute] string sessionId,
        CancellationToken ct)
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
            return NotFound(new ExecutionApprovalErrorResponse(false, "session_not_found", "Session does not exist or is not owned by the caller."));
        }

        var items = approvalBroker.GetPendingApprovals(sessionId);
        return Ok(new PendingApprovalsResponse(
            sessionId,
            items.Count,
            items,
            controlPlane?.IsControlPlaneAvailable ?? true,
            controlPlane?.SelfApprovalProtection ?? true));
    }

    /// <summary>
    /// Submits an authorization decision for a pending execution approval item. Requires a control-plane credential;
    /// normal access tokens receive <c>403 Forbidden</c>.
    /// </summary>
    [HttpPost("approvals/{approvalId}")]
    [Authorize(Policy = ControlPlaneAuthConstants.ControlPlanePolicy)]
    public async Task<IActionResult> SubmitApprovalDecision(
        [FromRoute] string approvalId,
        [FromBody] SubmitExecutionApprovalRequest request,
        CancellationToken ct)
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

        if (request is null || string.IsNullOrWhiteSpace(request.RequestDigest))
        {
            return BadRequest(new ExecutionApprovalErrorResponse(false, "invalid_request", "requestDigest is required."));
        }

        if (!ExecutionApprovalBroker.TryParseWireDecision(request.Decision, out var decisionKind))
        {
            return BadRequest(new ExecutionApprovalErrorResponse(
                false,
                "invalid_decision",
                "decision must be one of: allow_once, allow_session, allow_always, deny."));
        }

        var existingItem = approvalBroker.GetPendingApprovalById(approvalId);
        if (existingItem is not null)
        {
            var session = await chatService.GetSession(existingItem.SessionId).ConfigureAwait(false);
            if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
            {
                return NotFound(new ExecutionApprovalErrorResponse(
                    false,
                    "approval_not_found",
                    "Approval request was not found or is not owned by the caller."));
            }
        }

        var submission = new ExecutionApprovalDecisionSubmission(
            request.RequestDigest,
            decisionKind,
            DecidedBy: userId,
            Note: request.Note);

        var outcome = await approvalBroker.SubmitDecisionAsync(approvalId, submission, ct).ConfigureAwait(false);
        return outcome.Outcome switch
        {
            ApprovalSubmissionOutcome.Resolved => Ok(new SubmitExecutionApprovalResponse(
                true,
                approvalId,
                ExecutionApprovalBroker.ToWireDecision(decisionKind),
                outcome.Message)),
            ApprovalSubmissionOutcome.DigestMismatch => Conflict(new ExecutionApprovalErrorResponse(
                false,
                "digest_mismatch",
                outcome.Message,
                outcome.Item?.RequestDigest)),
            ApprovalSubmissionOutcome.AlreadyResolvedOrExpired => Conflict(new ExecutionApprovalErrorResponse(
                false,
                "already_resolved_or_expired",
                outcome.Message)),
            ApprovalSubmissionOutcome.TransientFailure => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ExecutionApprovalErrorResponse(
                    false,
                    "transient_failure",
                    outcome.Message,
                    outcome.Item?.RequestDigest)),
            ApprovalSubmissionOutcome.DecisionNotAllowed => BadRequest(new ExecutionApprovalErrorResponse(
                false,
                "decision_not_allowed",
                outcome.Message)),
            _ => NotFound(new ExecutionApprovalErrorResponse(
                false,
                "approval_not_found",
                outcome.Message))
        };
    }
}
