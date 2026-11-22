// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.Orchestration.Persistence;
using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Remote;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.Browser;

/// <summary>
/// Control-plane approval coordinator for sensitive browser interactions and non-allowlisted dev loopback ports.
/// Preserves CodexFlow's hardened approval invariants:
/// - Fail-fast when control plane is unavailable;
/// - Shorter timeout when no SSE subscribers are connected;
/// - Pauses turn timers via <see cref="AmbientTurnTimeoutController"/> while awaiting human decision;
/// - Single-use consumption + immutable SHA-256 requestDigest verification + turn cancellation cleanup.
/// </summary>
public sealed class BrowserApprovalCoordinator : IBrowserApprovalCoordinator
{
    public const string ApprovalIdPrefix = "brapr-";

    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoSubscriberTimeout = TimeSpan.FromSeconds(30);

    private readonly ICodexGateway? _gateway;
    private readonly IControlPlaneStateProvider? _controlPlane;
    private readonly ILogger<BrowserApprovalCoordinator> _logger;
    private readonly IRemotePendingProjection? _remotePendingProjection;

    private readonly ConcurrentDictionary<string, PendingBrowserApprovalEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionGrants = new(StringComparer.Ordinal);

    public BrowserApprovalCoordinator(
        ILogger<BrowserApprovalCoordinator> logger,
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

    public bool IsSessionOriginApproved(string sessionId, string origin, string actionCategory)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        return _sessionGrants.TryGetValue(sessionId, out var grants) &&
               grants.ContainsKey(BuildGrantKey(origin, actionCategory));
    }

