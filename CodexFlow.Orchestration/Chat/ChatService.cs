// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Application
{
    public class ChatService : IChatService
    {
        private readonly IChatPersistence _persistence;
        private readonly ILogger<ChatService> _logger;
        private readonly IWorkspaceRootProvider? _workspaceRootProvider;
        private readonly CodexFlow.Core.Browser.IBrowserSessionManager? _browserSessionManager;

        private readonly IConversationMessageStore _conversationMessages;

        public ChatService(
            IChatPersistence persistence,
            ILogger<ChatService> logger,
            IWorkspaceRootProvider? workspaceRootProvider,
            IConversationMessageStore conversationMessages,
            CodexFlow.Core.Browser.IBrowserSessionManager? browserSessionManager = null)
        {
            _persistence = persistence;
            _logger = logger;
            _workspaceRootProvider = workspaceRootProvider;
            _conversationMessages = conversationMessages;
            _browserSessionManager = browserSessionManager;
        }

        private string ResolveManagedWorkspacesRoot()
            => _workspaceRootProvider?.WorkspacesRoot
               ?? Path.Combine(Directory.GetCurrentDirectory(), "workspaces");

        /// <summary>
        /// The id of the user whose name contains <paramref name="username"/>. A host without a user table has no such user;
        /// the platform host overrides this with a lookup in its own.
        /// </summary>
        protected virtual Task<string?> FindUserIdByNameAsync(string username) => Task.FromResult<string?>(null);


        public async Task<string> Create(string userid, string model = "gpt-3.5-turbo", string name = "")
        {
            try
            {
                var conversation = new IvilsonConversation
                {
                    Model = model ?? "gpt-3.5-turbo",
                    UserId = userid,
                    Name = name ?? string.Empty,
                    UpdateAt = DateTime.UtcNow
                };
                await _persistence.AddConversationAsync(conversation);
                return conversation.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                return "";
            }

        }

        public async Task<IvilsonConversation?> Get(string id)
        {
            var result = await _persistence.FindConversationWithHistoryAsync(id);
            if (result == null)
            {
                _logger.LogError("Conversation not found");
                return null;
            }
            return result;
        }

        public async Task SetName(string id, string name)
        {
            if (await _persistence.SetConversationNameAsync(id, name, DateTime.UtcNow) is null)
            {
                _logger.LogError("Conversation not found");
            }
        }

        public async Task<string> GetConversationName(string conversationId)
        {
            var cov = await _persistence.FindConversationAsync(conversationId);
            if (cov != null)
            {
                return cov.Name;
            }
            return "";
        }

        
        //根据对话id获取对话内容
        public Task<List<IvilsonChatMessage>> GetConversationMessages(string conversationId)
            => _conversationMessages.ReadAfterAsync(conversationId, null, null);

        public Task<List<IvilsonChatMessage>> GetConversationMessagesAfterAsync(
            string conversationId,
            DateTime? createdAfterUtc,
            int? hardLimit = null)
            => _conversationMessages.ReadAfterAsync(conversationId, createdAfterUtc, hardLimit);

        public Task AddMessageWithToolcall(string userId, string conversationId, string userContent, string aiContent, int tokenUsed, string toolCall, string model, string replayId = "", string systemPrompt = "", string? thinkingContent = null)
            => AppendMessageAsync(userId, conversationId, userContent, aiContent, tokenUsed, model, replayId, systemPrompt,
                thinkingContent, toolCall ?? string.Empty, nameof(AddMessageWithToolcall));

        public Task AddMessage(string userId, string conversationId, string userContent, string aiContent, int tokenUsed, string model, string replayId = "", string systemPrompt = "", string? thinkingContent = null)
            => AppendMessageAsync(userId, conversationId, userContent, aiContent, tokenUsed, model, replayId, systemPrompt,
                thinkingContent, null, nameof(AddMessage));

        private async Task AppendMessageAsync(
            string userId,
            string conversationId,
            string userContent,
            string aiContent,
            int tokenUsed,
            string model,
            string replayId,
            string systemPrompt,
            string? thinkingContent,
            string? toolCallResult,
            string operation)
        {
            try
            {
                var message = new IvilsonChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = conversationId,
                    UserContent = userContent ?? string.Empty,
                    AiContent = aiContent ?? string.Empty,
                    ThinkingContent = thinkingContent,
                    TokenUsed = tokenUsed,
                    SystemPrompt = systemPrompt ?? string.Empty,
                    ReplyId = replayId ?? string.Empty,
                    ToolCallResult = toolCallResult,
                    Created = DateTime.UtcNow,
                };
                await _conversationMessages.AppendAsync(userId, model, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} failed", operation);
            }
        }

        public async Task<string?> AddNotificationMessage(string userId, string conversationId, string content, string messageType, string? notificationMeta)
        {
            var messageId = Guid.NewGuid().ToString("N");
            try
            {
                var updatedAt = DateTime.UtcNow;
                var message = new IvilsonChatMessage
                {
                    Id = messageId,
                    ConversationId = conversationId,
                    UserContent = string.Empty,
                    AiContent = content,
                    MessageType = messageType,
                    NotificationMeta = notificationMeta,
                    Created = DateTime.UtcNow
                };
                await _persistence.AddNotificationAsync(message, updatedAt);
                return messageId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddNotificationMessage failed for conversation {ConversationId}", conversationId);
                return null;
            }
        }

        //获取用户最近的对话id
        public async Task<string> GetUserLatestConversationId(string userid)
            => await _persistence.FindLatestConversationIdAsync(userid) ?? string.Empty;

        public Task<string?> GetMessageContentByReplyId(string replyId, string userId)
            => _persistence.FindReplyContentAsync(replyId);

        public Task<PageOf<IvilsonConversation>> GetConversations(string userid, string key, int page, int pagesize)
            => userid is null
                ? Task.FromResult(new PageOf<IvilsonConversation> { List = [], Page = page, PageSize = pagesize })
                : _persistence.ListConversationsAsync(userid, null, key, page, pagesize, includeArchived: true);

        public Task UpdateConversationIndexed(string cId)
            => _persistence.SetConversationIndexedAsync(cId);

        public async Task<PageOf<IvilsonConversation>> GetAllConversations(string key, int page, int pagesize, string? username)
        {
            string? userId = null;
            if (!string.IsNullOrEmpty(username))
            {
                userId = await FindUserIdByNameAsync(username);
                if (userId is null)
                {
                    return new PageOf<IvilsonConversation>
                    {
                        List = [],
                        PageSize = pagesize,
                        Page = page,
                        Total = 0
                    };
                }
            }

            return await _persistence.ListConversationsAsync(userId, null, key, page, pagesize, includeArchived: true);
        }

        public Task<string> CreateSession(string userId, string name)
            => CreateSession(userId, name, CodexFlow.Core.Models.SessionWorkMode.Development);

        public async Task<string> CreateSession(string userId, string name, CodexFlow.Core.Models.SessionWorkMode workMode)
        {
            if (string.Equals(name?.Trim(), "默认工作区", StringComparison.Ordinal) ||
                string.Equals(name?.Trim(), "日常助手", StringComparison.Ordinal))
            {
                throw new ArgumentException("Reserved workspace name cannot be created via generic session creation.", nameof(name));
            }

            var baseDir = ResolveManagedWorkspacesRoot();
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            var workspacePath = Path.Combine(baseDir, userId, Guid.NewGuid().ToString("N"));
            EnsureWorkspaceDirectoryExists(workspacePath);

            var session = new IvilsonSession
            {
                UserId = userId,
                Name = string.IsNullOrWhiteSpace(name) ? (workMode == CodexFlow.Core.Models.SessionWorkMode.Daily ? "日常对话" : "New Workspace") : name.Trim(),
                WorkspacePath = workspacePath,
                WorkMode = workMode,
                ModeVersion = 1,
                PlanModeActive = false,
                PlanModeVersion = 1
            };
            await _persistence.AddSessionAsync(session);
            return session.Id;
        }

        public async Task<(string SessionId, string WorkspaceName, bool Created)> CreateOrOpenExternalWorkspaceSession(
            string userId,
            string canonicalWorkspacePath,
            string? name = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWorkspacePath);

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalWorkspacePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.IsNullOrWhiteSpace(name)
                && (string.Equals(name.Trim(), "默认工作区", StringComparison.Ordinal) ||
                    string.Equals(name.Trim(), "日常助手", StringComparison.Ordinal)))
            {
                throw new ArgumentException("The default workspace name is reserved.", nameof(name));
            }

            var existingCandidates = await _persistence.ListActiveSessionsAsync(userId);

            var existing = existingCandidates.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.WorkspacePath) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.WorkspacePath)),
                    normalized,
                    comparison));

            if (existing is not null)
            {
                if (existing.WorkMode == CodexFlow.Core.Models.SessionWorkMode.Daily)
                {
                    throw new InvalidOperationException("Daily work mode session cannot bind an external workspace.");
                }

                var isDefaultWorkspace = string.Equals(existing.Name, "默认工作区", StringComparison.Ordinal) && !existing.IsExternalWorkspace;
                if (!isDefaultWorkspace && !string.IsNullOrWhiteSpace(name) && !string.Equals(existing.Name, name.Trim(), StringComparison.Ordinal))
                {
                    existing = await _persistence.RenameSessionAsync(existing.Id, userId, name.Trim()) ?? existing;
                }

                return (existing.Id, existing.Name, false);
            }

            var folderFallback = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(folderFallback) || string.Equals(folderFallback, "默认工作区", StringComparison.Ordinal))
            {
                folderFallback = $"工作区-{normalized.GetHashCode(StringComparison.OrdinalIgnoreCase):x8}";
            }

            var displayName = !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : folderFallback;

            var session = new IvilsonSession
            {
                UserId = userId,
                Name = displayName,
                WorkspacePath = normalized,
                IsExternalWorkspace = true
            };
            await _persistence.AddSessionAsync(session);
            return (session.Id, session.Name, true);
        }

        public Task<IvilsonSession?> GetSession(string sessionId)
            => _persistence.FindSessionAsync(sessionId, includeConversations: true);

        public Task<PageOf<IvilsonSession>> GetSessions(string userId, int page, int pageSize)
            => GetSessionsPaged(userId, page, pageSize, includeArchived: true);

        public Task<PageOf<IvilsonSession>> GetSessionsPaged(string userId, int page, int pageSize, bool includeArchived = false)
            => _persistence.ListSessionsAsync(userId, Math.Max(1, page), Math.Clamp(pageSize, 1, 100), includeArchived);

        public Task<IvilsonSession?> RenameSession(string sessionId, string userId, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _persistence.RenameSessionAsync(sessionId, userId, name.Trim());
        }

        public Task<IvilsonSession?> SetSessionArchived(string sessionId, string userId, bool isArchived)
            => _persistence.ArchiveSessionAsync(sessionId, userId, isArchived);

        public Task<PageOf<IvilsonConversation>> GetSessionConversations(
            string sessionId,
            string userId,
            int page,
            int pageSize,
            bool includeArchived = false)
        {
            var safePage = Math.Max(1, page);
            var safePageSize = Math.Clamp(pageSize, 1, 100);
            return userId is null || sessionId is null
                ? Task.FromResult(new PageOf<IvilsonConversation> { List = [], Page = safePage, PageSize = safePageSize })
                : _persistence.ListConversationsAsync(userId, sessionId, null, safePage, safePageSize, includeArchived);
        }

        public Task<IvilsonConversation?> RenameConversation(string conversationId, string userId, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _persistence.RenameConversationAsync(conversationId, userId, name.Trim(), DateTime.UtcNow);
        }

        public Task<IvilsonConversation?> SetConversationArchived(string conversationId, string userId, bool isArchived)
            => _persistence.ArchiveConversationAsync(conversationId, userId, isArchived, DateTime.UtcNow);

        public Task UpdateSessionStage(string sessionId, int stage)
            => _persistence.SetSessionStageAsync(sessionId, stage);

        public Task UpdateSessionFacts(string sessionId, string factsJson)
            => _persistence.SetSessionFactsAsync(sessionId, factsJson);

        public Task UpdateSessionPlan(string sessionId, string planJson)
            => _persistence.SetSessionPlanAsync(sessionId, planJson);

        public Task UpdateSessionProjectUrl(string sessionId, string url)
            => _persistence.SetSessionProjectUrlAsync(sessionId, url);

        public Task ResetSessionExecutionState(string sessionId, int stage = 1)
            => _persistence.ResetExecutionStateAsync(sessionId, stage);

        public async Task ClearSession(string sessionId, string userId)
        {
            var session = await _persistence.ClearSessionAsync(sessionId, userId);
            if (session != null)
            {
                // 3. 物理清理工作区目录内容 (保留根目录)
                if (!string.IsNullOrWhiteSpace(session.WorkspacePath))
                {
                    try
                    {
                        if (Directory.Exists(session.WorkspacePath))
                        {
                            var di = new DirectoryInfo(session.WorkspacePath);
                            foreach (var file in di.GetFiles()) file.Delete();
                            foreach (var dir in di.GetDirectories())
                            {
                                ForceDeleteDirectory(dir.FullName);
                            }
                        }

                        // 缺失用户目录/工作区目录时，补建根目录，保证“清空工作区”语义幂等。
                        EnsureWorkspaceDirectoryExists(session.WorkspacePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "物理清理工作區失敗: {Path}", session.WorkspacePath);
                    }
                }
            }
        }

        private void ForceDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            var di = new DirectoryInfo(path);
            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }
            
            try 
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // 如果 IOException，可能是進程佔用，嘗試重命名後標記刪除 (或其他補救措施)
                _logger.LogWarning("Directory in use, deferred deletion might be needed: {Path}", path);
            }
        }

        private static void EnsureWorkspaceDirectoryExists(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath)) return;
            Directory.CreateDirectory(workspacePath);
        }

        public async Task DeleteSession(string sessionId, string userId)
        {
            var session = await _persistence.DeleteSessionAsync(sessionId, userId);
            if (session != null)
            {
                if (_browserSessionManager is not null)
                {
                    try
                    {
                        await _browserSessionManager.CloseSessionAsync(sessionId, "session_deleted").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除会话 {SessionId} 时关闭浏览器上下文失败，继续清理工作区。", sessionId);
                    }
                }

                // 2. 物理清理工作区目录 (仅限 CodexFlow 托管创建的内部沙箱工作区；严禁删除用户通过 P3-3 打开的外部本地文件夹)
                if (!session.IsExternalWorkspace &&
                    !string.IsNullOrEmpty(session.WorkspacePath) &&
                    Directory.Exists(session.WorkspacePath))
                {
                    ForceDeleteDirectory(session.WorkspacePath);
                }
            }
        }

        public async Task<string> CreateConversationInSession(string userId, string sessionId, string model = "codex-v2", string name = "", string? taskId = null, ConversationThreadType type = ConversationThreadType.Chat)
        {
            var conversation = new IvilsonConversation
            {
                UserId = userId,
                SessionId = sessionId,
                Model = model ?? "codex-v2",
                Name = string.IsNullOrWhiteSpace(name) ? "New Conversation" : name,
                TaskId = taskId,
                ThreadType = type
            };
            conversation.UpdateAt = DateTime.UtcNow;
            await _persistence.AddConversationAsync(conversation);

            // P1-03: 记录新建 conversation 详情
            _logger.LogInformation("[ChatService] CreateConversationInSession: ConversationId={ConversationId}, SessionId={SessionId}, UserId={UserId}, ThreadType={ThreadType}, Name={Name}",
                conversation.Id, sessionId, userId, type, conversation.Name);

            return conversation.Id;
        }

        public async Task<List<HistoryViewModel>> GetWorkspaces(string userId)
        {
            var sessions = await _persistence.ListActiveSessionsAsync(userId, includeHistory: true);

            var result = new List<HistoryViewModel>();
            
            // 1. Separate default workspaces from others
            var defaultSessions = sessions.Where(s => s.Name == "默认工作区").ToList();
            var otherSessions = sessions.Where(s => s.Name != "默认工作区").ToList();

            // 2. Aggregate default workspaces if any exist
            if (defaultSessions.Any())
            {
                var latestDefault = defaultSessions.First(); // Already ordered by Created Descending
                var allDefaultConversations = defaultSessions.SelectMany(s => s.Conversations).ToList();

                var vm = new HistoryViewModel
                {
                    SessionId = latestDefault.Id,
                    ProjectName = "默认工作区",
                    CurrentStage = latestDefault.CurrentStage,
                    WorkspacePath = latestDefault.WorkspacePath,
                    LastActive = latestDefault.Created.ToString("yyyy-MM-dd HH:mm"),
                };

                var lastConv = allDefaultConversations.OrderByDescending(c => c.Created).FirstOrDefault();
                if (lastConv != null)
                {
                    vm.LastActive = lastConv.Created.ToString("yyyy-MM-dd HH:mm");
                }

                vm.RecentConversations = allDefaultConversations
                    .OrderByDescending(c => c.UpdateAt)
                    .Take(10) 
                    .Select(c => new RecentConversationDto
                    {
                        Id = c.Id,
                        Title = string.IsNullOrWhiteSpace(c.Name) ? "New Conversation" : c.Name,
                        MsgCount = c.ChatMessages?.Count ?? 0
                    })
                    .ToList();

                result.Add(vm);
            }
            // --- 已移除：不要在 GET 请求中自动创建 Session，避免并发导致的重复创建 ---

            // 3. Add other sessions normally
            foreach (var s in otherSessions)
            {
                var vm = new HistoryViewModel
                {
                    SessionId = s.Id,
                    ProjectName = string.IsNullOrWhiteSpace(s.Name) ? "New Project" : s.Name,
                    CurrentStage = s.CurrentStage,
                    WorkspacePath = s.IsExternalWorkspace
                        ? s.WorkspacePath
                        : Path.GetRelativePath(Directory.GetCurrentDirectory(), s.WorkspacePath),
                    LastActive = s.Created.ToString("yyyy-MM-dd HH:mm"),
                };

                if (vm.ProjectName.Contains("C#") || vm.ProjectName.Contains("Core")) vm.LanguageTags.Add("csharp");
                if (vm.ProjectName.Contains("Python") || vm.ProjectName.Contains("AI")) vm.LanguageTags.Add("python");
                if (vm.ProjectName.Contains("Web") || vm.ProjectName.Contains("React")) vm.LanguageTags.Add("typescript");

                var lastConv = s.Conversations.OrderByDescending(c => c.Created).FirstOrDefault();
                if (lastConv != null)
                {
                    vm.LastActive = lastConv.Created.ToString("yyyy-MM-dd HH:mm");
                }

                vm.RecentConversations = s.Conversations
                    .OrderByDescending(c => c.UpdateAt)
                    .Take(5)
                    .Select(c => new RecentConversationDto
                    {
                        Id = c.Id,
                        Title = string.IsNullOrWhiteSpace(c.Name) ? "New Conversation" : c.Name,
                        MsgCount = c.ChatMessages?.Count ?? 0
                    })
                    .ToList();

                result.Add(vm);
            }

            // P1-05: 记录 workspaces 返回数据统计
            var totalConversations = result.Sum(r => r.RecentConversations?.Count ?? 0);
            var lastActive = result.OrderByDescending(r => r.LastActive).FirstOrDefault()?.LastActive;
            _logger.LogInformation("[ChatService] GetWorkspaces: UserId={UserId}, SessionCount={SessionCount}, TotalConversations={TotalConversations}, LastActive={LastActive}",
                userId, result.Count, totalConversations, lastActive ?? "N/A");

            return result;
        }

        public async Task<string?> GetDefaultSessionId(string userId)
        {
            var session = await _persistence.FindLatestWorkspaceAsync(userId, "默认工作区", daily: false);
            return session?.Id;
        }

        /// <summary>
        /// P2-04: 获取或创建默认工作区，用于 Session Bootstrap
        /// </summary>
        public async Task<(string sessionId, bool created, string workspacePath)> GetOrCreateDefaultWorkspace(string userId)
        {
            // 查找现有默认工作区
            var existingSession = await _persistence.FindLatestWorkspaceAsync(userId, "默认工作区", daily: false);

            if (existingSession != null)
            {
                _logger.LogInformation("[ChatService] GetOrCreateDefaultWorkspace: 返回已有默认工作区 SessionId={SessionId}, UserId={UserId}", existingSession.Id, userId);
                return (existingSession.Id, false, existingSession.WorkspacePath);
            }

            // 创建新的默认工作区
            var baseDir = ResolveManagedWorkspacesRoot();
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            var workspacePath = Path.Combine(baseDir, userId, Guid.NewGuid().ToString("N"));
            EnsureWorkspaceDirectoryExists(workspacePath);

            var session = new IvilsonSession
            {
                UserId = userId,
                Name = "默认工作区",
                WorkspacePath = workspacePath
            };
            await _persistence.AddSessionAsync(session);

            _logger.LogInformation("[ChatService] GetOrCreateDefaultWorkspace: 创建新默认工作区 SessionId={SessionId}, UserId={UserId}, WorkspacePath={WorkspacePath}", session.Id, userId, workspacePath);
            return (session.Id, true, workspacePath);
        }

        /// <summary>
        /// 获取或创建“日常助手”默认托管工作空间会话（WorkMode = Daily）。
        /// </summary>
        public async Task<(string sessionId, bool created, string workspacePath)> GetOrCreateDailyAssistantWorkspace(string userId)
        {
            var existingSession = await _persistence.FindLatestWorkspaceAsync(userId, "日常助手", daily: true);

            if (existingSession != null)
            {
                EnsureWorkspaceDirectoryExists(existingSession.WorkspacePath);
                return (existingSession.Id, false, existingSession.WorkspacePath);
            }

            var baseDir = ResolveManagedWorkspacesRoot();
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            var workspacePath = Path.Combine(baseDir, userId, Guid.NewGuid().ToString("N"));
            EnsureWorkspaceDirectoryExists(workspacePath);

            var session = new IvilsonSession
            {
                UserId = userId,
                Name = "日常助手",
                WorkspacePath = workspacePath,
                WorkMode = CodexFlow.Core.Models.SessionWorkMode.Daily,
                ModeVersion = 1,
                PlanModeActive = false,
                PlanModeVersion = 1,
                IsExternalWorkspace = false
            };
            await _persistence.AddSessionAsync(session);

            _logger.LogInformation("[ChatService] GetOrCreateDailyAssistantWorkspace: 创建新日常助手工作空间 SessionId={SessionId}, UserId={UserId}", session.Id, userId);
            return (session.Id, true, workspacePath);
        }

        public Task DeleteConversation(string conversationId, string userId)
            => _persistence.DeleteConversationAsync(conversationId, userId);
    }
}
