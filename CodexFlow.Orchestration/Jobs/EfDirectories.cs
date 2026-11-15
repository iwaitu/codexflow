// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Hosting;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

/// <summary>Sessions in the host's database: one row per session, owned by a user.</summary>
public sealed class EfSessionDirectory : ISessionDirectory
{
    private readonly IJobDbContext _context;

    public EfSessionDirectory(IJobDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<SessionRecord?> FindAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await _context.Sessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => new { x.Id, x.UserId, x.WorkspacePath })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return session is null ? null : new SessionRecord(session.Id, session.UserId, session.WorkspacePath);
    }
}

/// <summary>Conversations in the host's database.</summary>
public sealed class EfConversationDirectory : IConversationDirectory
{
    private readonly IJobDbContext _context;

    public EfConversationDirectory(IJobDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<IvilsonConversation?> FindAsync(string conversationId, CancellationToken ct)
        => _context.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, ct);

    public Task<string?> FindLiveIdAsync(string conversationId, string sessionId, string userId, CancellationToken ct)
        => _context.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.SessionId == sessionId && c.UserId == userId && !c.IsArchived)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct)!;

    public Task<string?> FindLatestLiveIdAsync(string sessionId, string userId, ConversationThreadType type, CancellationToken ct)
        => _context.Conversations.AsNoTracking()
            .Where(c => c.SessionId == sessionId && c.UserId == userId && c.ThreadType == type && !c.IsArchived)
            .OrderByDescending(c => c.UpdateAt)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct)!;
}
