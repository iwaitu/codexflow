// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Gateway;
using CodexFlow.Gateway;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Logging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net;
using System.Globalization;

namespace CodexFlow.Controllers;

/// <summary>
/// CodexGateway 控制器 — 统一的消息入口和 SSE 事件出口
/// <para>
/// codex/chat  POST — 用户发送消息，入队后立即返回 202
/// codex/sse   GET  — UI 订阅 SSE 事件流，所有 AI 输出从这里推送
/// session/bootstrap POST — Session 初始化单入口
/// </para>
/// </summary>
[ApiController]
[Route("api/gateway")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "ActiveUser")]
public class CodexGatewayController : ControllerBase
{
    private readonly ICodexGateway _gateway;
    private readonly IChatService _chatService;
    private readonly CodexSessionManager _sessionManager;
    private readonly ITaskListService? _taskListService;
    private readonly IWorkspaceVectorStoreService? _workspaceVectorStoreService;
    private readonly ContextCompressionTriggerOptions _contextCompressionTriggerOptions;
    private readonly ILogger<CodexGatewayController> _logger;

    /// <summary>
    /// Initializes the gateway controller.
    /// </summary>
    /// <param name="gateway">Gateway that queues chat messages and hosts SSE subscriptions.</param>
    /// <param name="chatService">Chat/session service used to validate ownership and fetch history.</param>
    /// <param name="sessionManager">Session manager used to read live context-window metadata.</param>
    /// <param name="logger">Logger used for gateway request auditing.</param>
    /// <param name="taskListService">Optional task list snapshot service.</param>
    /// <param name="workspaceVectorStoreService">Optional workspace vector lifecycle service.</param>
    /// <param name="contextCompressionTriggerOptions">Resolved model context budget and auto-compression thresholds.</param>
    public CodexGatewayController(
        ICodexGateway gateway,
        IChatService chatService,
        CodexSessionManager sessionManager,
        ILogger<CodexGatewayController> logger,
        ITaskListService? taskListService = null,
        IWorkspaceVectorStoreService? workspaceVectorStoreService = null,
        ContextCompressionTriggerOptions? contextCompressionTriggerOptions = null)
    {
        _gateway = gateway;
        _chatService = chatService;
        _sessionManager = sessionManager;
        _taskListService = taskListService;
        _workspaceVectorStoreService = workspaceVectorStoreService;
        _contextCompressionTriggerOptions = contextCompressionTriggerOptions ?? new ContextCompressionTriggerOptions();
        _logger = logger;
    }

    /// <summary>
    /// P2-01: Session Bootstrap — 统一的会话初始化入口
    /// <para>
    /// 若传入 targetSessionId，验证该 session 是否属于当前用户，验证通过则返回该 session
    /// 若未传入或验证失败，返回/创建默认工作区
    /// </para>
    /// </summary>
    [HttpPost("session/bootstrap")]
    public async Task<IActionResult> BootstrapSession([FromBody] BootstrapRequest? request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        try
        {
            // 优先验证用户传入的 targetSessionId
            if (!string.IsNullOrWhiteSpace(request?.TargetSessionId))
            {
                var targetSession = await _chatService.GetSession(request.TargetSessionId);
                if (targetSession != null && targetSession.UserId == userId)
                {
                    // Pre-warm Redis session to prevent subsequent Redis miss
                    await _sessionManager.GetOrCreateSessionAsync(
                        targetSession.Id,
                        userId,
                        targetSession.WorkspacePath ?? string.Empty,
                        (Uri?)null);

                    _logger.LogInformation("[Gateway] BootstrapSession: 恢复用户指定 SessionId={SessionId}", request.TargetSessionId);
                    FullTextAuditLogger.Log(_logger, "session_bootstrap", request.TargetSessionId, 0, 0, new
                    {
                        userId,
                        restored = true,
                        sessionId = request.TargetSessionId
                    });

                    return Ok(new SessionBootstrapResponse
                    {
                        SessionId = targetSession.Id,
                        WorkspaceName = targetSession.Name,
                        Created = false,
                        WorkspacePath = targetSession.WorkspacePath ?? string.Empty,
                        IsDefault = targetSession.Name == "默认工作区"
                    });
                }

                // 验证失败，记录日志
                _logger.LogWarning("[Gateway] BootstrapSession: targetSessionId 无效或不属于当前用户, SessionId={SessionId}", request.TargetSessionId);
            }

            // 没有传入 targetSessionId 或验证失败，返回/创建默认工作区
            var (sessionId, created, workspacePath) = await _chatService.GetOrCreateDefaultWorkspace(userId);

            // Pre-warm Redis session for the new or existing default workspace
            await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, workspacePath, (Uri?)null);

            FullTextAuditLogger.Log(
                _logger,
                "session_bootstrap",
                sessionId,
                0,
                0,
                new
                {
                    userId,
                    created,
                    workspacePath
                });

            return Ok(new SessionBootstrapResponse
            {
                SessionId = sessionId,
                WorkspaceName = "默认工作区",
                Created = created,
                WorkspacePath = workspacePath,
                IsDefault = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gateway] BootstrapSession failed for user {UserId}", userId);
            return StatusCode(500, new { error = "Failed to bootstrap session" });
        }
    }

