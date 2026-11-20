// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Lets direct SQLite queries and the scoped EF context work against the same database.</summary>
internal sealed class ScopedSqliteConversationMessageStore : IConversationMessageStore
{
    private readonly IJobDbContext _db;
    private readonly EfConversationMessageStore _ef;
    private readonly SqliteConversationMessageStore _sqlite;

    public ScopedSqliteConversationMessageStore(IJobDbContext db)
    {
        _db = db;
        _ef = new EfConversationMessageStore(db);
        _sqlite = new SqliteConversationMessageStore((SqliteConnection)db.Database.GetDbConnection());
    }

    public Task<List<IvilsonChatMessage>> ReadAfterAsync(string conversationId, DateTime? createdAfterUtc, int? hardLimit)
        => _db.Database.CurrentTransaction is null
            ? _sqlite.ReadAfterAsync(conversationId, createdAfterUtc, hardLimit)
            : _ef.ReadAfterAsync(conversationId, createdAfterUtc, hardLimit);

    public async Task AppendAsync(string userId, string? model, IvilsonChatMessage message)
    {
        // Let EF save tracked changes together with the append and manage its existing transaction.
        if (_db.Database.CurrentTransaction is not null || _db.ChangeTracker.HasChanges())
        {
            await _ef.AppendAsync(userId, model, message);
            return;
        }

        var tracked = _db.Conversations.Local.FirstOrDefault(conversation => conversation.Id == message.ConversationId);
        await _sqlite.AppendAsync(userId, model, message);
        if (tracked is not null)
        {
            var timestamp = _db.Entry(tracked).Property(conversation => conversation.UpdateAt);
            timestamp.CurrentValue = message.Created;
            timestamp.OriginalValue = message.Created;
            timestamp.IsModified = false;
        }
    }
}
