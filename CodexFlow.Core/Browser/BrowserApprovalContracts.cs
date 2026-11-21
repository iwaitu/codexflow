// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Execution.Approval;

namespace CodexFlow.Core.Browser;

public sealed record BrowserApprovalPrompt(
    string SessionId,
    string Tool,
    string Action,
    string TargetAddress,
    string TargetOrigin,
    string TargetDescription,
    string RiskReason,
    string? ExpressionOrPath,
    bool AllowSessionScope);

public sealed record BrowserApprovalOutcome(
    bool Approved,
    bool Denied,
    bool ExpiredOrCancelled,
    bool GrantedForSession,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Control-plane approval coordinator for sensitive browser interactions and non-allowlisted dev loopback ports.
/// Plugs directly into <c>GatewayApprovalController</c> (POST /api/gateway/approvals/{id}) and QRE turn cancellation.
/// </summary>
public interface IBrowserApprovalCoordinator
{
    bool HasApproval(string approvalId);

    string? TryGetApprovalSessionId(string approvalId);

    bool IsSessionOriginApproved(string sessionId, string origin, string actionCategory);

    Task<BrowserApprovalOutcome> RequestApprovalAsync(
        BrowserApprovalPrompt prompt,
        CancellationToken ct = default);

    Task<ApprovalSubmissionResult?> TrySubmitDecisionAsync(
        string approvalId,
        string requestDigest,
        ExecutionApprovalDecisionKind decision,
        string? decidedBy,
        string? note,
        CancellationToken ct = default);

    IReadOnlyList<BrowserApprovalRequestDto> GetPendingApprovalsForSession(string sessionId);

    void CancelSessionApprovals(string sessionId, string reason);

    void ClearSessionAuthorizations(string sessionId);
}

/// <summary>
/// Publishes throttled browser state snapshots to the Gateway SSE channel for a session.
/// </summary>
public interface IBrowserStateEventPublisher
{
    ValueTask PublishStateUpdatedAsync(string sessionId, BrowserSessionStateDto state, CancellationToken ct = default);
}
