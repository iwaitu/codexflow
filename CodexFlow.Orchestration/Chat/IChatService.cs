// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Domain.Chat;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Application
{
    public interface IChatService : CodexFlow.Orchestration.Hosting.IGatewayChatStore
    {
        Task<string> Create(string userid, string model = "gpt-3.5-turbo", string name = "");
        Task<string> GetUserLatestConversationId(string userid);
        Task<List<IvilsonChatMessage>> GetConversationMessages(string conversationId);
        Task<string?> GetMessageContentByReplyId(string replyId, string userId);
        Task UpdateConversationIndexed(string cId);
        Task<PageOf<IvilsonConversation>> GetConversations(string userid, string key, int page, int pagesize);
        Task<PageOf<IvilsonConversation>> GetAllConversations(string key, int page, int pagesize, string? username);
        Task<string> GetConversationName(string conversationId);

        // Session Management
        Task<string> CreateSession(string userId, string name);
        Task<PageOf<IvilsonSession>> GetSessions(string userId, int page, int pageSize);
        Task<PageOf<IvilsonSession>> GetSessionsPaged(string userId, int page, int pageSize, bool includeArchived = false);
        Task<(string SessionId, string WorkspaceName, bool Created)> CreateOrOpenExternalWorkspaceSession(string userId, string canonicalWorkspacePath, string? name = null);
        Task<IvilsonSession?> RenameSession(string sessionId, string userId, string name);
        Task<IvilsonSession?> SetSessionArchived(string sessionId, string userId, bool isArchived);
        Task<PageOf<IvilsonConversation>> GetSessionConversations(string sessionId, string userId, int page, int pageSize, bool includeArchived = false);
        Task<IvilsonConversation?> RenameConversation(string conversationId, string userId, string name);
        Task<IvilsonConversation?> SetConversationArchived(string conversationId, string userId, bool isArchived);
        Task UpdateSessionStage(string sessionId, int stage);
        Task UpdateSessionFacts(string sessionId, string factsJson);
        Task UpdateSessionPlan(string sessionId, string planJson);
        Task UpdateSessionProjectUrl(string sessionId, string url);
        Task ResetSessionExecutionState(string sessionId, int stage = 1);
        Task DeleteSession(string sessionId, string userId);
        Task ClearSession(string sessionId, string userId);
        Task<List<HistoryViewModel>> GetWorkspaces(string userId);
        Task<string?> GetDefaultSessionId(string userId);
        Task DeleteConversation(string conversationId, string userId);

        /// <summary>
        /// P2-04: 获取或创建默认工作区，用于 Session Bootstrap
        /// </summary>
        /// <returns>sessionId, created (是否新建), workspacePath</returns>
        Task<(string sessionId, bool created, string workspacePath)> GetOrCreateDefaultWorkspace(string userId);

        /// <summary>
        /// 向会话注入系统通知消息（不产生 UserContent，仅写入 AiContent）
        /// </summary>
        /// <returns>消息 ID</returns>
        Task<string?> AddNotificationMessage(string userId, string conversationId, string content, string messageType, string? notificationMeta);
    }
}