    public async Task<BrowserApprovalOutcome> RequestApprovalAsync(
        BrowserApprovalPrompt prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var controlPlaneAvailable = _controlPlane?.IsControlPlaneAvailable ?? true;
        if (!controlPlaneAvailable)
        {
            return new BrowserApprovalOutcome(
                Approved: false,
                Denied: true,
                ExpiredOrCancelled: false,
                GrantedForSession: false,
                ErrorCode: "control_plane_unavailable",
                ErrorMessage: "当前处于未启用控制面的独立模式，无法进行浏览器高风险操作审批。请改用桌面托管模式或调整浏览器安全策略。");
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
        var dto = new BrowserApprovalRequestDto(
            ApprovalId: approvalId,
            RequestDigest: requestDigest,
            SessionId: prompt.SessionId,
            Tool: prompt.Tool,
            Action: prompt.Action,
            TargetUrl: prompt.TargetAddress,
            TargetOrigin: prompt.TargetOrigin,
            TargetDescription: prompt.TargetDescription,
            RiskReason: prompt.RiskReason,
            ExpressionOrPath: prompt.ExpressionOrPath,
            ExpiresAtUtc: expiresAt,
            AllowedDecisions: allowedDecisions,
            ControlPlaneAvailable: controlPlaneAvailable,
            SelfApprovalProtection: _controlPlane?.SelfApprovalProtection ?? true);

        var entry = new PendingBrowserApprovalEntry(dto, allowSessionScope, originChannel);
        _pending[approvalId] = entry;

        await PublishToRemoteProjectionAsync(dto, originChannel, "pending", isTerminal: false, null, CancellationToken.None).ConfigureAwait(false);
        await PublishEventAsync(
            prompt.SessionId,
            GatewaySseEventType.BrowserApprovalRequested,
            dto,
            CancellationToken.None).ConfigureAwait(false);

        using var pauseGuard = AmbientTurnTimeoutController.Current?.PauseTimers();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var outcome = await entry.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelledOutcome = new BrowserApprovalOutcome(
                Approved: false,
                Denied: false,
                ExpiredOrCancelled: true,
                GrantedForSession: false,
                ErrorCode: "approval_cancelled",
                ErrorMessage: "浏览器审批已随回合取消而终止。");

            if (_pending.TryRemove(approvalId, out var removed) && removed.Tcs.TrySetResult(cancelledOutcome))
            {
                await PublishResolvedAsync(dto, removed.OriginChannel, "cancelled", "system", "Turn cancelled.").ConfigureAwait(false);
            }

            return cancelledOutcome;
        }
        catch (OperationCanceledException)
        {
            var msg = hasSubscribers
                ? "浏览器高风险操作审批已超时（5 分钟内未收到确认）。"
                : "当前会话无活跃前端订阅者，浏览器高风险操作审批在 30 秒后自动超时。";

            var expiredOutcome = new BrowserApprovalOutcome(
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
                var expiredOutcome = new BrowserApprovalOutcome(
                    Approved: false,
                    Denied: false,
                    ExpiredOrCancelled: true,
                    GrantedForSession: false,
                    ErrorCode: "approval_expired",
                    ErrorMessage: "浏览器高风险操作审批已过期。");
                if (expiredEntry.Tcs.TrySetResult(expiredOutcome))
                {
                    await PublishResolvedAsync(expiredEntry.Request, expiredEntry.OriginChannel, "expired", "system", "Approval expired before submission.").ConfigureAwait(false);
                }
            }

            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Browser approval request has expired.");
        }

        if (!string.Equals(entry.Request.RequestDigest, requestDigest.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DigestMismatch,
                "Browser approval request digest mismatch.");
        }

        if (decision == ExecutionApprovalDecisionKind.AllowAlways)
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                "Browser approvals do not permit allow_always; use allow_once, allow_session, or deny.");
        }

        if (RemoteExecutionAmbientContext.IsRemote)
        {
            if (decision == ExecutionApprovalDecisionKind.AllowSession)
            {
                return new ApprovalSubmissionResult(
                    ApprovalSubmissionOutcome.DecisionNotAllowed,
                    "Remote channel only permits allow_once or deny for browser approvals.");
            }

            if (entry.OriginChannel != ControlChannel.Remote && decision != ExecutionApprovalDecisionKind.Deny)
            {
                return new ApprovalSubmissionResult(
                    ApprovalSubmissionOutcome.DecisionNotAllowed,
                    "Remote channel may only deny browser approvals initiated locally.");
            }
        }

        if (decision == ExecutionApprovalDecisionKind.AllowSession && !entry.AllowSessionScope)
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                "This high-risk browser action only allows allow_once or deny.");
        }

        if (!_pending.TryRemove(approvalId, out var removed))
        {
            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Browser approval was already resolved.");
        }

        var approved = decision is ExecutionApprovalDecisionKind.AllowOnce or ExecutionApprovalDecisionKind.AllowSession;
        var grantedSession = decision == ExecutionApprovalDecisionKind.AllowSession;
        var wireDecision = ExecutionApprovalBroker.ToWireDecision(decision);

        var applied = removed.Tcs.TrySetResult(new BrowserApprovalOutcome(
            Approved: approved,
            Denied: !approved,
            ExpiredOrCancelled: false,
            GrantedForSession: grantedSession,
            ErrorCode: approved ? null : "approval_denied",
            ErrorMessage: approved ? null : (string.IsNullOrWhiteSpace(note) ? "用户拒绝了该浏览器操作。" : $"用户拒绝了该浏览器操作：{note}")));

        if (!applied)
        {
            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, "Browser approval was already resolved or expired.");
        }

        if (grantedSession)
        {
            var grants = _sessionGrants.GetOrAdd(removed.Request.SessionId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            var category = ResolveGrantCategory(removed.Request);
            grants[BuildGrantKey(removed.Request.TargetOrigin, category)] = 0;
        }

        await PublishResolvedAsync(removed.Request, removed.OriginChannel, wireDecision, decidedBy ?? "user", note).ConfigureAwait(false);
        return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.Resolved, $"Browser approval resolved with '{wireDecision}'.");
    }

    public IReadOnlyList<BrowserApprovalRequestDto> GetPendingApprovalsForSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var list = new List<BrowserApprovalRequestDto>();
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
                removed.Tcs.TrySetResult(new BrowserApprovalOutcome(
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
        BrowserApprovalRequestDto request,
        ControlChannel originChannel,
        string decision,
        string decidedBy,
        string? note)
    {
        await PublishToRemoteProjectionAsync(request, originChannel, decision, isTerminal: true, note, CancellationToken.None).ConfigureAwait(false);
        await PublishEventAsync(
            request.SessionId,
            GatewaySseEventType.BrowserApprovalResolved,
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

    private static string ComputeDigest(BrowserApprovalPrompt prompt, IReadOnlyList<string> allowedDecisions)
    {
        var payload = new BrowserApprovalDigestPayload(
            "cf-browser-approval-v2", prompt.SessionId, prompt.Tool, prompt.Action, prompt.TargetOrigin, prompt.TargetAddress,
            prompt.TargetDescription, prompt.ExpressionOrPath ?? string.Empty, allowedDecisions);
        var canonical = JsonSerializer.Serialize(payload, OrchestrationSnapshotJsonContext.CamelCase.BrowserApprovalDigestPayload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveGrantCategory(BrowserApprovalRequestDto req)
    {
        if (string.Equals(req.Action, "persistent_origin_navigation", StringComparison.OrdinalIgnoreCase))
        {
            return "persistent_nav";
        }

        return string.Equals(req.Tool, "browser_navigate", StringComparison.OrdinalIgnoreCase)
            ? "loopback"
            : "interact";
    }

    private static string BuildGrantKey(string origin, string actionCategory)
        => $"{origin.Trim().ToLowerInvariant()}|{actionCategory.Trim().ToLowerInvariant()}";

    private async Task PublishToRemoteProjectionAsync(
        BrowserApprovalRequestDto request,
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
            await _remotePendingProjection.PublishAsync(
                sourceKind: "browser_approval",
                opaqueRefId: request.ApprovalId,
                sessionId: request.SessionId,
                title: $"{request.Tool}: {request.TargetOrigin}",
                summary: string.IsNullOrWhiteSpace(request.TargetDescription) ? request.Action : request.TargetDescription,
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
            _logger.LogDebug(ex, "Failed to update remote pending projection for browser approval {ApprovalId}.", request.ApprovalId);
        }
    }

    private sealed class PendingBrowserApprovalEntry(
        BrowserApprovalRequestDto request,
        bool allowSessionScope,
        ControlChannel originChannel)
    {
        public BrowserApprovalRequestDto Request { get; } = request;
        public bool AllowSessionScope { get; } = allowSessionScope;
        public ControlChannel OriginChannel { get; } = originChannel;
        public TaskCompletionSource<BrowserApprovalOutcome> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
