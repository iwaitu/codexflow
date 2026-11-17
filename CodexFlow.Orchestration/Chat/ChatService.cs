// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Application
{
    public class ChatService : IChatService
    {
        private readonly CodexFlow.Orchestration.Jobs.IJobDbContext _db;
        private readonly ILogger<ChatService> _logger;
        private readonly IWorkspaceRootProvider? _workspaceRootProvider;

        public ChatService(
            CodexFlow.Orchestration.Jobs.IJobDbContext db,
            ILogger<ChatService> logger,
            IWorkspaceRootProvider? workspaceRootProvider = null)
        {
            _db = db;
            _logger = logger;
            _workspaceRootProvider = workspaceRootProvider;
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
                _db.Conversations.Add(conversation);
                await _db.SaveChangesAsync();
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
            var result = await _db.Conversations.FindAsync(id);
            if (result == null)
            {
                _logger.LogError("Conversation not found");
                return null;
            }
            result.ChatMessages = await _db.ChatMessages.Where(p => p.ConversationId == id).OrderBy(p => p.Created).ToListAsync();
            return result;
        }

        public async Task SetName(string id, string name)
        {
            var conversation = await _db.Conversations.FindAsync(id);
            if (conversation == null)
            {
                _logger.LogError("Conversation not found");
                return;
            }
            conversation.Name = name;
            conversation.UpdateAt = DateTime.UtcNow;
            _db.Conversations.Update(conversation);
            await _db.SaveChangesAsync();
        }

        public async Task<string> GetConversationName(string conversationId)
        {
            var cov = await _db.Conversations.FindAsync(conversationId);
            if (cov != null)
            {
                return cov.Name;
            }
            return "";
        }

        
        //根据对话id获取对话内容
        public async Task<List<IvilsonChatMessage>> GetConversationMessages(string conversationId)
        {
            var result = await _db.ChatMessages.Where(p => p.ConversationId == conversationId).OrderBy(p => p.Created).ToListAsync();
            return result;
        }

        public async Task<List<IvilsonChatMessage>> GetConversationMessagesAfterAsync(
            string conversationId,
            DateTime? createdAfterUtc,
            int? hardLimit = null)
        {
            // When createdAfterUtc is null (session never compressed), preserve legacy
            // behavior: return all messages in chronological order without any cap.
            if (!createdAfterUtc.HasValue)
                return await _db.ChatMessages
                    .AsNoTracking()
                    .Where(m => m.ConversationId == conversationId)
                    .OrderBy(m => m.Created)
                    .ToListAsync();

            IQueryable<IvilsonChatMessage> query = _db.ChatMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .Where(m => m.Created > createdAfterUtc.Value);

            if (hardLimit.HasValue)
                return await query
                    .OrderByDescending(m => m.Created)
                    .Take(hardLimit.Value)
                    .OrderBy(m => m.Created)
                    .ToListAsync();

            return await query.OrderBy(m => m.Created).ToListAsync();
        }

        public async Task AddMessageWithToolcall(string userId, string conversationId, string userContent, string aiContent, int tokenUsed, string toolCall, string model, string replayId = "", string systemPrompt = "", string? thinkingContent = null)
        {
            try
            {
                var conversation = await _db.Conversations.FindAsync(conversationId);
                if (conversation == null)
                {
                    conversation = new IvilsonConversation
                    {
                        Id = conversationId,
                        UserId = userId,
                        Name = userContent.Length > 20 ? userContent.Substring(0, 20) : userContent,
                        Model = model ?? "gpt-3.5-turbo", // 兜底
                        UpdateAt = DateTime.UtcNow
                    };
                    _db.Conversations.Add(conversation);
                }
                else
                {
                    conversation.UpdateAt = DateTime.UtcNow;
                }
                var message = new IvilsonChatMessage();
                message.Id = Guid.NewGuid().ToString("N"); // 显式生成 Id
                message.ConversationId = conversationId;
                message.UserContent = userContent ?? string.Empty;
                message.AiContent = aiContent ?? string.Empty;
                message.ThinkingContent = thinkingContent; // ✅ 结构化存储思维链
                message.TokenUsed = tokenUsed;
                message.SystemPrompt = systemPrompt ?? string.Empty;
                message.ReplyId = replayId ?? string.Empty;
                message.ToolCallResult = toolCall ?? string.Empty;
                message.Created = DateTime.UtcNow; // 显式时间
                _db.ChatMessages.Add(message);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddMessageWithToolcall failed");
            }
        }

        public async Task AddMessage(string userId, string conversationId, string userContent, string aiContent, int tokenUsed, string model, string replayId = "", string systemPrompt = "", string? thinkingContent = null)
        {
            try
            {
                var conversation = await _db.Conversations.FindAsync(conversationId);
                if (conversation == null)
                {
                    conversation = new IvilsonConversation
                    {
                        Id = conversationId,
                        UserId = userId,
                        Name = userContent.Length > 20 ? userContent.Substring(0, 20) : userContent,
                        Model = model ?? "gpt-3.5-turbo", // 兜底
                        UpdateAt = DateTime.UtcNow
                    };
                    _db.Conversations.Add(conversation);
                }
                else
                {
                    conversation.UpdateAt = DateTime.UtcNow;
                }
                var message = new IvilsonChatMessage();
                message.Id = Guid.NewGuid().ToString("N"); // 显式生成 Id
                message.ConversationId = conversationId;
                message.UserContent = userContent ?? string.Empty;
                message.AiContent = aiContent ?? string.Empty;
                message.ThinkingContent = thinkingContent; // ✅ 结构化存储思维链
                message.TokenUsed = tokenUsed;
                message.SystemPrompt = systemPrompt ?? string.Empty;
                message.ReplyId = replayId ?? string.Empty;
                message.Created = DateTime.UtcNow; // 显式时间
                _db.ChatMessages.Add(message);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddMessage failed");
            }
        }

        public async Task<string?> AddNotificationMessage(string userId, string conversationId, string content, string messageType, string? notificationMeta)
        {
            var messageId = Guid.NewGuid().ToString("N");
            try
            {
                var conversation = await _db.Conversations.FindAsync(conversationId);
                if (conversation != null)
                {
                    conversation.UpdateAt = DateTime.UtcNow;
                }

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
                _db.ChatMessages.Add(message);
                await _db.SaveChangesAsync();
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
        {
            var conversation = await _db.Conversations.Where(p => p.UserId == userid).OrderByDescending(p => p.Created).FirstOrDefaultAsync();
            if (conversation == null)
            {
                return "";
            }
            return conversation.Id;
        }
        //根据回复id获取对话内容
        public async Task<string?> GetMessageContentByReplyId(string replyId, string userId)
        {
            var item = await _db.ChatMessages.FirstOrDefaultAsync(p => p.ReplyId == replyId);
            return item?.AiContent;
        }

        /// <summary>
        /// 获取用户的对话列表
        /// </summary>
        /// <param name="userid"></param>
        /// <returns></returns>
        public async Task<PageOf<IvilsonConversation>> GetConversations(string userid, string key, int page, int pagesize)
        {
            IQueryable<IvilsonConversation> query = _db.Conversations.Where(p => p.UserId == userid);

            // 处理关键词 key
            if (!string.IsNullOrEmpty(key))
            {
                query = query.Where(p => p.Name.Contains(key));
            }

            // 统计总数
            var total = await query.CountAsync();

            // 分页查询
            var list = await query
                .OrderByDescending(p => p.UpdateAt)
                .Skip((page - 1) * pagesize)
                .Take(pagesize)
                .ToListAsync();

            return new PageOf<IvilsonConversation>
            {
                List = list,
                PageSize = pagesize,
                Page = page,
                Total = total
            };
        }

        public async Task UpdateConversationIndexed(string cId)
        {
            var conversation = await _db.Conversations.FindAsync(cId);
            if (conversation == null)
            {
                return;
            }
            conversation.IsIndexed = true;
            _db.Update(conversation);
            await _db.SaveChangesAsync();
        }
        public async Task<PageOf<IvilsonConversation>> GetAllConversations(string key, int page, int pagesize, string? username)
        {
            IQueryable<IvilsonConversation> query = _db.Conversations.AsQueryable();

            // 处理 username
            if (!string.IsNullOrEmpty(username))
            {
                var userId = await FindUserIdByNameAsync(username);

                if (userId == null)
                {
                    return new PageOf<IvilsonConversation>
                    {
                        List = new List<IvilsonConversation>(),
                        PageSize = pagesize,
                        Page = page,
                        Total = 0
                    };
                }

                query = query.Where(p => p.UserId == userId);
            }

            // 处理关键词 key
            if (!string.IsNullOrEmpty(key))
            {
                query = query.Where(p => p.Name.Contains(key));
            }

            // 统计总数
            var total = await query.CountAsync();

            // 分页查询
            var list = await query
                .OrderByDescending(p => p.UpdateAt)
                .Skip((page - 1) * pagesize)
                .Take(pagesize)
                .ToListAsync();

            return new PageOf<IvilsonConversation>
            {
                List = list,
                PageSize = pagesize,
                Page = page,
                Total = total
            };
        }

        public async Task<string> CreateSession(string userId, string name)
        {
            var baseDir = ResolveManagedWorkspacesRoot();
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            var workspacePath = Path.Combine(baseDir, userId, Guid.NewGuid().ToString("N"));
            EnsureWorkspaceDirectoryExists(workspacePath);

            var session = new IvilsonSession
            {
                UserId = userId,
                Name = name,
                WorkspacePath = workspacePath
            };
            _db.Sessions.Add(session);
            await _db.SaveChangesAsync();
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
                && string.Equals(name.Trim(), "默认工作区", StringComparison.Ordinal))
            {
                throw new ArgumentException("The default workspace name is reserved.", nameof(name));
            }

            var existingCandidates = await _db.Sessions
                .Where(s => s.UserId == userId && !s.IsArchived)
                .OrderByDescending(s => s.Created)
                .ToListAsync();

            var existing = existingCandidates.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.WorkspacePath) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.WorkspacePath)),
                    normalized,
                    comparison));

            if (existing is not null)
            {
                var isDefaultWorkspace = string.Equals(existing.Name, "默认工作区", StringComparison.Ordinal) && !existing.IsExternalWorkspace;
                if (!isDefaultWorkspace && !string.IsNullOrWhiteSpace(name) && !string.Equals(existing.Name, name.Trim(), StringComparison.Ordinal))
                {
                    existing.Name = name.Trim();
                    await _db.SaveChangesAsync();
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
            _db.Sessions.Add(session);
            await _db.SaveChangesAsync();
            return (session.Id, session.Name, true);
        }

        public async Task<IvilsonSession?> GetSession(string sessionId)
        {
            return await _db.Sessions.Include(s => s.Conversations).FirstOrDefaultAsync(s => s.Id == sessionId);
        }

        public async Task<PageOf<IvilsonSession>> GetSessions(string userId, int page, int pageSize)
            => await GetSessionsPaged(userId, page, pageSize, includeArchived: true);

        public async Task<PageOf<IvilsonSession>> GetSessionsPaged(string userId, int page, int pageSize, bool includeArchived = false)
        {
            var safePage = Math.Max(1, page);
            var safePageSize = Math.Clamp(pageSize, 1, 100);

            var query = _db.Sessions.AsNoTracking().Where(s => s.UserId == userId);
            if (!includeArchived)
            {
                query = query.Where(s => !s.IsArchived);
            }

            var total = await query.CountAsync();
            var list = await query.OrderByDescending(s => s.Created)
                                  .Skip((safePage - 1) * safePageSize)
                                  .Take(safePageSize)
                                  .ToListAsync();
            return new PageOf<IvilsonSession> { List = list, Total = total, Page = safePage, PageSize = safePageSize };
        }

        public async Task<IvilsonSession?> RenameSession(string sessionId, string userId, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
            if (session is null)
            {
                return null;
            }

            session.Name = name.Trim();
            await _db.SaveChangesAsync();
            return session;
        }

        public async Task<IvilsonSession?> SetSessionArchived(string sessionId, string userId, bool isArchived)
        {
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
            if (session is null)
            {
                return null;
            }

            session.IsArchived = isArchived;
            await _db.SaveChangesAsync();
            return session;
        }

        public async Task<PageOf<IvilsonConversation>> GetSessionConversations(
            string sessionId,
            string userId,
            int page,
            int pageSize,
            bool includeArchived = false)
        {
            var safePage = Math.Max(1, page);
            var safePageSize = Math.Clamp(pageSize, 1, 100);

            var query = _db.Conversations.AsNoTracking()
                .Where(c => c.SessionId == sessionId && c.UserId == userId);
            if (!includeArchived)
            {
                query = query.Where(c => !c.IsArchived);
            }

            var total = await query.CountAsync();
            var list = await query.OrderByDescending(c => c.UpdateAt)
                                  .Skip((safePage - 1) * safePageSize)
                                  .Take(safePageSize)
                                  .ToListAsync();
            return new PageOf<IvilsonConversation> { List = list, Total = total, Page = safePage, PageSize = safePageSize };
        }

        public async Task<IvilsonConversation?> RenameConversation(string conversationId, string userId, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);
            if (conversation is null)
            {
                return null;
            }

            conversation.Name = name.Trim();
            conversation.UpdateAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return conversation;
        }

        public async Task<IvilsonConversation?> SetConversationArchived(string conversationId, string userId, bool isArchived)
        {
            var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);
            if (conversation is null)
            {
                return null;
            }

            conversation.IsArchived = isArchived;
            conversation.UpdateAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return conversation;
        }

        public async Task UpdateSessionStage(string sessionId, int stage)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.CurrentStage = stage;
                await _db.SaveChangesAsync();
            }
        }

        public async Task UpdateSessionFacts(string sessionId, string factsJson)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.FactsJson = factsJson;
                await _db.SaveChangesAsync();
            }
        }

        public async Task UpdateSessionPlan(string sessionId, string planJson)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.GlobalPlanJson = planJson;
                await _db.SaveChangesAsync();
            }
        }

        public async Task UpdateSessionProjectUrl(string sessionId, string url)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.ProjectUrl = url;
                await _db.SaveChangesAsync();
            }
        }

        public async Task ResetSessionExecutionState(string sessionId, int stage = 1)
        {
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                session.CurrentStage = stage;
                session.GlobalPlanJson = null;
                session.ActiveTaskId = null;
                await _db.SaveChangesAsync();
            }
        }

        public async Task ClearSession(string sessionId, string userId)
        {
            var session = await _db.Sessions
                .Include(s => s.Conversations)
                .ThenInclude(c => c.ChatMessages)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);

            if (session != null)
            {
                // 1. 清空所有对话和消息
                _db.ChatMessages.RemoveRange(session.Conversations.SelectMany(c => c.ChatMessages));
                _db.Conversations.RemoveRange(session.Conversations);
                
                // 2. 重置 Session 状态
                session.CurrentStage = 0;
                session.FactsJson = "[]";
                session.GlobalPlanJson = null;
                session.ActiveTaskId = null;
                
                await _db.SaveChangesAsync();

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
            var session = await _db.Sessions
                .Include(s => s.Conversations)
                .ThenInclude(c => c.ChatMessages)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);

            if (session != null)
            {
                // 保护默认工作区，不允许删除
                if (session.Name == "默认工作区") return;

                _db.ChatMessages.RemoveRange(session.Conversations.SelectMany(c => c.ChatMessages));
                _db.Conversations.RemoveRange(session.Conversations);
                _db.Sessions.Remove(session);
                await _db.SaveChangesAsync();

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
            var session = await _db.Sessions.FindAsync(sessionId);
            if (session == null) throw new ArgumentException("Session not found");

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
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync();

            // P1-03: 记录新建 conversation 详情
            _logger.LogInformation("[ChatService] CreateConversationInSession: ConversationId={ConversationId}, SessionId={SessionId}, UserId={UserId}, ThreadType={ThreadType}, Name={Name}",
                conversation.Id, sessionId, userId, type, conversation.Name);

            return conversation.Id;
        }

        public async Task<List<HistoryViewModel>> GetWorkspaces(string userId)
        {
            var sessions = await _db.Sessions
                .Include(s => s.Conversations)
                .ThenInclude(c => c.ChatMessages)
                .AsSplitQuery()
                .Where(s => s.UserId == userId && !s.IsArchived)
                .OrderByDescending(s => s.Created)
                .ToListAsync();

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
            var session = await _db.Sessions
                .Where(s => s.UserId == userId && s.Name == "默认工作区")
                .OrderByDescending(s => s.Created)
                .FirstOrDefaultAsync();
            return session?.Id;
        }

        /// <summary>
        /// P2-04: 获取或创建默认工作区，用于 Session Bootstrap
        /// </summary>
        public async Task<(string sessionId, bool created, string workspacePath)> GetOrCreateDefaultWorkspace(string userId)
        {
            // 查找现有默认工作区
            var existingSession = await _db.Sessions
                .Where(s => s.UserId == userId && s.Name == "默认工作区")
                .OrderByDescending(s => s.Created)
                .FirstOrDefaultAsync();

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
            _db.Sessions.Add(session);
            await _db.SaveChangesAsync();

            _logger.LogInformation("[ChatService] GetOrCreateDefaultWorkspace: 创建新默认工作区 SessionId={SessionId}, UserId={UserId}, WorkspacePath={WorkspacePath}", session.Id, userId, workspacePath);
            return (session.Id, true, workspacePath);
        }

        public async Task DeleteConversation(string conversationId, string userId)
        {
            var conversation = await _db.Conversations
                .Include(c => c.ChatMessages)
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);

            if (conversation != null)
            {
                _db.ChatMessages.RemoveRange(conversation.ChatMessages);
                _db.Conversations.Remove(conversation);
                await _db.SaveChangesAsync();
            }
        }
    }
}
