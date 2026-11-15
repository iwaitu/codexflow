// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.DependencyInjection;

namespace CodexFlow.Gateway;

/// <summary>
/// The exact plan a user approval is bound to. Approval for one identity is never valid for another:
/// a different user, session, artifact, version or content hash is a different identity.
/// </summary>
public sealed record PlanApprovalIdentity(
    string UserId,
    string SessionId,
    string PlanArtifactId,
    string PlanVersion,
    string PlanHash)
{
    /// <summary>Plan identity without the approver (who may legitimately differ for display only).</summary>
    public string PlanKey => $"{SessionId}|{PlanArtifactId}|{PlanVersion}|{PlanHash}";

    public bool SamePlanAs(PlanApprovalIdentity? other)
        => other != null && string.Equals(PlanKey, other.PlanKey, StringComparison.Ordinal);
}

public enum GatewayGateStatus
{
    /// <summary>Proceed with the underlying tool / dispatch.</summary>
    Allowed,

    /// <summary>The identical approval is already recorded: skip the approval tool, do not require a second confirmation.</summary>
    AlreadyApproved,

    /// <summary>Refused. <see cref="GatewayGateResult.ReasonCode"/> says why. Never retry silently.</summary>
    Denied
}

public sealed record GatewayGateResult(
    GatewayGateStatus Status,
    string ReasonCode,
    string Message,
    PlanApprovalIdentity? Identity = null)
{
    public bool IsAllowed => Status != GatewayGateStatus.Denied;

    public static GatewayGateResult Allow(PlanApprovalIdentity? identity, string reason = "allowed")
        => new(GatewayGateStatus.Allowed, reason, string.Empty, identity);

    public static GatewayGateResult Already(PlanApprovalIdentity identity)
        => new(GatewayGateStatus.AlreadyApproved, "already_approved", "该计划版本已按相同授权批准，无需重复确认。", identity);

    public static GatewayGateResult Deny(string reason, string message, PlanApprovalIdentity? identity = null)
        => new(GatewayGateStatus.Denied, reason, message, identity);
}

/// <summary>
/// Process-wide approval state: which plan identity each session approved, and which idempotency keys already
/// produced an approval. In-memory by design (no schema change); durable truth stays in the PlanArtifact approval
/// record, which the gate also consults, so a restart can only make the gate stricter about <em>new</em> approvals,
/// never silently accept an old one for a changed plan.
/// </summary>
public sealed class GatewayApprovalLedger
{
    private const int MaxSessions = 4096;
    private const int MaxKeysPerSession = 64;

    public static GatewayApprovalLedger Shared { get; } = new();

    private sealed class SessionState
    {
        public readonly object Gate = new();
        public PlanApprovalIdentity? Approved;
        public readonly Queue<string> Keys = new();
        public long Touched;
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private long _clock;
    private readonly object _evictionLock = new();

    /// <summary>Records an approval. Returns false when the same idempotency key already recorded it (duplicate request).</summary>
    public bool TryRecordApproval(PlanApprovalIdentity identity, string idempotencyKey)
    {
        var state = GetOrAdd(identity.SessionId);
        lock (state.Gate)
        {
            if (state.Keys.Contains(idempotencyKey))
            {
                return false;
            }

            state.Keys.Enqueue(idempotencyKey);
            while (state.Keys.Count > MaxKeysPerSession)
            {
                state.Keys.Dequeue();
            }

            state.Approved = identity;
            return true;
        }
    }

    /// <summary>The approval recorded for the session, or null. May describe a plan that has since changed.</summary>
    public PlanApprovalIdentity? GetApproved(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return null;
        }

        lock (state.Gate)
        {
            return state.Approved;
        }
    }

    /// <summary>True when the session's recorded approval is for exactly this user and plan identity.</summary>
    public bool IsApproved(PlanApprovalIdentity identity)
    {
        var approved = GetApproved(identity.SessionId);
        return approved != null &&
               approved.SamePlanAs(identity) &&
               string.Equals(approved.UserId, identity.UserId, StringComparison.Ordinal);
    }

