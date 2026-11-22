// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Remote;
using CodexFlow.Orchestration.Persistence;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.ComputerUse;

/// <summary>
/// Control-plane approval coordinator for desktop Computer Use interactions.
/// Preserves CodexFlow's hardened approval invariants:
/// - Fail-fast when control plane is unavailable;
/// - Shorter timeout when no SSE subscribers are connected;
/// - Pauses turn timers via <see cref="AmbientTurnTimeoutController"/> while awaiting human decision;
/// - Single-use consumption + immutable SHA-256 requestDigest verification + turn cancellation cleanup.
/// </summary>
public sealed class ComputerApprovalCoordinator : IComputerApprovalCoordinator
{
    public const string ApprovalIdPrefix = "cappr-";

    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoSubscriberTimeout = TimeSpan.FromSeconds(30);

    private readonly ICodexGateway? _gateway;
    private readonly IControlPlaneStateProvider? _controlPlane;
    private readonly ILogger<ComputerApprovalCoordinator> _logger;
    private readonly IRemotePendingProjection? _remotePendingProjection;

    private readonly ConcurrentDictionary<string, PendingComputerApprovalEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionGrants = new(StringComparer.Ordinal);

    public ComputerApprovalCoordinator(
        ILogger<ComputerApprovalCoordinator> logger,
        ICodexGateway? gateway = null,
        IControlPlaneStateProvider? controlPlane = null,
        IRemotePendingProjection? remotePendingProjection = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _gateway = gateway;
        _controlPlane = controlPlane;
        _remotePendingProjection = remotePendingProjection;
    }

    public bool HasApproval(string approvalId)
        => !string.IsNullOrWhiteSpace(approvalId) &&
           (_pending.ContainsKey(approvalId) || approvalId.StartsWith(ApprovalIdPrefix, StringComparison.Ordinal));

    public string? TryGetApprovalSessionId(string approvalId)
        => !string.IsNullOrWhiteSpace(approvalId) && _pending.TryGetValue(approvalId, out var entry)
            ? entry.Request.SessionId
            : null;