    /// <summary>
    /// Returns all workspaces that belong to the current user.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current user's workspace list.</returns>
    [HttpGet("workspaces")]
    public async Task<IActionResult> GetWorkspaces(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var workspaces = await _chatService.GetWorkspaces(userId);
        return Ok(workspaces);
    }

    /// <summary>
    /// Creates a new workspace session for the current user.
    /// </summary>
    /// <param name="name">Workspace name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new session identifier.</returns>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession([FromQuery] string name = "New Workspace", CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var sessionId = await _chatService.CreateSession(userId, name);
        if (_workspaceVectorStoreService != null)
        {
            _logger.LogInformation("[Gateway] Ensuring workspace vector collection after session creation. SessionId={SessionId}", sessionId);
            await _workspaceVectorStoreService.EnsureCollectionAsync(sessionId);
        }

        return Ok(new { sessionId });
    }

    /// <summary>
    /// Gets the persisted task list snapshot for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest task list snapshot JSON when available.</returns>
    [HttpGet("session/{sessionId}/tasklist")]
    public async Task<IActionResult> GetTaskList(string sessionId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await _chatService.GetSession(sessionId);
        if (session == null || session.UserId != userId) return NotFound("Session not found");

        if (_taskListService == null) return NoContent();

        var json = await _taskListService.GetSnapshotAsync(sessionId);
        if (json == null) return NoContent();

        return Content(json, "application/json");
    }

    /// <summary>
    /// Returns the current session context-window usage snapshot for the UI.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Estimated context usage, model budget, compression threshold, and cumulative token totals.</returns>
    [HttpGet("session/{sessionId}/context-window")]
    public async Task<IActionResult> GetContextWindowSnapshot(string sessionId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId);
        if (dbSession == null || dbSession.UserId != userId) return NotFound("Session not found");

        var session = await _sessionManager.GetOrCreateSessionAsync(
            sessionId,
            userId,
            dbSession.WorkspacePath ?? string.Empty,
            (Uri?)null);

        var estimatedTokens = ParseLongMetadata(session.Metadata, "ContextWindowEstimatedTokens");
        var estimatedChars = ParseLongMetadata(session.Metadata, "ContextWindowEstimatedChars");
        var recentTurns = (int)ParseLongMetadata(session.Metadata, "ContextWindowRecentTurns");
        var summaryChars = ParseLongMetadata(session.Metadata, "ContextWindowSummaryChars");
        var totalPromptTokens = ParseLongMetadata(session.Metadata, "SessionTotalPromptTokens");
        var totalCompletionTokens = ParseLongMetadata(session.Metadata, "SessionTotalCompletionTokens");
        var totalCachedPromptTokens = ParseLongMetadata(session.Metadata, "SessionTotalCachedPromptTokens");
        var updatedAtUtc = session.Metadata.TryGetValue("ContextWindowUpdatedAtUtc", out var rawUpdatedAt)
            ? rawUpdatedAt
            : null;

        var modelMaxTokens = Math.Max(1, _contextCompressionTriggerOptions.ModelMaxContextTokens);
        var triggerThresholdTokens = _contextCompressionTriggerOptions.ResolveTriggerThresholdTokens();
        var usageRatio = Math.Clamp((double)estimatedTokens / modelMaxTokens, 0d, 1d);
        var triggerRatio = Math.Clamp((double)estimatedTokens / Math.Max(1, triggerThresholdTokens), 0d, 1d);

        return Ok(new SessionContextWindowResponse
        {
            SessionId = sessionId,
            EstimatedTokens = estimatedTokens,
            EstimatedChars = estimatedChars,
            RecentTurns = recentTurns,
            SummaryChars = summaryChars,
            TotalPromptTokens = totalPromptTokens,
            TotalCachedPromptTokens = totalCachedPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalSessionTokens = totalPromptTokens + totalCompletionTokens,
            ModelMaxTokens = modelMaxTokens,
            CompressionTriggerTokens = triggerThresholdTokens,
            UsageRatio = usageRatio,
            TriggerRatio = triggerRatio,
            UpdatedAtUtc = updatedAtUtc
        });
    }

    /// <summary>
    /// Returns normalized conversation history for gateway-driven chat sessions.
    /// </summary>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered message history with assistant thinking content normalized for the UI.</returns>
    [HttpGet("history/{conversationId}")]
    public async Task<IActionResult> GetHistory(string conversationId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var conversation = await _chatService.Get(conversationId);
        if (conversation == null || conversation.UserId != userId) return NotFound("Conversation not found");

        var messages = await _chatService.GetConversationMessages(conversationId);
        var result = new List<object>();
        foreach (var m in messages)
        {
            if (!string.IsNullOrWhiteSpace(m.MessageType))
            {
                result.Add(new
                {
                    id = m.Id + "_n",
                    role = "system",
                    content = m.AiContent ?? string.Empty,
                    messageType = m.MessageType,
                    notificationMeta = m.NotificationMeta
                });
                continue;
            }

            if (!string.IsNullOrEmpty(m.UserContent))
            {
                result.Add(new { id = m.Id + "_u", role = "user", content = m.UserContent });
            }

            if (!string.IsNullOrEmpty(m.AiContent) || !string.IsNullOrEmpty(m.ThinkingContent))
            {
                var (cleanContent, normalizedThinking) = NormalizeAssistantContentForHistory(m.AiContent, m.ThinkingContent);
                result.Add(new
                {
                    id = m.Id + "_a",
                    role = "assistant",
                    content = cleanContent,
                    thinkingContent = normalizedThinking
                });
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// 发送用户消息 — 入队到 Gateway，立即返回 202 Accepted
    /// </summary>
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] GatewayChatRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("消息内容不能为空");

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest("SessionId 不能为空");

        // Ownership gate (404, never reveals whether the id exists): session, conversation and task must all belong to this caller.
        if (await FindOwnershipViolationAsync(request.SessionId, request.ConversationId, request.TaskId, userId) is { } violation)
        {
            _logger.LogWarning("[Gateway] Chat refused: {Violation}. Session={SessionId}", violation, request.SessionId);
            return NotFound("Session not found");
        }

        FullTextAuditLogger.Log(
            _logger,
            "gateway_chat_received",
            request.SessionId,
            0,
            0,
            new
            {
                userId,
                conversationId = request.ConversationId,
                taskId = request.TaskId,
                messageLength = request.Message.Length,
                hasGitRepoUrl = !string.IsNullOrWhiteSpace(request.GitRepoUrl),
                branch = request.Branch,
                approveGitPush = request.ApproveGitPush
            });

        var message = new GatewayMessage
        {
            Source = GatewayMessageSource.UserChat,
            SessionId = request.SessionId,
            UserId = userId,
            ConversationId = request.ConversationId,
            TaskId = request.TaskId,
            Content = request.Message,
            Meta = new GatewayMessageMeta
            {
                GitRepoUrl = request.GitRepoUrl,
                Branch = request.Branch,
                ApproveGitPush = request.ApproveGitPush,
                ReplyId = request.ReplyId,
                SystemPrompt = request.SystemPrompt,
                ConversationMode = request.ConversationMode // P3-01: 传递会话模式
            }
        };

        // Trusted structured action: attached only here, after authentication; message text can never create one.
        if (request.StructuredAction != null)
        {
            if (!GatewayStructuredActionMapper.TryCreate(request.StructuredAction, out var structuredAction, out var structuredError))
            {
                return BadRequest(structuredError);
            }

            GatewayStructuredActions.Attach(message, structuredAction!);
        }

        string messageId;
        try
        {
            messageId = await _gateway.EnqueueAsync(message, ct);
        }
        catch (GatewayMessageRejectedException ex)
        {
            _logger.LogInformation(
                "[Gateway] Rejecting chat enqueue for session {SessionId}. Reason={Reason}",
                request.SessionId,
                ex.Reason);
            FullTextAuditLogger.Log(
                _logger,
                "gateway_chat_rejected",
                request.SessionId,
                0,
                0,
                new
                {
                    userId,
                    conversationId = request.ConversationId,
                    taskId = request.TaskId,
                    reason = ex.Reason,
                    message = ex.Message
                });

            return Conflict(new
            {
                success = false,
                reason = ex.Reason,
                message = ex.Message,
                sessionId = request.SessionId
            });
        }

        FullTextAuditLogger.Log(
            _logger,
            "gateway_chat_accepted",
            request.SessionId,
            0,
            0,
            new
            {
                userId,
                messageId,
                conversationId = request.ConversationId,
                taskId = request.TaskId
            });

        return Accepted(new { messageId, sessionId = request.SessionId });
    }

    /// <summary>
    /// SSE 订阅 — UI 通过此端点接收所有 AI 输出和系统事件
    /// <para>
    /// 支持断线重连：传入 Last-Event-ID header 或 lastEventId query param
    /// </para>
    /// </summary>
    [HttpGet("sse")]
    public async Task Sse(
        [FromQuery] string sessionId,
        [FromQuery] long lastEventId = 0,
        CancellationToken ct = default,
        [FromQuery] string? epoch = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            Response.StatusCode = 401;
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("sessionId is required", ct);
            return;
        }

        // Only the session owner may subscribe: 404 before any channel is created or replayed.
        if (!await IsSessionOwnerAsync(sessionId, userId))
        {
            Response.StatusCode = 404;
            return;
        }

        // 支持标准 SSE 断线重连 (Last-Event-ID header)
        if (lastEventId == 0 && Request.Headers.TryGetValue("Last-Event-ID", out var headerValue)
            && long.TryParse(headerValue, out var parsedId))
        {
            lastEventId = parsedId;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // Nginx SSE 兼容

        _logger.LogInformation("[Gateway SSE] 连接建立: Session={SessionId}, User={UserId}, LastEventId={LastEventId}",
            sessionId, userId, lastEventId);
        FullTextAuditLogger.Log(
            _logger,
            "gateway_sse_connected",
            sessionId,
            0,
            0,
            new
            {
                userId,
                lastEventId,
                userAgent = Request.Headers.UserAgent.ToString()
            });

        try
        {
            await foreach (var evt in _gateway.SubscribeAsync(sessionId, userId, lastEventId, ct, epoch))
            {
                var data = JsonConvert.SerializeObject(evt.Payload);

                // SSE 标准格式：id + event + data
                if (evt.Seq > 0)
                {
                    await Response.WriteAsync($"id: {evt.Seq}\n", ct);
                }
                await Response.WriteAsync($"event: {evt.Type}\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开连接，正常退出
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Gateway SSE] 连接异常断开: Session={SessionId}", sessionId);
        }

        _logger.LogInformation("[Gateway SSE] 连接关闭: Session={SessionId}, User={UserId}", sessionId, userId);
        FullTextAuditLogger.Log(
            _logger,
            "gateway_sse_closed",
            sessionId,
            0,
            0,
            new
            {
                userId,
                lastEventId
            });
    }

    /// <summary>
    /// 检查 Session 是否有活跃的 Gateway 通道
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetGatewayStatus([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest("sessionId is required");

        var statusUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(statusUserId)) return Unauthorized();
        if (!await IsSessionOwnerAsync(sessionId, statusUserId)) return NotFound("Session not found");

        return Ok(new
        {
            sessionId,
            active = _gateway.IsSessionActive(sessionId),
            hasSubscribers = _gateway.HasActiveSubscribers(sessionId)
        });
    }

    private async Task<bool> IsSessionOwnerAsync(string sessionId, string userId)
    {
        var session = await _chatService.GetSession(sessionId);
        return session != null && string.Equals(session.UserId, userId, StringComparison.Ordinal);
    }

    /// <summary>Null when session, conversation and task all belong to the caller; otherwise a short internal reason.</summary>
    private async Task<string?> FindOwnershipViolationAsync(string sessionId, string? conversationId, string? taskId, string userId)
    {
        if (!await IsSessionOwnerAsync(sessionId, userId))
        {
            return "session_not_owned";
        }

        Domain.Chat.IvilsonConversation? conversation = null;
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            conversation = await _chatService.Get(conversationId);
            if (conversation == null ||
                !string.Equals(conversation.UserId, userId, StringComparison.Ordinal) ||
                !string.Equals(conversation.SessionId, sessionId, StringComparison.Ordinal))
            {
                return "conversation_not_owned_or_other_session";
            }
        }

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            // A task id is only meaningful inside the caller's own session: the session plan or a task conversation of that session.
            var inPlan = (await _sessionManager.GetExistingSessionAsync(sessionId))?.Plan.Any(t => string.Equals(t.Id, taskId, StringComparison.Ordinal)) == true;
            var onConversation = conversation != null && string.Equals(conversation.TaskId, taskId, StringComparison.Ordinal);
            if (!inPlan && !onConversation)
            {
                return "task_not_in_session";
            }
        }

        return null;
    }

    private static (string CleanContent, string? ThinkingContent) NormalizeAssistantContentForHistory(string? aiContent, string? thinkingContent)
    {
        var content = aiContent ?? string.Empty;
        var collectedThinking = new List<string>();

        if (!string.IsNullOrWhiteSpace(thinkingContent))
        {
            collectedThinking.Add(thinkingContent.Trim());
        }

        var rawThinkRegex = new System.Text.RegularExpressions.Regex(@"<\s*think\s*>([\s\S]*?)(?:<\s*/\s*think\s*>|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var escapedThinkRegex = new System.Text.RegularExpressions.Regex(@"&lt;\s*think\s*&gt;([\s\S]*?)(?:&lt;\s*/\s*think\s*&gt;|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var rawMatches = rawThinkRegex.Matches(content);
        foreach (System.Text.RegularExpressions.Match match in rawMatches)
        {
            var think = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(think))
            {
                collectedThinking.Add(think);
            }
        }

        var escapedMatches = escapedThinkRegex.Matches(content);
        foreach (System.Text.RegularExpressions.Match match in escapedMatches)
        {
            var think = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(think))
            {
                collectedThinking.Add(think);
            }
        }

        var cleaned = rawThinkRegex.Replace(content, string.Empty);
        cleaned = escapedThinkRegex.Replace(cleaned, string.Empty).Trim();

        var normalizedThinking = string.Join("\n\n", collectedThinking.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        return (cleaned, string.IsNullOrWhiteSpace(normalizedThinking) ? null : normalizedThinking);
    }

    private static long ParseLongMetadata(Dictionary<string, string> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }
}