    /// <summary>Drops the session's approval (plan edited, rejected or revoked).</summary>
    public void Invalidate(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            lock (state.Gate)
            {
                state.Approved = null;
            }
        }
    }

    private SessionState GetOrAdd(string sessionId)
    {
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
        state.Touched = Interlocked.Increment(ref _clock);
        if (_sessions.Count > MaxSessions)
        {
            lock (_evictionLock)
            {
                if (_sessions.Count > MaxSessions)
                {
                    // Victims are the least recently touched; the state just handed out is the newest, so never one of them.
                    foreach (var victim in _sessions.OrderBy(kv => kv.Value.Touched).Take(_sessions.Count - MaxSessions + 64))
                    {
                        _sessions.TryRemove(victim.Key, out _);
                    }
                }
            }
        }

        return state;
    }
}

/// <summary>A snapshot of the session's current plan and what the durable approval record says about it.</summary>
public sealed record GatewayPlanSnapshot(
    PlanApprovalIdentity Identity,
    PlanArtifactStatus? ArtifactStatus,
    string? ArtifactApprovedByUserId,
    string? ArtifactApprovedHash,
    bool FromArtifact,
    string? ArtifactApprovedVersion = null,
    string? HostRefusal = null);

public static class GatewayPlanIdentityResolver
{
    /// <summary>
    /// Resolves the current plan identity. Prefers the PlanArtifact (id/version/markdown hash); falls back to a hash of the
    /// session task list when no artifact exists. Returns null when there is no plan at all, or when the artifact
    /// belongs to another session.
    /// </summary>
    public static async Task<GatewayPlanSnapshot?> ResolveAsync(
        IServiceScopeFactory scopeFactory,
        string sessionId,
        string userId,
        CodexSession? session,
        CancellationToken ct)
    {
        PlanArtifact? artifact = null;
        if (scopeFactory != null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IPlanArtifactStore>();
            if (store != null)
            {
                artifact = !string.IsNullOrWhiteSpace(session?.CurrentPlanArtifactId)
                    ? await store.GetAsync(session!.CurrentPlanArtifactId!, ct).ConfigureAwait(false)
                    : await store.GetCurrentAsync(sessionId, ct).ConfigureAwait(false);
            }
        }

        if (artifact != null)
        {
            if (!string.Equals(artifact.SessionId, sessionId, StringComparison.Ordinal))
            {
                // An artifact from another session can never be approved from this one.
                return null;
            }

            return new GatewayPlanSnapshot(
                new PlanApprovalIdentity(
                    userId,
                    sessionId,
                    artifact.Id,
                    artifact.Version,
                    PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown)),
                artifact.Status,
                artifact.Approval?.ApprovedByUserId,
                artifact.Approval?.ApprovedMarkdownHash,
                FromArtifact: true,
                ArtifactApprovedVersion: artifact.Approval?.ApprovedPlanVersion,
                HostRefusal: PlanHostRefusal.Refuses(artifact, out var refusal) ? refusal : null);
        }

        if (session == null || session.Plan.Count == 0)
        {
            return null;
        }

