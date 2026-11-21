// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Chat;

/// <summary>Session state adapter for hosts that supply an EF context.</summary>
public sealed class EfSessionStateStore(IJobDbContext db)
    : ISessionPolicyStore, ISessionCompressionPersistence, ILateToolOutputStore
{
    public Task<StoredSessionPolicy?> FindPolicyAsync(string sessionId, string? userId, CancellationToken ct)
        => db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId && (userId == null || s.UserId == userId))
            .Select(s => new StoredSessionPolicy(s.UserId, new SessionPolicySnapshot(
                s.WorkMode, s.ModeVersion, s.PlanModeActive, s.PlanModeVersion,
                s.PlanModeChangedAtUtc, s.PlanModeChangedBy)))
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TrySetPlanModeAsync(
        string sessionId,
        StoredSessionPolicy expected,
        bool active,
        int version,
        DateTime changedAtUtc,
        string changedBy,
        CancellationToken ct)
    {
        var state = expected.Policy;
        var changed = await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == expected.UserId
                && s.PlanModeVersion == state.PlanModeVersion && s.PlanModeActive == state.PlanModeActive
                && s.ModeVersion == state.ModeVersion && s.WorkMode == state.WorkMode)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.PlanModeActive, active)
                .SetProperty(s => s.PlanModeVersion, version)
                .SetProperty(s => s.PlanModeChangedAtUtc, changedAtUtc)
                .SetProperty(s => s.PlanModeChangedBy, changedBy), ct)
            .ConfigureAwait(false);
        return changed != 0;
    }

    public async Task<SessionCompressionState?> ReadCompressionAsync(string sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.HistorySummary, s.LastCompressedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return session?.LastCompressedAt is DateTime compressedAt && !string.IsNullOrEmpty(session.HistorySummary)
            ? new SessionCompressionState(session.HistorySummary, compressedAt)
            : null;
    }

    public Task WriteCompressionAsync(string sessionId, string? summary, DateTime? compressedAtUtc, CancellationToken ct)
        => db.Sessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.HistorySummary, summary)
                .SetProperty(s => s.LastCompressedAt, compressedAtUtc), ct);

    public async Task AppendLateToolOutputAsync(
        string sessionId,
        string? preferredConversationId,
        Func<string?, string> mergeEnvelope,
        CancellationToken ct)
    {
        var conversationId = !string.IsNullOrWhiteSpace(preferredConversationId)
            ? preferredConversationId
            : await db.Conversations
                .Where(c => c.SessionId == sessionId && !c.IsArchived)
                .OrderByDescending(c => c.UpdateAt)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        var latest = await db.ChatMessages
            .Where(m => m.ConversationId == conversationId && (m.MessageType == null || m.MessageType == ""))
            .OrderByDescending(m => m.Created)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latest is null)
        {
            db.ChatMessages.Add(new IvilsonChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                UserContent = string.Empty,
                AiContent = string.Empty,
                SystemPrompt = string.Empty,
                ReplyId = string.Empty,
                Created = DateTime.UtcNow,
                ToolCallResult = mergeEnvelope(null),
            });
        }
        else
        {
            latest.ToolCallResult = mergeEnvelope(latest.ToolCallResult);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
