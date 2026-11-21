// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.BusinessSkills;

public interface IBusinessSkillApprovalCoordinator
{
    bool HasApproval(string approvalId);

    void RegisterActiveRun(string runId, string pluginId);

    void UnregisterActiveRun(string runId);

    bool IsStopOrInvalidationRequested(string runId);

    void RequestRunStop(string runId, string reason);

    Task<SkillApprovalGrantOutcome> AwaitApprovalAsync(
        string pluginId,
        BusinessApprovalRequestDto request,
        BusinessApprovalDigestInput digestInput,
        CancellationToken ct);

    Task<ApprovalSubmissionResult?> TrySubmitDecisionAsync(
        string approvalId,
        string requestDigest,
        ExecutionApprovalDecisionKind decision,
        string? decidedBy,
        string? note,
        CancellationToken ct = default);

    Task<IReadOnlyList<BusinessApprovalRequestDto>> GetPendingApprovalsForSessionAsync(
        string sessionId,
        CancellationToken ct = default);

    Task<string?> TryGetApprovalSessionIdAsync(
        string approvalId,
        CancellationToken ct = default);

    Task InvalidatePluginApprovalsAndRunsAsync(string pluginId, string reason, CancellationToken ct = default);
}

/// <summary>
/// 业务写入审批与运行中止协调器：
/// - 绑定 12 要素不可变 Digest 与严格 allow_once/deny 策略（绝不允许 allow_session/allow_always）；
/// - 插件停用、连接/凭据轮换或目录变更时即时废止所有待批摘要并阻断后续写入；
/// - 与 GatewayApprovalController 共用审批入口并在携带 SessionId 时发送 SSE 状态通知。
/// </summary>
public sealed class BusinessSkillApprovalCoordinator : IBusinessSkillApprovalCoordinator
{
    private static BusinessSkillJsonContext Json => BusinessSkillJsonContext.Default;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICodexGateway? _gateway;
    private readonly ILogger<BusinessSkillApprovalCoordinator> _logger;

    private readonly ConcurrentDictionary<string, PendingBusinessApprovalEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunState> _activeRuns = new(StringComparer.Ordinal);

    public BusinessSkillApprovalCoordinator(
        IServiceScopeFactory scopeFactory,
        ILogger<BusinessSkillApprovalCoordinator> logger,
        ICodexGateway? gateway = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _gateway = gateway;
    }

    public bool HasApproval(string approvalId)
        => !string.IsNullOrWhiteSpace(approvalId) &&
           (_pending.ContainsKey(approvalId) || approvalId.StartsWith("bapr-", StringComparison.Ordinal));

    public void RegisterActiveRun(string runId, string pluginId)
        => _activeRuns[runId] = new ActiveRunState(pluginId);

    public void UnregisterActiveRun(string runId)
        => _activeRuns.TryRemove(runId, out _);

    public bool IsStopOrInvalidationRequested(string runId)
        => _activeRuns.TryGetValue(runId, out var state) && state.StopOrInvalidated;

