// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Orchestration.Chat;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Application;

/// <summary>
/// Database-backed implementation of <see cref="ISessionCompressionStore"/>.
/// Persists the <c>HistorySummary</c> and <c>LastCompressedAt</c> projection so
/// that compression state survives Redis TTL expiry.
/// </summary>
public sealed class SessionCompressionStore(
    ISessionCompressionPersistence persistence,
    ILogger<SessionCompressionStore> logger) : ISessionCompressionStore
{
    public async Task<SessionCompressionState?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await persistence.ReadCompressionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to load session compression projection. SessionId={SessionId}", sessionId);
            return null;
        }
    }

    public async Task SaveAsync(string sessionId, string historySummary, DateTime lastCompressedAtUtc, CancellationToken ct = default)
    {
        try
        {
            await persistence.WriteCompressionAsync(sessionId, historySummary, lastCompressedAtUtc, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to save session compression projection. SessionId={SessionId}", sessionId);
        }
    }

    public async Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await persistence.WriteCompressionAsync(sessionId, null, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to clear session compression projection. SessionId={SessionId}", sessionId);
        }
    }
}