/// <summary>
/// Read-only snapshot of the current session context-window budget and cumulative token usage.
/// </summary>
public class SessionContextWindowResponse
{
    /// <summary>Owning session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Estimated active context usage in tokens for the current session window.</summary>
    public long EstimatedTokens { get; set; }
    /// <summary>Estimated active context usage in characters for the current session window.</summary>
    public long EstimatedChars { get; set; }
    /// <summary>Number of hot recent turns currently retained outside the compressed summary.</summary>
    public int RecentTurns { get; set; }
    /// <summary>Character count of the persisted history summary currently injected into prompts.</summary>
    public long SummaryChars { get; set; }
    /// <summary>Cumulative prompt tokens recorded for the session.</summary>
    public long TotalPromptTokens { get; set; }
    /// <summary>The part of <see cref="TotalPromptTokens"/> the provider served from its prompt cache.</summary>
    public long TotalCachedPromptTokens { get; set; }
    /// <summary>Cumulative completion tokens recorded for the session.</summary>
    public long TotalCompletionTokens { get; set; }
    /// <summary>Total cumulative prompt + completion tokens recorded for the session.</summary>
    public long TotalSessionTokens { get; set; }
    /// <summary>Configured model context window size in tokens.</summary>
    public int ModelMaxTokens { get; set; }
    /// <summary>Configured token threshold at which auto-compression is expected to trigger.</summary>
    public int CompressionTriggerTokens { get; set; }
    /// <summary>Normalized ratio of <see cref="EstimatedTokens"/> relative to <see cref="ModelMaxTokens"/>.</summary>
    public double UsageRatio { get; set; }
    /// <summary>Normalized ratio of <see cref="EstimatedTokens"/> relative to <see cref="CompressionTriggerTokens"/>.</summary>
    public double TriggerRatio { get; set; }
    /// <summary>Last time the context window snapshot metadata was refreshed, in UTC ISO-8601 format.</summary>
    public string? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Gateway Chat 请求 DTO
/// </summary>
public class GatewayChatRequest
{
    /// <summary>User message content to enqueue.</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>Owning session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Optional target conversation identifier.</summary>
    public string? ConversationId { get; set; }
    /// <summary>Optional task list identifier associated with the message.</summary>
    public string? TaskId { get; set; }
    /// <summary>Optional Git repository URL used for workspace bootstrap or clone flows.</summary>
    public string? GitRepoUrl { get; set; }
    /// <summary>Optional branch name associated with the repository request.</summary>
    public string? Branch { get; set; }
    /// <summary>Whether Git push operations are pre-approved for this request.</summary>
    public bool ApproveGitPush { get; set; }
    /// <summary>Optional reply target identifier used by threaded UI flows.</summary>
    public string? ReplyId { get; set; }
    /// <summary>Optional system prompt override.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// P3-01: 会话模式
    /// <list type="bullet">
    ///   <item><description>continue-current: 继续当前 conversation（使用传入的 conversationId）</description></item>
    ///   <item><description>reuse-master: 复用主会话（默认行为，优先复用 Master 类型对话）</description></item>
    ///   <item><description>new-thread: 强制创建新 conversation</description></item>
    /// </list>
    /// 默认值为 reuse-master（保持向后兼容）
    /// </summary>
    public string? ConversationMode { get; set; }