        return new GatewayPlanSnapshot(
            new PlanApprovalIdentity(
                userId,
                sessionId,
                "session-plan",
                session.PlanVersion ?? "0",
                HashTaskList(session.Plan)),
            null,
            null,
            null,
            FromArtifact: false);
    }

    /// <summary>Content hash of the plan tasks, deliberately excluding execution status so progress does not invalidate approval.</summary>
    internal static string HashTaskList(IEnumerable<CodexTask> tasks)
    {
        var sb = new StringBuilder();
        foreach (var task in tasks)
        {
            sb.Append(task.Id).Append('\u001f')
              .Append(task.Title).Append('\u001f')
              .Append(task.Description).Append('\u001f')
              .Append(task.TaskType).Append('\u001f')
              .Append(string.Join(",", task.Dependencies.OrderBy(d => d, StringComparer.Ordinal)))
              .Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// The approval state machine used by every approve / reject / dispatch path. All of them receive the same
/// <see cref="GatewayIntentDecision"/> from <see cref="GatewayIntentResolver"/> and the same plan identity rules.
/// </summary>
public sealed class GatewayApprovalGate
{
    private sealed class TurnStartBox
    {
        public GatewayPlanSnapshot? Snapshot;
    }

    private static readonly ConditionalWeakTable<GatewayMessage, TurnStartBox> TurnStart = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GatewayApprovalLedger _ledger;

    public GatewayApprovalGate(IServiceScopeFactory scopeFactory, GatewayApprovalLedger? ledger = null)
    {
        _scopeFactory = scopeFactory;
        _ledger = ledger ?? GatewayApprovalLedger.Shared;
    }

    public GatewayApprovalLedger Ledger => _ledger;

    /// <summary>
    /// Remembers the plan identity as it was when the user's message arrived. An approval refers to that plan:
    /// if the plan changes during the turn (a model edit before approve_plan), the approval no longer applies.
    /// Also lazily invalidates a stale recorded approval when the plan changed since it was granted.
    /// </summary>
    public async Task CaptureTurnStartAsync(GatewayMessage message, CodexSession? session, CancellationToken ct)
    {
        var snapshot = await GatewayPlanIdentityResolver
            .ResolveAsync(_scopeFactory, message.SessionId, message.UserId ?? string.Empty, session, ct)
            .ConfigureAwait(false);
        TurnStart.GetOrCreateValue(message).Snapshot = snapshot;

        if (snapshot != null)
        {
            var approved = _ledger.GetApproved(message.SessionId);
            if (approved != null && !approved.SamePlanAs(snapshot.Identity))
            {
                _ledger.Invalidate(message.SessionId);
            }
        }
    }

    /// <summary>Gate for approve_plan, whether called by the direct path, the tool wrapper or the runtime.</summary>
    public async Task<GatewayGateResult> EvaluateApprovalAsync(
        GatewayMessage message,
        string? requestedPlanArtifactId,
        CodexSession? session,
        CancellationToken ct)
    {
        var decision = GatewayIntentResolver.Resolve(message);
        if (!decision.AuthorizesApproval)
        {
            return GatewayGateResult.Deny(
                $"approval_not_authorized:{decision.ReasonCode}",
                decision.RequiresConfirmation && decision.ConfirmationPrompt != null
                    ? decision.ConfirmationPrompt
                    : "当前用户消息没有明确的批准指令（仅规划、只读、否定、引用、历史、条件或提问均不构成批准）。等待用户明确授权执行。");
        }

        var userId = message.UserId ?? string.Empty;
        if (session != null &&
            !string.IsNullOrEmpty(session.UserId) &&
            !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return GatewayGateResult.Deny("approval_user_mismatch", "该会话不属于当前用户，不能批准其计划。");
        }

        var current = await GatewayPlanIdentityResolver
            .ResolveAsync(_scopeFactory, message.SessionId, userId, session, ct)
            .ConfigureAwait(false);
        if (current == null)
        {
            // Nothing identifiable to approve; the underlying tool reports the missing plan itself. But a model-chosen
            // artifact id must never reach the tool when this session has no plan of its own (it could be another session's).
            return string.IsNullOrWhiteSpace(requestedPlanArtifactId)
                ? GatewayGateResult.Allow(null, "no_plan_identity")
                : GatewayGateResult.Deny(
                    "approval_plan_mismatch",
                    "当前会话没有可批准的计划，不能按指定 artifact id 批准其他计划。");
        }

        var identity = current.Identity;
        if (!string.IsNullOrWhiteSpace(requestedPlanArtifactId) &&
            !string.Equals(requestedPlanArtifactId, identity.PlanArtifactId, StringComparison.Ordinal))
        {
            return GatewayGateResult.Deny(
                "approval_plan_mismatch",
                "请求批准的计划与当前会话计划不一致，拒绝批准。请对当前计划重新明确批准。",
                identity);
        }

        if (decision.Structured is { } structured)
        {
            if ((structured.PlanArtifactId != null && structured.PlanArtifactId != identity.PlanArtifactId) ||
                (structured.PlanVersion != null && structured.PlanVersion != identity.PlanVersion) ||
                (structured.PlanHash != null && !string.Equals(structured.PlanHash, identity.PlanHash, StringComparison.OrdinalIgnoreCase)))
            {
                _ledger.Invalidate(message.SessionId);
                return GatewayGateResult.Deny(
                    "approval_plan_changed",
                    "计划自您操作后已发生变化，旧批准失效。请查看最新计划并重新明确批准。",
                    identity);
            }
        }

        if (TurnStart.TryGetValue(message, out var box) &&
            box.Snapshot != null &&
            !box.Snapshot.Identity.SamePlanAs(identity))
        {
            _ledger.Invalidate(message.SessionId);
            return GatewayGateResult.Deny(
                "approval_plan_changed",
                "计划在本轮对话中被修改，批准不适用于修改后的计划。请查看最新计划并重新明确批准。",
                identity);
        }

        if (current.HostRefusal is { } refusal)
        {
            // The host refused this exact plan. Asking again (or request_plan_approval re-opening it) does not change what it contains.
            return GatewayGateResult.Deny(
                "plan_refused_by_host",
                "宿主已拒绝这份计划，内容没有改动前不能批准：" + refusal + " 请先按上面的原因修改计划，再请求批准。",
                identity);
        }

        if (_ledger.IsApproved(identity) || IsDurablyApprovedBy(current, userId))
        {
            return GatewayGateResult.Already(identity);
        }

        return GatewayGateResult.Allow(identity);
    }

    /// <summary>Records a successful approval so repeats are idempotent and later plan edits invalidate it.</summary>
    public void RecordApproval(GatewayMessage message, PlanApprovalIdentity? identity)
    {
        if (identity == null)
        {
            return;
        }

        var key = GatewayIntentResolver.Resolve(message).Structured?.IdempotencyKey ?? message.MessageId;
        _ledger.TryRecordApproval(identity, $"{key}|{identity.PlanKey}");
    }

    /// <summary>Gate for reject_plan: needs an explicit rejection and a plan that belongs to this session.</summary>
    public async Task<GatewayGateResult> EvaluateRejectionAsync(
        GatewayMessage message,
        string? requestedPlanArtifactId,
        CodexSession? session,
        CancellationToken ct)
    {
        var decision = GatewayIntentResolver.Resolve(message);
        if (!decision.AuthorizesRejection)
        {
            return GatewayGateResult.Deny(
                $"rejection_not_authorized:{decision.ReasonCode}",
                "当前用户消息没有明确的拒绝/撤销计划指令，不能拒绝计划。");
        }

        var current = await GatewayPlanIdentityResolver
            .ResolveAsync(_scopeFactory, message.SessionId, message.UserId ?? string.Empty, session, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(requestedPlanArtifactId) &&
            (current == null ||
             !string.Equals(requestedPlanArtifactId, current.Identity.PlanArtifactId, StringComparison.Ordinal)))
        {
            return GatewayGateResult.Deny("rejection_plan_mismatch", "请求拒绝的计划与当前会话计划不一致。", current?.Identity);
        }

        _ledger.Invalidate(message.SessionId);
        return GatewayGateResult.Allow(current?.Identity);
    }

    /// <summary>
    /// Gate for start_next_task / direct dispatch. System notifications are not user dispatches and are not gated here
    /// (the backend scheduler owns them). A user dispatch needs an Execute/Approve intent, and an approval recorded for a
    /// plan that has since changed no longer counts.
    /// </summary>
    public async Task<GatewayGateResult> EvaluateDispatchAsync(
        GatewayMessage message,
        CodexSession? session,
        CancellationToken ct)
    {
        if (message.Source != GatewayMessageSource.UserChat)
        {
            return await EvaluateSystemDispatchAsync(message, session, ct).ConfigureAwait(false);
        }

        var decision = GatewayIntentResolver.Resolve(message);
        if (!decision.AuthorizesDispatch)
        {
            return GatewayGateResult.Deny(
                $"dispatch_not_authorized:{decision.ReasonCode}",
                decision.RequiresConfirmation && decision.ConfirmationPrompt != null
                    ? decision.ConfirmationPrompt
                    : "当前用户消息没有明确的执行/继续指令（仅规划、只读、否定、引用、历史、条件或提问均不会启动任务）。");
        }

        var current = await GatewayPlanIdentityResolver
            .ResolveAsync(_scopeFactory, message.SessionId, message.UserId ?? string.Empty, session, ct)
            .ConfigureAwait(false);
        if (current == null)
        {
            return GatewayGateResult.Allow(null, "no_plan_identity");
        }

        var approved = _ledger.GetApproved(message.SessionId);
        if (approved != null && !approved.SamePlanAs(current.Identity))
        {
            _ledger.Invalidate(message.SessionId);
            return GatewayGateResult.Deny(
                "approval_invalidated_by_plan_change",
                "计划在批准后已发生变化，旧批准失效。请查看最新计划并重新明确批准后再执行。",
                current.Identity);
        }

        // Durable binding (survives restart, where the in-memory ledger is empty): an approved/executing artifact must have
        // been approved for exactly this markdown. A plan edited after approval is never dispatched under the old approval.
        if (current.FromArtifact &&
            current.ArtifactStatus is PlanArtifactStatus.Approved
                or PlanArtifactStatus.Projecting
                or PlanArtifactStatus.ReadyToExecute
                or PlanArtifactStatus.Executing &&
            !DurableApprovalMatchesPlan(current))
        {
            return GatewayGateResult.Deny(
                "approval_invalidated_by_plan_change",
                "计划内容与批准时的版本不一致，旧批准失效。请查看最新计划并重新明确批准后再执行。",
                current.Identity);
        }

        return GatewayGateResult.Allow(current.Identity);
    }

    /// <summary>
    /// A system notification / task event may continue a plan that a user already approved, but it can never start a new,
    /// unapproved or changed plan: with an artifact the persisted approval must match the current hash and version; without
    /// one the plan must already be underway or approved in this process.
    /// </summary>
    private async Task<GatewayGateResult> EvaluateSystemDispatchAsync(
        GatewayMessage message,
        CodexSession? session,
        CancellationToken ct)
    {
        var current = await GatewayPlanIdentityResolver
            .ResolveAsync(_scopeFactory, message.SessionId, message.UserId ?? string.Empty, session, ct)
            .ConfigureAwait(false);
        if (current == null)
        {
            return GatewayGateResult.Allow(null, "system_source_no_plan_identity");
        }

        if (current.FromArtifact)
        {
            var approvedStatus = current.ArtifactStatus is PlanArtifactStatus.Approved
                or PlanArtifactStatus.Projecting
                or PlanArtifactStatus.ReadyToExecute
                or PlanArtifactStatus.Executing;
            if (approvedStatus && DurableApprovalMatchesPlan(current))
            {
                return GatewayGateResult.Allow(current.Identity, "system_source_approved_plan");
            }
        }
        else
        {
            var underway = session?.Plan.Any(static t => t.Status != CodexTaskStatus.Pending) == true;
            if (underway || _ledger.IsApproved(current.Identity))
            {
                return GatewayGateResult.Allow(current.Identity, "system_source_approved_plan");
            }
        }

        return GatewayGateResult.Deny(
            "dispatch_system_source_not_approved",
            "系统通知不能启动尚未获得用户明确批准（或批准后已变更）的计划。请用户明确批准当前计划。",
            current.Identity);
    }

    private static bool IsDurablyApprovedBy(GatewayPlanSnapshot snapshot, string userId)
    {
        if (!snapshot.FromArtifact ||
            snapshot.ArtifactStatus is not (PlanArtifactStatus.Approved
                or PlanArtifactStatus.Projecting
                or PlanArtifactStatus.ReadyToExecute
                or PlanArtifactStatus.Executing))
        {
            return false;
        }

        return string.Equals(snapshot.ArtifactApprovedByUserId, userId, StringComparison.Ordinal) &&
               DurableApprovalMatchesPlan(snapshot);
    }

    /// <summary>
    /// The persisted approval covers the current plan only if hash AND (when recorded) version match. Records written
    /// before ApprovedPlanVersion existed carry no version and are bound by hash alone.
    /// </summary>
    private static bool DurableApprovalMatchesPlan(GatewayPlanSnapshot snapshot)
        => string.Equals(snapshot.ArtifactApprovedHash, snapshot.Identity.PlanHash, StringComparison.OrdinalIgnoreCase) &&
           (string.IsNullOrEmpty(snapshot.ArtifactApprovedVersion) ||
            string.Equals(snapshot.ArtifactApprovedVersion, snapshot.Identity.PlanVersion, StringComparison.Ordinal));
}
