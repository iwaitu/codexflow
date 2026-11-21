// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.Execution.Approval;

namespace CodexFlow.Core.ComputerUse;

public sealed record ComputerApprovalPrompt(
    string SessionId,
    string Tool,
    string Action,
    string TargetProcessName,
    string TargetWindowTitle,
    string TargetWindowHandle,
    string ActionSummary,
    string RiskReason,
    int? TargetX,
    int? TargetY,
    bool AllowSessionScope,
    string? PreviewImageBase64 = null);

public sealed record ComputerApprovalOutcome(
    bool Approved,
    bool Denied,
    bool ExpiredOrCancelled,
    bool GrantedForSession,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Control-plane approval coordinator for desktop Computer Use actions.
/// Reuses <c>GatewayApprovalController</c> (POST /api/gateway/approvals/{id}) and QRE turn cancellation.
/// </summary>
public interface IComputerApprovalCoordinator
{
    bool HasApproval(string approvalId);

    string? TryGetApprovalSessionId(string approvalId);

    bool IsSessionProcessApproved(string sessionId, string processName);

    Task<ComputerApprovalOutcome> RequestApprovalAsync(
        ComputerApprovalPrompt prompt,
        CancellationToken ct = default);

    Task<ApprovalSubmissionResult?> TrySubmitDecisionAsync(
        string approvalId,
        string requestDigest,
        ExecutionApprovalDecisionKind decision,
        string? decidedBy,
        string? note,
        CancellationToken ct = default);

    IReadOnlyList<ComputerApprovalRequestDto> GetPendingApprovalsForSession(string sessionId);

    void CancelSessionApprovals(string sessionId, string reason);

    void ClearSessionAuthorizations(string sessionId);
}

/// <summary>
/// Publishes throttled Computer Use state snapshots to the Gateway SSE channel for a session.
/// </summary>
public interface IComputerStateEventPublisher
{
    ValueTask PublishStateUpdatedAsync(string sessionId, ComputerSessionStateDto state, CancellationToken ct = default);
}

/// <summary>
/// Allows the desktop Computer Use guard to check whether ANY subsystem (shell/plan/browser/business/computer)
/// currently has an unresolved approval pending for the session so desktop input stays frozen during approvals.
/// </summary>
public interface IComputerSessionApprovalFreezeProbe
{
    bool HasAnyPendingApproval(string sessionId);
}