    /// <summary>
    /// Optional trusted structured action (UI/CLI button). When present it takes precedence over natural-language
    /// parsing of <see cref="Message"/>. Approve/reject should carry the plan id/version/hash the user actually saw.
    /// </summary>
    public GatewayStructuredActionRequest? StructuredAction { get; set; }
}

/// <summary>Structured action payload for <see cref="GatewayChatRequest.StructuredAction"/>.</summary>
public class GatewayStructuredActionRequest
{
    /// <summary>approve | reject | execute | stop | planOnly.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Plan artifact id the user acted on.</summary>
    public string? PlanArtifactId { get; set; }
    /// <summary>Plan version the user acted on.</summary>
    public string? PlanVersion { get; set; }
    /// <summary>Plan markdown hash the user acted on.</summary>
    public string? PlanHash { get; set; }
    /// <summary>Client idempotency key; repeats of the same key do not re-approve.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>For approve: also start execution (default true).</summary>
    public bool? StartExecution { get; set; }
    /// <summary>
    /// For approve: also accept the dependency advisories that already exist when the plan starts (never Critical, never what the
    /// change introduces), so verification records them as existing debt instead of blocking this plan's fixes. Default false.
    /// </summary>
    public bool? AcceptExistingDependencyDebt { get; set; }
    /// <summary>
    /// For approve: package ids whose major-version upgrade the user approves (candidates the host resolved from its own dependency scan;
    /// the user never supplies a version). The host fetches them at approval so the plan's upgrade task can restore them offline.
    /// </summary>
    public string[]? ApproveDependencyUpgrades { get; set; }
}

/// <summary>
/// P2-02: Session Bootstrap 响应 DTO
/// </summary>
public class SessionBootstrapResponse
{
    /// <summary>Resolved or created session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Display name of the workspace.</summary>
    public string WorkspaceName { get; set; } = string.Empty;
    /// <summary>Whether the bootstrap call created a new workspace.</summary>
    public bool Created { get; set; }
    /// <summary>Filesystem path of the workspace.</summary>
    public string WorkspacePath { get; set; } = string.Empty;
    /// <summary>Whether the resolved workspace is the user's default workspace.</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// P2-01: Session Bootstrap 请求 DTO
/// </summary>
public class BootstrapRequest
{
    /// <summary>
    /// 可选：目标 session ID，用于恢复用户上次选择的工作区
    /// </summary>
    public string? TargetSessionId { get; set; }
}

/// <summary>Validates a structured action request and turns it into the trusted action; the controller attaches the result.</summary>
public static class GatewayStructuredActionMapper
{
    private const int MaxUpgradeApprovals = 50;

