// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Application;

public sealed class EfChatPersistence(IJobDbContext db) : IChatPersistence
{
    public async Task AddConversationAsync(IvilsonConversation conversation)
    {
        if (conversation.SessionId is string sessionId && await db.Sessions.FindAsync(sessionId) is null)
        {
            throw new ArgumentException("Session not found");
        }

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
    }

    public Task<IvilsonConversation?> FindConversationAsync(string conversationId)
        => db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId);

    public async Task<IvilsonConversation?> FindConversationWithHistoryAsync(string conversationId)
    {
        var conversation = await db.Conversations.FindAsync(conversationId);
        if (conversation is not null)
        {
            conversation.ChatMessages = await db.ChatMessages.Where(message => message.ConversationId == conversationId)
                .OrderBy(message => message.Created).ToListAsync();
        }
        return conversation;
    }

    public Task<IvilsonConversation?> SetConversationNameAsync(string conversationId, string name, DateTime updatedAt)
        => EditConversationAsync(conversationId, null, false, conversation =>
        {
            conversation.Name = name;
            conversation.UpdateAt = updatedAt;
        });

    public Task<IvilsonConversation?> RenameConversationAsync(string conversationId, string userId, string name, DateTime updatedAt)
        => EditConversationAsync(conversationId, userId, true, conversation =>
        {
            conversation.Name = name;
            conversation.UpdateAt = updatedAt;
        });

    public Task<IvilsonConversation?> ArchiveConversationAsync(string conversationId, string userId, bool isArchived, DateTime updatedAt)
        => EditConversationAsync(conversationId, userId, true, conversation =>
        {
            conversation.IsArchived = isArchived;
            conversation.UpdateAt = updatedAt;
        });

    public Task SetConversationIndexedAsync(string conversationId)
        => EditConversationAsync(conversationId, null, false, conversation => conversation.IsIndexed = true);

    public Task<string?> FindLatestConversationIdAsync(string userId)
        => db.Conversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.Created)
            .Select(c => (string?)c.Id)
            .FirstOrDefaultAsync();

    public async Task<PageOf<IvilsonConversation>> ListConversationsAsync(
        string? userId, string? sessionId, string? keyword, int page, int pageSize, bool includeArchived)
    {
        var query = db.Conversations.AsNoTracking()
            .Where(c => (userId == null || c.UserId == userId)
                && (sessionId == null || c.SessionId == sessionId)
                && (includeArchived || !c.IsArchived));
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(c => c.Name.Contains(keyword));
        }

        var total = await query.CountAsync();
        var list = await query.OrderByDescending(c => c.UpdateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PageOf<IvilsonConversation> { List = list, PageSize = pageSize, Page = page, Total = total };
    }

    public async Task DeleteConversationAsync(string conversationId, string userId)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);
        if (conversation is not null)
        {
            await RemoveConversationMessagesAsync([conversation.Id]);
            db.Conversations.Remove(conversation);
            await db.SaveChangesAsync();
        }
    }

    public async Task AddNotificationAsync(IvilsonChatMessage message, DateTime updatedAt)
    {
        var conversation = await db.Conversations.FindAsync(message.ConversationId);
        if (conversation is not null)
        {
            conversation.UpdateAt = updatedAt;
        }

        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
    }

    public Task<string?> FindReplyContentAsync(string replyId)
        => db.ChatMessages
            .Where(message => message.ReplyId == replyId)
            .Select(message => message.AiContent)
            .FirstOrDefaultAsync();

    public async Task AddSessionAsync(IvilsonSession session)
    {
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
    }

    public Task<IvilsonSession?> FindSessionAsync(string sessionId, bool includeConversations = false)
        => includeConversations
            ? db.Sessions.Include(s => s.Conversations).FirstOrDefaultAsync(s => s.Id == sessionId)
            : db.Sessions.FindAsync(sessionId).AsTask();

    public Task<List<IvilsonSession>> ListActiveSessionsAsync(string userId, bool includeHistory = false)
    {
        var query = includeHistory
            ? db.Sessions.Include(s => s.Conversations).ThenInclude(c => c.ChatMessages).AsSplitQuery()
            : db.Sessions.AsQueryable();
        return query.Where(s => s.UserId == userId && !s.IsArchived)
            .OrderByDescending(s => s.Created)
            .ToListAsync();
    }

    public async Task<PageOf<IvilsonSession>> ListSessionsAsync(string userId, int page, int pageSize, bool includeArchived)
    {
        var query = db.Sessions.AsNoTracking().Where(s => s.UserId == userId && (includeArchived || !s.IsArchived));
        var total = await query.CountAsync();
        var list = await query.OrderByDescending(s => s.Created)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PageOf<IvilsonSession> { List = list, Page = page, PageSize = pageSize, Total = total };
    }

    public Task<IvilsonSession?> FindLatestWorkspaceAsync(string userId, string name, bool daily)
        => db.Sessions.Where(s => s.UserId == userId && s.Name == name
                && (!daily || (s.WorkMode == SessionWorkMode.Daily && !s.IsExternalWorkspace && !s.IsArchived)))
            .OrderByDescending(s => s.Created)
            .FirstOrDefaultAsync();

    public Task<IvilsonSession?> RenameSessionAsync(string sessionId, string userId, string name)
        => EditSessionAsync(sessionId, userId, true, session => session.Name = name);

    public Task<IvilsonSession?> ArchiveSessionAsync(string sessionId, string userId, bool isArchived)
        => EditSessionAsync(sessionId, userId, true, session => session.IsArchived = isArchived);

    public Task SetSessionStageAsync(string sessionId, int stage)
        => EditSessionAsync(sessionId, null, false, session => session.CurrentStage = stage);

    public Task SetSessionFactsAsync(string sessionId, string factsJson)
        => EditSessionAsync(sessionId, null, false, session => session.FactsJson = factsJson);

    public Task SetSessionPlanAsync(string sessionId, string planJson)
        => EditSessionAsync(sessionId, null, false, session => session.GlobalPlanJson = planJson);

    public Task SetSessionProjectUrlAsync(string sessionId, string url)
        => EditSessionAsync(sessionId, null, false, session => session.ProjectUrl = url);

    public Task ResetExecutionStateAsync(string sessionId, int stage)
        => EditSessionAsync(sessionId, null, false, session =>
        {
            session.CurrentStage = stage;
            session.GlobalPlanJson = null;
            session.ActiveTaskId = null;
        });

    public async Task<IvilsonSession?> ClearSessionAsync(string sessionId, string userId)
    {
        var session = await FindOwnedSessionAsync(sessionId, userId);
        if (session is null)
        {
            return null;
        }

        await RemoveSessionConversationsAsync(session);
        session.CurrentStage = 0;
        session.FactsJson = "[]";
        session.GlobalPlanJson = null;
        session.ActiveTaskId = null;
        await db.SaveChangesAsync();
        return session;
    }

    public async Task<IvilsonSession?> DeleteSessionAsync(string sessionId, string userId)
    {
        var session = await FindOwnedSessionAsync(sessionId, userId);
        if (session is null || session.Name == "默认工作区"
            || (session.Name == "日常助手" && session.WorkMode == SessionWorkMode.Daily && !session.IsExternalWorkspace))
        {
            return null;
        }

        await RemoveSessionConversationsAsync(session);
        db.Sessions.Remove(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task<IvilsonConversation?> EditConversationAsync(
        string id, string? userId, bool requireOwner, Action<IvilsonConversation> apply)
    {
        var conversation = requireOwner
            ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId)
            : await db.Conversations.FindAsync(id);
        if (conversation is not null)
        {
            apply(conversation);
            await db.SaveChangesAsync();
        }

        return conversation;
    }

    private async Task<IvilsonSession?> EditSessionAsync(string id, string? userId, bool requireOwner, Action<IvilsonSession> apply)
    {
        var session = requireOwner
            ? await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId)
            : await FindSessionAsync(id);
        if (session is not null)
        {
            apply(session);
            await db.SaveChangesAsync();
        }

        return session;
    }

    private Task<IvilsonSession?> FindOwnedSessionAsync(string id, string userId)
        => db.Sessions.Include(s => s.Conversations).FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

    private async Task RemoveSessionConversationsAsync(IvilsonSession session)
    {
        var ids = session.Conversations.Select(c => c.Id).ToArray();
        await RemoveConversationMessagesAsync(ids);
        db.Conversations.RemoveRange(session.Conversations);
    }

    private async Task RemoveConversationMessagesAsync(string[] conversationIds)
    {
        if (conversationIds.Length != 0)
        {
            var messages = await db.ChatMessages.Where(m => conversationIds.Contains(m.ConversationId)
                || conversationIds.Contains(EF.Property<string>(m, "IvilsonConversationId"))).ToListAsync();
            db.ChatMessages.RemoveRange(messages);
        }
    }
}