    public bool IsSessionProcessApproved(string sessionId, string processName)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return _sessionGrants.TryGetValue(sessionId, out var grants) &&
               grants.ContainsKey(BuildGrantKey(processName));
    }

    public async Task<ComputerApprovalOutcome> RequestApprovalAsync(
        ComputerApprovalPrompt prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var controlPlaneAvailable = _controlPlane?.IsControlPlaneAvailable ?? true;
        if (!controlPlaneAvailable)
        {
            return new ComputerApprovalOutcome(
                Approved: false,
                Denied: true,
                ExpiredOrCancelled: false,
                GrantedForSession: false,
                ErrorCode: "control_plane_unavailable",
                ErrorMessage: "当前处于未启用控制面的独立模式，无法进行桌面操控人工审批。请改用桌面托管模式或调整安全策略。");
        }

        var hasSubscribers = _gateway is null || _gateway.HasActiveSubscribers(prompt.SessionId);
        var timeout = hasSubscribers ? DefaultApprovalTimeout : NoSubscriberTimeout;
        var approvalId = ApprovalIdPrefix + Guid.NewGuid().ToString("N")[..16];
        var expiresAt = DateTimeOffset.UtcNow.Add(timeout);
        var originChannel = RemoteExecutionAmbientContext.Current?.Channel ?? ControlChannel.Local;
        var allowSessionScope = originChannel != ControlChannel.Remote && prompt.AllowSessionScope;
        var allowedDecisions = allowSessionScope
            ? new[] { "allow_once", "allow_session", "deny" }
            : new[] { "allow_once", "deny" };

        var requestDigest = ComputeDigest(prompt, allowedDecisions);
        var dto = new ComputerApprovalRequestDto(
            ApprovalId: approvalId,
            RequestDigest: requestDigest,
            SessionId: prompt.SessionId,
            Tool: prompt.Tool,
            Action: prompt.Action,
            TargetProcessName: prompt.TargetProcessName,
            TargetWindowTitle: prompt.TargetWindowTitle,
            TargetWindowHandle: prompt.TargetWindowHandle,
            ActionSummary: prompt.ActionSummary,
            RiskReason: prompt.RiskReason,
            TargetX: prompt.TargetX,
            TargetY: prompt.TargetY,
            ExpiresAtUtc: expiresAt,
            AllowedDecisions: allowedDecisions,
            ControlPlaneAvailable: controlPlaneAvailable,
            SelfApprovalProtection: _controlPlane?.SelfApprovalProtection ?? true,
            PreviewImageBase64: prompt.PreviewImageBase64);

        var entry = new PendingComputerApprovalEntry(dto, allowSessionScope, originChannel);
        _pending[approvalId] = entry;

        await PublishToRemoteProjectionAsync(dto, originChannel, "pending", isTerminal: false, null, CancellationToken.None).ConfigureAwait(false);
        await PublishEventAsync(
            prompt.SessionId,
            GatewaySseEventType.ComputerApprovalRequested,
            dto,
            CancellationToken.None).ConfigureAwait(false);

        using var pauseGuard = AmbientTurnTimeoutController.Current?.PauseTimers();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await entry.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelledOutcome = new ComputerApprovalOutcome(
                Approved: false,
                Denied: false,
                ExpiredOrCancelled: true,
                GrantedForSession: false,
                ErrorCode: "approval_cancelled",
                ErrorMessage: "桌面操控审批已随回合取消或紧急停止而终止。");

            if (_pending.TryRemove(approvalId, out var removed) && removed.Tcs.TrySetResult(cancelledOutcome))
            {
                await PublishResolvedAsync(dto, removed.OriginChannel, "cancelled", "system", "Turn cancelled.").ConfigureAwait(false);
            }

            return cancelledOutcome;
        }
        catch (OperationCanceledException)
        {
            var msg = hasSubscribers
                ? "桌面操控审批已超时（5 分钟内未收到确认）。"
                : "当前会话无活跃前端订阅者，桌面操控审批在 30 秒后自动超时。";

            var expiredOutcome = new ComputerApprovalOutcome(
                Approved: false,
                Denied: false,
                ExpiredOrCancelled: true,
                GrantedForSession: false,
                ErrorCode: "approval_expired",
                ErrorMessage: msg);

            if (_pending.TryRemove(approvalId, out var removed) && removed.Tcs.TrySetResult(expiredOutcome))
            {
                await PublishResolvedAsync(dto, removed.OriginChannel, "expired", "system", "Approval timed out.").ConfigureAwait(false);
            }

            return expiredOutcome;
        }
    }

    public async Task<ApprovalSubmissionResult?> TrySubmitDecisionAsync(
        string approvalId,
        string requestDigest,
        ExecutionApprovalDecisionKind decision,
        string? decidedBy,
        string? note,
        CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(approvalId) || !_pending.TryGetValue(approvalId, out var entry))
        {
            return null;
        }

        if (entry.Request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (_pending.TryRemove(approvalId, out var expiredEntry))
            {
                var expiredOutcome = new ComputerApprovalOutcome(
                    Approved: false,
                    Denied: false,
                    ExpiredOrCancelled: true,
                    GrantedForSession: false,
                    ErrorCode: "approval_expired",
                    ErrorMessage: "桌面操控审批请求已过期。");
                if (expiredEntry.Tcs.TrySetResult(expiredOutcome))
                {
                    await PublishResolvedAsync(expiredEntry.Request, expiredEntry.OriginChannel, "expired", "system", "Approval expired before submission.").ConfigureAwait(false);
                }
            }

            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Computer approval request has expired.");
        }

        if (string.IsNullOrWhiteSpace(requestDigest) ||
            !string.Equals(entry.Request.RequestDigest, requestDigest.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DigestMismatch,
                "Computer approval request digest mismatch.");
        }

        if (decision == ExecutionApprovalDecisionKind.AllowAlways)
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                "Computer Use approvals do not permit allow_always; use allow_once, allow_session, or deny.");
        }

        if (RemoteExecutionAmbientContext.IsRemote)
        {
            if (decision == ExecutionApprovalDecisionKind.AllowSession)
            {
                return new ApprovalSubmissionResult(
                    ApprovalSubmissionOutcome.DecisionNotAllowed,
                    "Remote channel only permits allow_once or deny for Computer Use approvals.");
            }

            if (entry.OriginChannel != ControlChannel.Remote && decision != ExecutionApprovalDecisionKind.Deny)
            {
                return new ApprovalSubmissionResult(
                    ApprovalSubmissionOutcome.DecisionNotAllowed,
                    "Remote channel may only deny Computer Use approvals initiated locally.");
            }
        }

        if (decision == ExecutionApprovalDecisionKind.AllowSession && !entry.AllowSessionScope)
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                "AlwaysAsk policy only permits allow_once or deny for Computer Use actions.");
        }

        if (!_pending.TryRemove(approvalId, out var removed))
        {
            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Computer approval was already resolved.");
        }

        var approved = decision is ExecutionApprovalDecisionKind.AllowOnce or ExecutionApprovalDecisionKind.AllowSession;
        var grantedSession = decision == ExecutionApprovalDecisionKind.AllowSession;
        var wireDecision = ExecutionApprovalBroker.ToWireDecision(decision);

        var applied = removed.Tcs.TrySetResult(new ComputerApprovalOutcome(
            Approved: approved,
            Denied: !approved,
            ExpiredOrCancelled: false,
            GrantedForSession: grantedSession,
            ErrorCode: approved ? null : "approval_denied",
            ErrorMessage: approved ? null : (string.IsNullOrWhiteSpace(note) ? "用户拒绝了该桌面操控操作。" : $"用户拒绝了该桌面操控操作：{note}")));

        if (!applied)
        {
            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Computer approval was already resolved or expired.");
        }

        if (grantedSession && !string.IsNullOrWhiteSpace(removed.Request.TargetProcessName))
        {
            var grants = _sessionGrants.GetOrAdd(
                removed.Request.SessionId,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            grants[BuildGrantKey(removed.Request.TargetProcessName)] = 0;
        }

        await PublishResolvedAsync(removed.Request, removed.OriginChannel, wireDecision, decidedBy ?? "user", note).ConfigureAwait(false);
        return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.Resolved, $"Computer approval resolved with '{wireDecision}'.");
    }

    public IReadOnlyList<ComputerApprovalRequestDto> GetPendingApprovalsForSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var list = new List<ComputerApprovalRequestDto>();
        foreach (var entry in _pending.Values)
        {
            if (string.Equals(entry.Request.SessionId, sessionId, StringComparison.Ordinal) &&
                entry.Request.ExpiresAtUtc > now)
            {
                list.Add(entry.Request);
            }
        }

        return list;
    }

    public void CancelSessionApprovals(string sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        foreach (var entry in _pending.Values.Where(e => string.Equals(e.Request.SessionId, sessionId, StringComparison.Ordinal)).ToArray())
        {
            if (_pending.TryRemove(entry.Request.ApprovalId, out var removed))
            {
                removed.Tcs.TrySetResult(new ComputerApprovalOutcome(
                    Approved: false,
                    Denied: false,
                    ExpiredOrCancelled: true,
                    GrantedForSession: false,
                    ErrorCode: "approval_cancelled",
                    ErrorMessage: reason));
                _ = PublishResolvedAsync(removed.Request, removed.OriginChannel, "cancelled", "system", reason);
            }
        }
    }

    public void ClearSessionAuthorizations(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessionGrants.TryRemove(sessionId, out _);
        }
    }

    private async Task PublishResolvedAsync(
        ComputerApprovalRequestDto request,
        ControlChannel originChannel,
        string decision,
        string decidedBy,
        string? note)
    {
        await PublishToRemoteProjectionAsync(request, originChannel, decision, isTerminal: true, note, CancellationToken.None).ConfigureAwait(false);
        await PublishEventAsync(
            request.SessionId,
            GatewaySseEventType.ComputerApprovalResolved,
            new Dictionary<string, object?>
            {
                ["approvalId"] = request.ApprovalId,
                ["sessionId"] = request.SessionId,
                ["decision"] = decision,
                ["decidedBy"] = decidedBy,
                ["note"] = note,
                ["resolvedAtUtc"] = DateTimeOffset.UtcNow
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PublishEventAsync(string sessionId, string eventType, object payload, CancellationToken ct)
    {
        if (_gateway is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            await _gateway.PublishEventAsync(
                sessionId,
                new GatewaySseEvent { Type = eventType, Payload = payload },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed publishing SSE event {EventType} for session {SessionId}", eventType, sessionId);
        }
    }

    private static string ComputeDigest(ComputerApprovalPrompt prompt, IReadOnlyList<string> allowedDecisions)
    {
        var payload = new ComputerApprovalDigestPayload(
            "cf-computer-approval-v1",
            prompt.SessionId,
            prompt.Tool,
            prompt.Action,
            prompt.TargetProcessName,
            prompt.TargetWindowTitle,
            prompt.TargetWindowHandle,
            prompt.ActionSummary,
            prompt.TargetX,
            prompt.TargetY,
            allowedDecisions);
        var canonical = JsonSerializer.Serialize(payload, OrchestrationSnapshotJsonContext.CamelCase.ComputerApprovalDigestPayload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildGrantKey(string processName)
        => ComputerPermissionClamp.NormalizeSingleProcessName(processName);

    private async Task PublishToRemoteProjectionAsync(
        ComputerApprovalRequestDto request,
        ControlChannel originChannel,
        string status,
        bool isTerminal,
        string? resolvedReason,
        CancellationToken ct)
    {
        if (_remotePendingProjection is null)
        {
            return;
        }

        try
        {
            // Model A: never project PreviewImageBase64 into mobile remote TODO stream or summaries.
            await _remotePendingProjection.PublishAsync(
                sourceKind: "computer_approval",
                opaqueRefId: request.ApprovalId,
                sessionId: request.SessionId,
                title: $"{request.Tool}: {request.Action}",
                summary: request.ActionSummary,
                status: status,
                isTerminal: isTerminal,
                readOnlyInRemote: originChannel != ControlChannel.Remote,
                expiresAtUtc: request.ExpiresAtUtc,
                resolvedReason: resolvedReason,
                approvalId: request.ApprovalId,
                requestDigest: request.RequestDigest,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to update remote pending projection for computer approval {ApprovalId}.", request.ApprovalId);
        }
    }

    private sealed class PendingComputerApprovalEntry(
        ComputerApprovalRequestDto request,
        bool allowSessionScope,
        ControlChannel originChannel)
    {
        public ComputerApprovalRequestDto Request { get; } = request;
        public bool AllowSessionScope { get; } = allowSessionScope;
        public ControlChannel OriginChannel { get; } = originChannel;
        public TaskCompletionSource<ComputerApprovalOutcome> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
