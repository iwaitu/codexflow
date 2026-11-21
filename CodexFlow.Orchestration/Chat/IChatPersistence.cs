// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Domain.Chat;

namespace CodexFlow.Application;

public interface IChatPersistence
{
    Task AddConversationAsync(IvilsonConversation conversation);
    Task<IvilsonConversation?> FindConversationAsync(string conversationId);
    Task<IvilsonConversation?> FindConversationWithHistoryAsync(string conversationId);
    Task<IvilsonConversation?> SetConversationNameAsync(string conversationId, string name, DateTime updatedAt);
    Task<IvilsonConversation?> RenameConversationAsync(string conversationId, string userId, string name, DateTime updatedAt);
    Task<IvilsonConversation?> ArchiveConversationAsync(string conversationId, string userId, bool isArchived, DateTime updatedAt);
    Task SetConversationIndexedAsync(string conversationId);
    Task<string?> FindLatestConversationIdAsync(string userId);
    Task<PageOf<IvilsonConversation>> ListConversationsAsync(
        string? userId, string? sessionId, string? keyword, int page, int pageSize, bool includeArchived);
    Task DeleteConversationAsync(string conversationId, string userId);
    Task AddNotificationAsync(IvilsonChatMessage message, DateTime updatedAt);
    Task<string?> FindReplyContentAsync(string replyId);

    Task AddSessionAsync(IvilsonSession session);
    Task<IvilsonSession?> FindSessionAsync(string sessionId, bool includeConversations = false);
    Task<List<IvilsonSession>> ListActiveSessionsAsync(string userId, bool includeHistory = false);
    Task<PageOf<IvilsonSession>> ListSessionsAsync(string userId, int page, int pageSize, bool includeArchived);
    Task<IvilsonSession?> FindLatestWorkspaceAsync(string userId, string name, bool daily);
    Task<IvilsonSession?> RenameSessionAsync(string sessionId, string userId, string name);
    Task<IvilsonSession?> ArchiveSessionAsync(string sessionId, string userId, bool isArchived);
    Task SetSessionStageAsync(string sessionId, int stage);
    Task SetSessionFactsAsync(string sessionId, string factsJson);
    Task SetSessionPlanAsync(string sessionId, string planJson);
    Task SetSessionProjectUrlAsync(string sessionId, string url);
    Task ResetExecutionStateAsync(string sessionId, int stage);
    Task<IvilsonSession?> ClearSessionAsync(string sessionId, string userId);
    Task<IvilsonSession?> DeleteSessionAsync(string sessionId, string userId);
}
