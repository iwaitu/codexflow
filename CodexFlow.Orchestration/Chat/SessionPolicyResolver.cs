// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Orchestration.Persistence;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.Chat;

public enum PlanModeTransitionOutcome
{
    Updated = 0,
    NoOp = 1,
    NotFound = 2,
    VersionConflict = 3,
    InvalidState = 4
}

public sealed record PlanModeTransitionResult(
    PlanModeTransitionOutcome Outcome,
    SessionPolicySnapshot? Policy,
    string Message);

/// <summary>
/// Resolves authoritative session WorkMode and PlanMode from the database (never trusting mutable KV metadata)
/// and applies host-controlled Plan Mode transitions with optimistic CAS version checks and audit records.
/// </summary>
public sealed class SessionPolicyResolver(
    ISessionPolicyStore policies,
    ISessionStore sessionStore,
    IWorkflowAuditStore auditStore,
    ICodexGateway? gateway = null,
    ILogger<SessionPolicyResolver>? logger = null) : ISessionPolicyResolver, IPlanModeHostBridge
{
    public async Task<SessionPolicySnapshot> ResolveRequiredAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("SessionId is required to resolve authoritative session policy.");
        }

        StoredSessionPolicy? session;
        try
        {
            session = await policies.FindPolicyAsync(sessionId, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Failed to load authoritative session policy for session {SessionId}. Failing closed.", sessionId);
            throw new InvalidOperationException($"Failed to load authoritative policy for session '{sessionId}'.", ex);
        }

        if (session is null)
        {
            throw new InvalidOperationException($"Authoritative session '{sessionId}' not found; failing closed.");
        }

        return Normalize(session.Policy);
    }

    public async Task<SessionPolicySnapshot?> TryResolveAsync(string sessionId, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await policies.FindPolicyAsync(sessionId, userId, ct).ConfigureAwait(false);
        return session is null ? null : Normalize(session.Policy);
    }

    public async Task<(bool Success, int PlanModeVersion, string? Error)> EnterPlanModeAsync(
        string sessionId,
        string? reason,
        string? objective,
        CancellationToken ct = default)
    {
        var session = await policies.FindPolicyAsync(sessionId, null, ct).ConfigureAwait(false);
        if (session is null)
        {
            return (false, 1, $"Session '{sessionId}' not found.");
        }

        var result = await SetPlanModeAsync(
            sessionId,
            session.UserId,
            active: true,
            expectedPlanModeVersion: null,
            changedBy: "model_enter",
            reason: reason,
            objective: objective,
            messageId: null,
            ct: ct).ConfigureAwait(false);

        var version = Math.Max(1, session.Policy.PlanModeVersion);
        return result.Outcome is PlanModeTransitionOutcome.Updated or PlanModeTransitionOutcome.NoOp
            ? (true, result.Policy?.PlanModeVersion ?? version, null)
            : (false, version, result.Message);
    }

    public async Task<int> GetCurrentPlanModeVersionAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await policies.FindPolicyAsync(sessionId, null, ct).ConfigureAwait(false);
        return Math.Max(1, session?.Policy.PlanModeVersion ?? 1);
    }

    public async Task<PlanModeTransitionResult> SetPlanModeAsync(
        string sessionId,
        string userId,
        bool active,
        int? expectedPlanModeVersion,
        string changedBy,
        string? reason = null,
        string? objective = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var session = await policies.FindPolicyAsync(sessionId, userId, ct).ConfigureAwait(false);
            if (session is null)
            {
                return new PlanModeTransitionResult(PlanModeTransitionOutcome.NotFound, null, "Session not found.");
            }

            var current = Normalize(session.Policy);
            if (expectedPlanModeVersion is int expected && expected != current.PlanModeVersion)
            {
                return new PlanModeTransitionResult(
                    PlanModeTransitionOutcome.VersionConflict,
                    current,
                    $"PlanModeVersion mismatch: expected {expected}, current is {current.PlanModeVersion}.");
            }

            if (current.PlanModeActive == active)
            {
                return new PlanModeTransitionResult(PlanModeTransitionOutcome.NoOp, current, "Plan mode unchanged.");
            }

            var nowUtc = DateTime.UtcNow;
            var newVersion = current.PlanModeVersion + 1;
            if (!await policies.TrySetPlanModeAsync(sessionId, session, active, newVersion, nowUtc, changedBy, ct)
                .ConfigureAwait(false))
            {
                continue;
            }

            var snapshot = current with
            {
                PlanModeActive = active,
                PlanModeVersion = newVersion,
                PlanModeChangedAtUtc = nowUtc,
                PlanModeChangedBy = changedBy,
            };
            await SyncCachedSessionAsync(sessionId, snapshot, reason, objective).ConfigureAwait(false);
            await RecordPlanModeAuditAsync(sessionId, userId, snapshot, changedBy, reason, messageId, ct).ConfigureAwait(false);
            await PublishPlanModeChangedAsync(sessionId, snapshot, reason, objective, ct).ConfigureAwait(false);

            return new PlanModeTransitionResult(
                PlanModeTransitionOutcome.Updated,
                snapshot,
                active ? "Plan mode enabled." : "Plan mode disabled.");
        }
    }

    public static SessionPolicySnapshot ToSnapshot(IvilsonSession entity)
        => new(
            entity.WorkMode,
            Math.Max(1, entity.ModeVersion),
            entity.PlanModeActive,
            Math.Max(1, entity.PlanModeVersion),
            entity.PlanModeChangedAtUtc,
            entity.PlanModeChangedBy);

    private static SessionPolicySnapshot Normalize(SessionPolicySnapshot policy)
        => policy with { ModeVersion = Math.Max(1, policy.ModeVersion), PlanModeVersion = Math.Max(1, policy.PlanModeVersion) };

    private async Task SyncCachedSessionAsync(
        string sessionId,
        SessionPolicySnapshot snapshot,
        string? reason,
        string? objective)
    {
        var cached = await sessionStore.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (cached is null)
        {
            return;
        }

        cached.WorkMode = snapshot.WorkMode;
        cached.ModeVersion = snapshot.ModeVersion;
        cached.PlanModeActive = snapshot.PlanModeActive;
        cached.PlanModeVersion = snapshot.PlanModeVersion;
        cached.PlanModeChangedAtUtc = snapshot.PlanModeChangedAtUtc;
        cached.PlanModeChangedBy = snapshot.PlanModeChangedBy;
        cached.Metadata[EnterPlanModeTool.ActiveMetadataKey] = snapshot.PlanModeActive ? bool.TrueString : bool.FalseString;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            cached.Metadata[EnterPlanModeTool.ReasonMetadataKey] = reason.Trim();
        }
        if (!string.IsNullOrWhiteSpace(objective))
        {
            cached.Metadata[EnterPlanModeTool.ObjectiveMetadataKey] = objective.Trim();
        }

        await sessionStore.SaveSessionAsync(cached).ConfigureAwait(false);
    }

    private async Task RecordPlanModeAuditAsync(
        string sessionId,
        string userId,
        SessionPolicySnapshot snapshot,
        string changedBy,
        string? reason,
        string? messageId,
        CancellationToken ct)
    {
        try
        {
            var auditId = $"plan-mode-{Guid.NewGuid():N}";
            var payload = JsonSerializer.Serialize(
                new PlanModeAuditPayload(userId, sessionId, snapshot.WorkMode.ToString(), snapshot.ModeVersion,
                    snapshot.PlanModeActive, snapshot.PlanModeVersion, changedBy, reason, messageId, snapshot.PlanModeChangedAtUtc),
                OrchestrationSnapshotJsonContext.CamelCase.PlanModeAuditPayload);

            await auditStore.StartAsync(new WorkflowRunRecord
            {
                Id = auditId,
                SessionId = sessionId,
                Name = "plan_mode_transition",
                Status = snapshot.PlanModeActive ? "entered" : "exited",
                StartedAtUtc = DateTime.UtcNow
            }, ct).ConfigureAwait(false);

            await auditStore.CompleteAsync(auditId, snapshot.PlanModeActive ? "entered" : "exited", payload, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to record plan_mode_transition audit for session {SessionId}.", sessionId);
        }
    }

    private async Task PublishPlanModeChangedAsync(
        string sessionId,
        SessionPolicySnapshot snapshot,
        string? reason,
        string? objective,
        CancellationToken ct)
    {
        if (gateway is null)
        {
            return;
        }

        try
        {
            await gateway.PublishEventAsync(
                sessionId,
                new GatewaySseEvent
                {
                    Type = GatewaySseEventType.PlanModeChanged,
                    Payload = new Dictionary<string, object?>
                    {
                        ["type"] = GatewaySseEventType.PlanModeChanged,
                        ["sessionId"] = sessionId,
                        ["workMode"] = snapshot.WorkMode.ToString(),
                        ["modeVersion"] = snapshot.ModeVersion,
                        ["active"] = snapshot.PlanModeActive,
                        ["version"] = snapshot.PlanModeVersion,
                        ["changedAtUtc"] = snapshot.PlanModeChangedAtUtc,
                        ["changedBy"] = snapshot.PlanModeChangedBy,
                        ["reason"] = reason,
                        ["objective"] = objective
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Failed to publish plan_mode_changed SSE event for session {SessionId}.", sessionId);
        }
    }
}
