// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Application;

/// <summary>The provider-neutral EF implementation for hosts that use a job context.</summary>
public sealed class EfConversationMessageStore(IJobDbContext db) : IConversationMessageStore
{
    public Task<List<IvilsonChatMessage>> ReadAfterAsync(string conversationId, DateTime? createdAfterUtc, int? hardLimit)
    {
        var query = db.ChatMessages.AsNoTracking().Where(message => message.ConversationId == conversationId);
        if (createdAfterUtc.HasValue)
            query = query.Where(message => message.Created > createdAfterUtc.Value);
        if (hardLimit.HasValue)
            query = query.OrderByDescending(message => message.Created).ThenByDescending(message => message.Id)
                .Take(Math.Max(0, hardLimit.Value));

        return query.OrderBy(message => message.Created).ThenBy(message => message.Id).ToListAsync();
    }

    public async Task AppendAsync(string userId, string? model, IvilsonChatMessage message)
    {
        var conversation = await db.Conversations.FindAsync(message.ConversationId);
        if (conversation is null)
        {
            var title = message.UserContent ?? string.Empty;
            conversation = new IvilsonConversation
            {
                Id = message.ConversationId,
                UserId = userId,
                Name = title.Length > 20 ? title[..20] : title,
                Model = model ?? "gpt-3.5-turbo",
                Created = message.Created,
            };
            db.Conversations.Add(conversation);
        }

        conversation.UpdateAt = message.Created;
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
    }
}