    public static bool TryCreate(GatewayStructuredActionRequest request, out GatewayStructuredAction? action, out string? error)
    {
        action = null;
        if (!Enum.TryParse<GatewayStructuredActionKind>(request.Action, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            error = "StructuredAction.Action 无效，可选值: approve, reject, execute, stop, planOnly";
            return false;
        }

        var upgrades = request.ApproveDependencyUpgrades?.Where(id => id is not null).ToArray() ?? [];
        if (kind != GatewayStructuredActionKind.Approve && (request.AcceptExistingDependencyDebt == true || upgrades.Length > 0))
        {
            error = "StructuredAction.AcceptExistingDependencyDebt 和 ApproveDependencyUpgrades 只能用于 approve。";
            return false;
        }

        if (upgrades.Length > MaxUpgradeApprovals || upgrades.Any(id => !CodexFlow.Services.ArtifactAudit.NuGetFlatContainerVersionCatalog.IsValidPackageId(id)))
        {
            error = $"StructuredAction.ApproveDependencyUpgrades 必须是不超过 {MaxUpgradeApprovals} 个合法的包 id。";
            return false;
        }

        action = new GatewayStructuredAction(
            kind, request.PlanArtifactId, request.PlanVersion, request.PlanHash, request.IdempotencyKey,
            request.StartExecution ?? true,
            request.AcceptExistingDependencyDebt ?? false,
            upgrades.Length == 0 ? null : upgrades.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        error = null;
        return true;
    }
}