    public void RequestRunStop(string runId, string reason)
    {
        if (_activeRuns.TryGetValue(runId, out var state))
        {
            state.MarkStopped(reason);
        }

        foreach (var entry in _pending.Values.Where(p => string.Equals(p.Request.RunId, runId, StringComparison.Ordinal)))
        {
            if (_pending.TryRemove(entry.Request.ApprovalId, out var removed))
            {
                removed.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                    Approved: false,
                    Denied: true,
                    ExpiredOrInvalidated: false,
                    ErrorCode: BusinessSkillErrorCodes.ApprovalRequired,
                    ErrorMessage: reason));
                _ = PublishApprovalResolvedSseAsync(removed.Request, "cancelled", "system", reason, CancellationToken.None);
            }
        }
    }

    public async Task<IReadOnlyList<BusinessApprovalRequestDto>> GetPendingApprovalsForSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var byId = new Dictionary<string, BusinessApprovalRequestDto>(StringComparer.Ordinal);

        foreach (var entry in _pending.Values)
        {
            if (string.Equals(entry.Request.SessionId, sessionId, StringComparison.Ordinal) &&
                entry.Request.ExpiresAtUtc > now)
            {
                byId[entry.Request.ApprovalId] = entry.Request;
            }
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBusinessSkillExecutionStore>();
            var waitingRuns = await store.ListWaitingRunsAsync(ct).ConfigureAwait(false);

            foreach (var run in waitingRuns)
            {
                if (string.IsNullOrWhiteSpace(run.PendingApprovalJson))
                {
                    continue;
                }

                try
                {
                    var req = JsonSerializer.Deserialize(run.PendingApprovalJson, Json.BusinessApprovalRequestDto);
                    if (req is not null &&
                        string.Equals(req.SessionId, sessionId, StringComparison.Ordinal) &&
                        req.ExpiresAtUtc > now &&
                        !byId.ContainsKey(req.ApprovalId))
                    {
                        byId[req.ApprovalId] = req;
                    }
                }
                catch (JsonException)
                {
                    // 忽略损坏的旧审批快照
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed querying pending business approvals for session {SessionId}", sessionId);
        }

        return byId.Values
            .OrderBy(static r => r.ExpiresAtUtc)
            .ThenBy(static r => r.ApprovalId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string?> TryGetApprovalSessionIdAsync(
        string approvalId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return null;
        }

        if (_pending.TryGetValue(approvalId, out var memEntry))
        {
            return memEntry.Request.SessionId;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBusinessSkillExecutionStore>();
            var waitingRuns = await store.ListWaitingRunsAsync(ct).ConfigureAwait(false);

            foreach (var run in waitingRuns)
            {
                if (string.IsNullOrWhiteSpace(run.PendingApprovalJson))
                {
                    continue;
                }

                try
                {
                    var req = JsonSerializer.Deserialize(run.PendingApprovalJson, Json.BusinessApprovalRequestDto);
                    if (req is not null && string.Equals(req.ApprovalId, approvalId, StringComparison.Ordinal))
                    {
                        return req.SessionId;
                    }
                }
                catch (JsonException)
                {
                    // 忽略损坏的快照
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving session id for business approval {ApprovalId}", approvalId);
        }

        return null;
    }

    public async Task<SkillApprovalGrantOutcome> AwaitApprovalAsync(
        string pluginId,
        BusinessApprovalRequestDto request,
        BusinessApprovalDigestInput digestInput,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(digestInput);

        var tcs = new TaskCompletionSource<SkillApprovalGrantOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingBusinessApprovalEntry(pluginId, request, digestInput, tcs);
        _pending[request.ApprovalId] = entry;

        await PersistPendingApprovalStateAsync(request, SkillRunStatus.WaitingForApproval, ct).ConfigureAwait(false);
        await PublishApprovalRequestedSseAsync(request, ct).ConfigureAwait(false);

        if (IsStopOrInvalidationRequested(request.RunId))
        {
            if (_pending.TryRemove(request.ApprovalId, out _))
            {
                return new SkillApprovalGrantOutcome(false, true, true, BusinessSkillErrorCodes.ApprovalExpired, "Run stopped or invalidated before approval decision.");
            }
        }

        var remaining = request.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _pending.TryRemove(request.ApprovalId, out _);
            return new SkillApprovalGrantOutcome(false, false, true, BusinessSkillErrorCodes.ApprovalExpired, "Approval request expired.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(remaining);
            using var cancelReg = timeoutCts.Token.Register(static state =>
            {
                var tuple = ((BusinessSkillApprovalCoordinator Self, string ApprovalId, CancellationToken HostCt))state!;
                if (tuple.Self._pending.TryRemove(tuple.ApprovalId, out var timedOut))
                {
                    var isHostCancelled = tuple.HostCt.IsCancellationRequested;
                    timedOut.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                        Approved: false,
                        Denied: isHostCancelled,
                        ExpiredOrInvalidated: !isHostCancelled,
                        ErrorCode: isHostCancelled ? "cancelled" : BusinessSkillErrorCodes.ApprovalExpired,
                        ErrorMessage: isHostCancelled ? "Run cancelled while waiting for approval." : "Business write approval request expired (10-minute validity window)."));
                }
            }, (this, request.ApprovalId, ct));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.ApprovalId, out _);
            await ClearPendingApprovalInDbAsync(request.RunId).ConfigureAwait(false);
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
        if (!HasApproval(approvalId))
        {
            return null;
        }

        if (decision is not (ExecutionApprovalDecisionKind.AllowOnce or ExecutionApprovalDecisionKind.Deny))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                "Business skill write approvals only allow 'allow_once' or 'deny'.");
        }

        if (!_pending.TryGetValue(approvalId, out var entry))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                "Business approval request was already resolved, invalidated, or expired.");
        }

        if (!string.Equals(entry.Request.RequestDigest, requestDigest, StringComparison.Ordinal))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DigestMismatch,
                "Approval requestDigest does not match pending immutable write summary.");
        }

        if (DateTimeOffset.UtcNow >= entry.Request.ExpiresAtUtc)
        {
            if (_pending.TryRemove(approvalId, out var expired))
            {
                expired.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                    false, false, true, BusinessSkillErrorCodes.ApprovalExpired, "Approval expired before decision."));
            }

            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                "Business approval request expired before decision could be applied.");
        }

        // 提交决定时再次核对数据库中的插件启用状态、连接修订与凭据修订，防止过期审批在撤权瞬间生效
        var contextMismatch = await VerifyBindingStillValidAsync(entry, ct).ConfigureAwait(false);
        if (contextMismatch is not null)
        {
            if (_pending.TryRemove(approvalId, out var invalidated))
            {
                invalidated.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                    false, false, true, BusinessSkillErrorCodes.CatalogChanged, contextMismatch));
            }

            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                contextMismatch);
        }

        if (!_pending.TryRemove(approvalId, out _))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                "Approval request was resolved concurrently.");
        }

        var wireDecision = ExecutionApprovalBroker.ToWireDecision(decision);
        await PublishApprovalResolvedSseAsync(entry.Request, wireDecision, decidedBy, note, ct).ConfigureAwait(false);

        if (decision == ExecutionApprovalDecisionKind.Deny)
        {
            entry.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                Approved: false,
                Denied: true,
                ExpiredOrInvalidated: false,
                ErrorCode: BusinessSkillErrorCodes.ApprovalRequired,
                ErrorMessage: string.IsNullOrWhiteSpace(note) ? "Write batch was denied by control-plane operator." : note));

            return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.Resolved, "Business write approval denied.");
        }

        entry.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
            Approved: true,
            Denied: false,
            ExpiredOrInvalidated: false,
            ErrorCode: null,
            ErrorMessage: null));

        return new ApprovalSubmissionResult(ApprovalSubmissionOutcome.Resolved, "Business write batch approved (allow_once).");
    }

    public async Task InvalidatePluginApprovalsAndRunsAsync(string pluginId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        foreach (var pair in _activeRuns)
        {
            if (string.Equals(pair.Value.PluginId, pluginId, StringComparison.Ordinal))
            {
                pair.Value.MarkStopped(reason);
            }
        }

        foreach (var entry in _pending.Values.Where(p => string.Equals(p.PluginId, pluginId, StringComparison.Ordinal)))
        {
            if (_pending.TryRemove(entry.Request.ApprovalId, out var removed))
            {
                removed.Tcs.TrySetResult(new SkillApprovalGrantOutcome(
                    Approved: false,
                    Denied: false,
                    ExpiredOrInvalidated: true,
                    ErrorCode: BusinessSkillErrorCodes.CatalogChanged,
                    ErrorMessage: reason));

                await PublishApprovalResolvedSseAsync(removed.Request, "invalidated", null, reason, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> VerifyBindingStillValidAsync(PendingBusinessApprovalEntry entry, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBusinessSkillExecutionStore>();

            var plugin = await store.FindPluginAsync(entry.PluginId, ct).ConfigureAwait(false);
            if (plugin is null || !plugin.Enabled)
            {
                return $"Plugin '{entry.PluginId}' is disabled or missing; pending write approval invalidated.";
            }

            var conn = await store.FindConnectionAsync(entry.DigestInput.ConnectionId, entry.PluginId, ct).ConfigureAwait(false);
            if (conn is null ||
                conn.Revision != entry.DigestInput.ConnectionRevision ||
                !string.Equals(conn.CredentialRevision, entry.DigestInput.CredentialRevision, StringComparison.Ordinal))
            {
                return "Plugin connection or credential revision changed since approval was prepared; pending approval invalidated.";
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed verifying approval binding for {ApprovalId}", entry.Request.ApprovalId);
            return "Unable to verify live plugin binding state; approval failed closed.";
        }
    }

    private async Task PersistPendingApprovalStateAsync(BusinessApprovalRequestDto request, SkillRunStatus status, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBusinessSkillExecutionStore>();
            var json = JsonSerializer.Serialize(request, Json.BusinessApprovalRequestDto);
            await store.SetPendingApprovalAsync(request.RunId, status, json, DateTime.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed persisting pending business approval state for run {RunId}", request.RunId);
        }
    }

    private async Task ClearPendingApprovalInDbAsync(string runId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBusinessSkillExecutionStore>();
            await store.ClearPendingApprovalAsync(runId, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed clearing pending business approval state for run {RunId}", runId);
        }
    }

    private async Task PublishApprovalRequestedSseAsync(BusinessApprovalRequestDto request, CancellationToken ct)
    {
        if (_gateway is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        try
        {
            await _gateway.PublishEventAsync(
                request.SessionId,
                new GatewaySseEvent
                {
                    Type = GatewaySseEventType.BusinessSkillApprovalRequested,
                    Payload = new Dictionary<string, object?>
                    {
                        ["approvalId"] = request.ApprovalId,
                        ["requestDigest"] = request.RequestDigest,
                        ["sessionId"] = request.SessionId,
                        ["runId"] = request.RunId,
                        ["stepId"] = request.StepId,
                        ["kind"] = request.Kind,
                        ["allowedDecisions"] = request.AllowedDecisions,
                        ["expiresAtUtc"] = request.ExpiresAtUtc.ToString("O")
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed publishing SSE business approval requested for run {RunId}", request.RunId);
        }
    }

    private async Task PublishApprovalResolvedSseAsync(
        BusinessApprovalRequestDto request,
        string decision,
        string? decidedBy,
        string? note,
        CancellationToken ct)
    {
        if (_gateway is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        try
        {
            await _gateway.PublishEventAsync(
                request.SessionId,
                new GatewaySseEvent
                {
                    Type = GatewaySseEventType.BusinessSkillApprovalResolved,
                    Payload = new Dictionary<string, object?>
                    {
                        ["approvalId"] = request.ApprovalId,
                        ["requestDigest"] = request.RequestDigest,
                        ["runId"] = request.RunId,
                        ["decision"] = decision,
                        ["decidedBy"] = decidedBy,
                        ["note"] = note,
                        ["resolvedAt"] = DateTimeOffset.UtcNow.ToString("O")
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed publishing SSE business approval resolved for run {RunId}", request.RunId);
        }
    }

    private sealed class ActiveRunState(string pluginId)
    {
        private volatile bool _stopped;
        public string PluginId { get; } = pluginId;
        public bool StopOrInvalidated => _stopped;
        public void MarkStopped(string _) => _stopped = true;
    }

    private sealed record PendingBusinessApprovalEntry(
        string PluginId,
        BusinessApprovalRequestDto Request,
        BusinessApprovalDigestInput DigestInput,
        TaskCompletionSource<SkillApprovalGrantOutcome> Tcs);
}
