// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Orchestration.Chat;

/// <summary>The owner and persisted policy fields used in a compare-and-set update.</summary>
public sealed record StoredSessionPolicy(string UserId, SessionPolicySnapshot Policy);

public interface ISessionPolicyStore
{
    Task<StoredSessionPolicy?> FindPolicyAsync(string sessionId, string? userId, CancellationToken ct);

    /// <summary>Updates the plan mode only if the owner and persisted policy versions still match.</summary>
    Task<bool> TrySetPlanModeAsync(
        string sessionId,
        StoredSessionPolicy expected,
        bool active,
        int version,
        DateTime changedAtUtc,
        string changedBy,
        CancellationToken ct);
}

public interface ISessionCompressionPersistence
{
    Task<SessionCompressionState?> ReadCompressionAsync(string sessionId, CancellationToken ct);

    Task WriteCompressionAsync(string sessionId, string? summary, DateTime? compressedAtUtc, CancellationToken ct);
}

/// <summary>Merges output arriving after a turn into the conversation's latest ordinary chat message.</summary>
public interface ILateToolOutputStore
{
    /// <param name="sessionId">The session used to find the latest conversation when no preferred conversation is known.</param>
    /// <param name="preferredConversationId">The conversation that completed the turn, when known.</param>
    /// <param name="mergeEnvelope">A pure transform from the persisted envelope to the updated envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendLateToolOutputAsync(
        string sessionId,
        string? preferredConversationId,
        Func<string?, string> mergeEnvelope,
        CancellationToken ct);
}
