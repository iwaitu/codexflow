// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Gateway.Http;
using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Contracts.Browser;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.Browser;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Orchestration.BusinessSkills;

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
    bool SelfApprovalProtection,
    IReadOnlyList<BusinessApprovalRequestDto>? BusinessApprovals = null,
    IReadOnlyList<BrowserApprovalRequestDto>? BrowserApprovals = null,
    IReadOnlyList<ComputerApprovalRequestDto>? ComputerApprovals = null);

/// <summary>
/// Interactive execution approval reconciliation (normal plane) and decision submission (control plane).
/// </summary>

public sealed class GatewayApprovalController(
    IExecutionApprovalBroker approvalBroker,
    IChatService chatService,
    IControlPlaneStateProvider? controlPlane = null,
    IBusinessSkillApprovalCoordinator? businessApprovalCoordinator = null,
    IBrowserApprovalCoordinator? browserApprovalCoordinator = null,
    IComputerApprovalCoordinator? computerApprovalCoordinator = null) : GatewayEndpointHandler
{
    /// <summary>
    /// Returns all currently pending execution approval items for the owned session so clients can reconcile
    /// after SSE reconnects, <c>replay_gap</c> markers, or heartbeat count mismatches.
    /// </summary>

    public async Task<IResult> GetPendingApprovals(
        string sessionId,
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
        IReadOnlyList<BusinessApprovalRequestDto> businessApprovals = businessApprovalCoordinator is not null
            ? await businessApprovalCoordinator.GetPendingApprovalsForSessionAsync(sessionId, ct).ConfigureAwait(false)
            : [];
        IReadOnlyList<BrowserApprovalRequestDto> browserApprovals = browserApprovalCoordinator is not null
            ? browserApprovalCoordinator.GetPendingApprovalsForSession(sessionId)
            : [];
        IReadOnlyList<ComputerApprovalRequestDto> computerApprovals = computerApprovalCoordinator is not null
            ? computerApprovalCoordinator.GetPendingApprovalsForSession(sessionId)
            : [];
        return Ok(new PendingApprovalsResponse(
            sessionId,
            items.Count,
            items,
            controlPlane?.IsControlPlaneAvailable ?? true,
            controlPlane?.SelfApprovalProtection ?? true,
            businessApprovals,
            browserApprovals,
            computerApprovals));
    }

    /// <summary>
    /// Returns the full script snapshot and SHA-256 hash for a pending script execution approval.
    /// Requires control-plane credentials and session ownership so the UI can verify script fidelity
    /// independently from SSE preview events.
    /// </summary>

    public async Task<IResult> GetPendingApprovalScript(
        string approvalId,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
        {
            return Forbid();
        }

        var existingItem = approvalBroker.GetPendingApprovalById(approvalId);
        if (existingItem is null)
        {
            return NotFound(new ExecutionApprovalErrorResponse(false, "approval_not_found", "Approval request was not found or has expired."));
        }

        var session = await chatService.GetSession(existingItem.SessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new ExecutionApprovalErrorResponse(false, "approval_not_found", "Approval request was not found or is not owned by the caller."));
        }

        if (!approvalBroker.TryGetPendingApprovalScript(approvalId, session.Id, out var scriptSnapshot) || scriptSnapshot is null)
        {
            return NotFound(new ExecutionApprovalErrorResponse(false, "approval_script_not_found", "Full script snapshot is not available for this approval."));
        }

        return Ok(scriptSnapshot);
    }

    /// <summary>
    /// Submits an authorization decision for a pending execution approval item. Requires a control-plane credential;
    /// normal access tokens receive <c>403 Forbidden</c>.
    /// </summary>

    public async Task<IResult> SubmitApprovalDecision(
        string approvalId,
        SubmitExecutionApprovalRequest request,
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
        else if (businessApprovalCoordinator is not null && businessApprovalCoordinator.HasApproval(approvalId))
        {
            var bizSessionId = await businessApprovalCoordinator
                .TryGetApprovalSessionIdAsync(approvalId, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bizSessionId))
            {
                var bizSession = await chatService.GetSession(bizSessionId).ConfigureAwait(false);
                if (bizSession is null || !string.Equals(bizSession.UserId, userId, StringComparison.Ordinal))
                {
                    return NotFound(new ExecutionApprovalErrorResponse(
                        false,
                        "approval_not_found",
                        "Approval request was not found or is not owned by the caller."));
                }
            }

            var bizOutcome = await businessApprovalCoordinator
                .TrySubmitDecisionAsync(approvalId, request.RequestDigest, decisionKind, userId, request.Note, ct)
                .ConfigureAwait(false);
            if (bizOutcome is not null)
            {
                return MapSubmissionResult(approvalId, decisionKind, bizOutcome);
            }
        }
        else if (browserApprovalCoordinator is not null && browserApprovalCoordinator.HasApproval(approvalId))
        {
            var brSessionId = browserApprovalCoordinator.TryGetApprovalSessionId(approvalId);
            if (string.IsNullOrWhiteSpace(brSessionId))
            {
                return NotFound(new ExecutionApprovalErrorResponse(
                    false,
                    "approval_not_found",
                    "Approval request was not found or has expired."));
            }

            var brSession = await chatService.GetSession(brSessionId).ConfigureAwait(false);
            if (brSession is null || !string.Equals(brSession.UserId, userId, StringComparison.Ordinal))
            {
                return NotFound(new ExecutionApprovalErrorResponse(
                    false,
                    "approval_not_found",
                    "Approval request was not found or is not owned by the caller."));
            }

            var brOutcome = await browserApprovalCoordinator
                .TrySubmitDecisionAsync(approvalId, request.RequestDigest, decisionKind, userId, request.Note, ct)
                .ConfigureAwait(false);
            if (brOutcome is not null)
            {
                return MapSubmissionResult(approvalId, decisionKind, brOutcome);
            }

            return NotFound(new ExecutionApprovalErrorResponse(
                false,
                "approval_not_found",
                "Approval request was not found or has expired."));
        }
        else if (computerApprovalCoordinator is not null && computerApprovalCoordinator.HasApproval(approvalId))
        {
            var cuSessionId = computerApprovalCoordinator.TryGetApprovalSessionId(approvalId);
            if (string.IsNullOrWhiteSpace(cuSessionId))
            {
                return NotFound(new ExecutionApprovalErrorResponse(
                    false,
                    "approval_not_found",
                    "Approval request was not found or has expired."));
            }

            var cuSession = await chatService.GetSession(cuSessionId).ConfigureAwait(false);
            if (cuSession is null || !string.Equals(cuSession.UserId, userId, StringComparison.Ordinal))
            {
                return NotFound(new ExecutionApprovalErrorResponse(
                    false,
                    "approval_not_found",
                    "Approval request was not found or is not owned by the caller."));
            }

            var cuOutcome = await computerApprovalCoordinator
                .TrySubmitDecisionAsync(approvalId, request.RequestDigest, decisionKind, userId, request.Note, ct)
                .ConfigureAwait(false);
            if (cuOutcome is not null)
            {
                return MapSubmissionResult(approvalId, decisionKind, cuOutcome);
            }

            return NotFound(new ExecutionApprovalErrorResponse(
                false,
                "approval_not_found",
                "Approval request was not found or has expired."));
        }

        var submission = new ExecutionApprovalDecisionSubmission(
            request.RequestDigest,
            decisionKind,
            DecidedBy: userId,
            Note: request.Note);

        var outcome = await approvalBroker.SubmitDecisionAsync(approvalId, submission, ct).ConfigureAwait(false);
        return MapSubmissionResult(approvalId, decisionKind, outcome);
    }

    private IResult MapSubmissionResult(
        string approvalId,
        ExecutionApprovalDecisionKind decisionKind,
        ApprovalSubmissionResult outcome)
        => outcome.Outcome switch
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
