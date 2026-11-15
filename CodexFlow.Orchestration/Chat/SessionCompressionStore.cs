// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Application;

/// <summary>
/// Database-backed implementation of <see cref="ISessionCompressionStore"/>.
/// Persists the <c>HistorySummary</c> and <c>LastCompressedAt</c> projection so
/// that compression state survives Redis TTL expiry.
/// </summary>
public sealed class SessionCompressionStore : ISessionCompressionStore
{
    private readonly CodexFlow.Orchestration.Jobs.IJobDbContext _db;
    private readonly ILogger<SessionCompressionStore> _logger;

    public SessionCompressionStore(CodexFlow.Orchestration.Jobs.IJobDbContext db, ILogger<SessionCompressionStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SessionCompressionState?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var session = await _db.Sessions
                .AsNoTracking()
                .Select(s => new { s.Id, s.HistorySummary, s.LastCompressedAt })
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                .ConfigureAwait(false);

            if (session == null || string.IsNullOrEmpty(session.HistorySummary) || session.LastCompressedAt == null)
                return null;

            return new SessionCompressionState(session.HistorySummary, session.LastCompressedAt.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load session compression projection from PG. SessionId={SessionId}", sessionId);
            return null;
        }
    }

    public async Task SaveAsync(string sessionId, string historySummary, DateTime lastCompressedAtUtc, CancellationToken ct = default)
    {
        try
        {
            await _db.Sessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.HistorySummary, historySummary)
                    .SetProperty(s => s.LastCompressedAt, lastCompressedAtUtc),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to save session compression projection to PG. SessionId={SessionId}", sessionId);
        }
    }

    public async Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _db.Sessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.HistorySummary, (string?)null)
                    .SetProperty(s => s.LastCompressedAt, (DateTime?)null),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to clear session compression projection from PG. SessionId={SessionId}", sessionId);
        }
    }
}
