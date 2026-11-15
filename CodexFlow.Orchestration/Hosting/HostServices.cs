// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;

namespace CodexFlow.Orchestration.Hosting
{
    /// <summary>
    /// Pushes an event to whoever is watching: a browser over SignalR on the platform, a terminal on the local host. The gateway,
    /// the job runner and the projector report progress through this and know nothing about the transport.
    /// </summary>
    public interface IRealtimeNotifier
    {
        /// <summary>Sends to everyone watching a group (the runner uses <c>conv:{sessionId}</c>).</summary>
        Task SendToGroupAsync(string group, string method, object? payload, CancellationToken ct = default);

        /// <summary>Sends to every connection of one user.</summary>
        Task SendToUserAsync(string userId, string method, object? payload, CancellationToken ct = default);
    }

    /// <summary>A notifier for a host nobody is watching.</summary>
    public sealed class NullRealtimeNotifier : IRealtimeNotifier
    {
        public Task SendToGroupAsync(string group, string method, object? payload, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendToUserAsync(string userId, string method, object? payload, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The part of the chat store the gateway and the acceptance code work on: sessions, conversations and their messages. The
    /// platform's chat service implements it next to everything else it offers its controllers.
    /// </summary>
    public interface IGatewayChatStore
    {
        Task AddMessage(string userId, string conversationId, string userContent, string aiContent, int tokenUsed, string model, string replyId = "", string systemPrompt = "", string? thinkingContent = null);
        Task SetName(string id, string name);
        Task<IvilsonConversation?> Get(string id);
        Task<List<IvilsonChatMessage>> GetConversationMessagesAfterAsync(string conversationId, DateTime? createdAfterUtc, int? hardLimit = null);
        Task<IvilsonSession?> GetSession(string sessionId);
        Task<string> CreateConversationInSession(string userId, string sessionId, string model = "codex-v2", string name = "", string? taskId = null, ConversationThreadType type = ConversationThreadType.Chat);
    }

    /// <summary>Finds the conversation a message belongs to. All lookups ignore archived conversations.</summary>
    public interface IConversationDirectory
    {
        /// <summary>The conversation with this id, archived or not, or null.</summary>
        Task<IvilsonConversation?> FindAsync(string conversationId, CancellationToken ct);

        /// <summary>The id itself when that conversation is live and belongs to this session and user, else null.</summary>
        Task<string?> FindLiveIdAsync(string conversationId, string sessionId, string userId, CancellationToken ct);

        /// <summary>The most recently updated live conversation of this type in the session, or null.</summary>
        Task<string?> FindLatestLiveIdAsync(string sessionId, string userId, ConversationThreadType type, CancellationToken ct);
    }

    /// <summary>The credentials a user has stored with the host for the gateway to act with.</summary>
    public interface IUserCredentialSource
    {
        /// <summary>The user's GitHub token, or an empty string when there is none.</summary>
        Task<string> GetGithubTokenAsync(string userId, CancellationToken ct);
    }
}

namespace CodexFlow.Application.Services
{
    /// <summary>Records what a model call cost a user.</summary>
    public interface ITokenUsageService
    {
        /// <param name="cachedPromptTokens">
        /// promptTokens 中由 provider 提示缓存命中的部分。它是 promptTokens 的子集，只单独累计，不改变总量。
        /// </param>
        Task RecordTokenUsageAsync(string userId, long promptTokens, long completionTokens, long cachedPromptTokens = 0);
    }
}
