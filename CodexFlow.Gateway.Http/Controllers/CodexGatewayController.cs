// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Documents;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Remote;
using CodexFlow.Core.Services;
using CodexFlow.Gateway;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Logging;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Remote;
using CodexFlow.Orchestration.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using System.Net;
using System.Globalization;
using CodexFlow.Gateway.Http;
using CodexFlow.Gateway.Http.Remote;

namespace CodexFlow.Controllers;

/// <summary>
/// CodexGateway 控制器 — 统一的消息入口和 SSE 事件出口
/// <para>
/// codex/chat  POST — 用户发送消息，入队后立即返回 202
/// codex/sse   GET  — UI 订阅 SSE 事件流，所有 AI 输出从这里推送
/// session/bootstrap POST — Session 初始化单入口
/// </para>
/// </summary>
public class CodexGatewayController : GatewayEndpointHandler
{
    private readonly ICodexGateway _gateway;
    private readonly IChatService _chatService;
    private readonly IConversationDirectory? _conversationDirectory;
    private readonly CodexSessionManager _sessionManager;
    private readonly ITaskListService? _taskListService;
    private readonly IWorkspaceVectorStoreService? _workspaceVectorStoreService;
    private readonly ContextCompressionTriggerOptions _contextCompressionTriggerOptions;
    private readonly CodexFlow.Models.ContextCompressionOptions _contextCompressionOptions;
    private readonly ILogger<CodexGatewayController> _logger;
    private readonly IWorkspaceSettingsService? _workspaceSettingsService;
    private readonly IWorkspaceRootProvider? _workspaceRootProvider;
    private readonly IControlPlaneStateProvider? _controlPlane;
    private readonly IWorkflowAuditStore? _auditStore;
    private readonly IConfiguration? _configuration;
    private readonly IModelSettingsService? _modelSettingsService;
    private readonly IRemoteWorkspaceAuthorizationGuard? _remoteWorkspaceAuthorizationGuard;
    private readonly RemoteAttachmentStagingCoordinator? _remoteAttachmentStagingCoordinator;
    private readonly IRemoteAttachmentUploadStore? _remoteAttachmentUploadStore;
    private readonly IRemoteChatOpStore? _remoteChatOpStore;
    private readonly IRemotePendingProjection? _remotePendingProjection;

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
    /// <param name="workspaceSettingsService">Optional workspace allow-list settings service.</param>
    /// <param name="workspaceRootProvider">Optional data-directory workspace root provider.</param>
    /// <param name="controlPlane">Optional control-plane credential posture provider.</param>
    /// <param name="auditStore">Optional workflow audit store.</param>
    /// <param name="configuration">Optional configuration root for checking active model capabilities.</param>
    /// <param name="modelSettingsService">Optional model settings service for live multimodal capability lookup.</param>
    /// <param name="contextCompressionOptions">Prompt history and summary limits.</param>
    /// <param name="conversationDirectory">Conversation metadata lookup for scoped history reads.</param>
    /// <param name="remoteWorkspaceAuthorizationGuard">Optional remote workspace authorization guard.</param>
    /// <param name="remoteAttachmentStagingCoordinator">Optional pairing-isolated remote attachment staging coordinator.</param>
    /// <param name="remoteAttachmentUploadStore">Optional remote attachment upload ledger store.</param>
    /// <param name="remoteChatOpStore">Optional remote chat operation idempotency store.</param>
    /// <param name="remotePendingProjection">Optional remote pending TODO projection store.</param>
    public CodexGatewayController(
        ICodexGateway gateway,
        IChatService chatService,
        CodexSessionManager sessionManager,
        ILogger<CodexGatewayController> logger,
        ITaskListService? taskListService = null,
        IWorkspaceVectorStoreService? workspaceVectorStoreService = null,
        ContextCompressionTriggerOptions? contextCompressionTriggerOptions = null,
        IWorkspaceSettingsService? workspaceSettingsService = null,
        IWorkspaceRootProvider? workspaceRootProvider = null,
        IControlPlaneStateProvider? controlPlane = null,
        IWorkflowAuditStore? auditStore = null,
        IConfiguration? configuration = null,
        IModelSettingsService? modelSettingsService = null,
        IOptions<CodexFlow.Models.ContextCompressionOptions>? contextCompressionOptions = null,
        IConversationDirectory? conversationDirectory = null,
        IRemoteWorkspaceAuthorizationGuard? remoteWorkspaceAuthorizationGuard = null,
        RemoteAttachmentStagingCoordinator? remoteAttachmentStagingCoordinator = null,
        IRemoteAttachmentUploadStore? remoteAttachmentUploadStore = null,
        IRemoteChatOpStore? remoteChatOpStore = null,
        IRemotePendingProjection? remotePendingProjection = null)
    {
        _gateway = gateway;
        _chatService = chatService;
        _conversationDirectory = conversationDirectory;
        _sessionManager = sessionManager;
        _taskListService = taskListService;
        _workspaceVectorStoreService = workspaceVectorStoreService;
        _contextCompressionTriggerOptions = contextCompressionTriggerOptions ?? new ContextCompressionTriggerOptions();
        _contextCompressionOptions = contextCompressionOptions?.Value ?? new CodexFlow.Models.ContextCompressionOptions();
        _logger = logger;
        _workspaceSettingsService = workspaceSettingsService;
        _workspaceRootProvider = workspaceRootProvider;
        _controlPlane = controlPlane;
        _auditStore = auditStore;
        _configuration = configuration;
        _modelSettingsService = modelSettingsService;
        _remoteWorkspaceAuthorizationGuard = remoteWorkspaceAuthorizationGuard;
        _remoteAttachmentStagingCoordinator = remoteAttachmentStagingCoordinator;
        _remoteAttachmentUploadStore = remoteAttachmentUploadStore;
        _remoteChatOpStore = remoteChatOpStore;
        _remotePendingProjection = remotePendingProjection;
    }

    private async Task<IResult?> AuthorizeRemoteSessionAsync(
        string? sessionId,
        string? workspacePath,
        string? projectLocation,
        CancellationToken ct)
    {
        if (!RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            return null;
        }

        if (_remoteWorkspaceAuthorizationGuard is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse("remote_workspace_not_authorized", "远程工作区授权守卫未就绪。"));
        }

        var decision = await _remoteWorkspaceAuthorizationGuard
            .AuthorizeSessionAsync(sessionId, workspacePath, projectLocation, ct)
            .ConfigureAwait(false);
        return decision.Allowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse(decision.ReasonCode, decision.Message));
    }

    /// <summary>
    /// P2-01: Session Bootstrap — 统一的会话初始化入口
    /// <para>
    /// 若传入 targetSessionId，验证该 session 是否属于当前用户，验证通过则返回该 session
    /// 若未传入或验证失败，返回/创建默认工作区
    /// </para>
    /// </summary>
    public async Task<IResult> BootstrapSession(BootstrapRequest? request, CancellationToken ct)
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
                    if (await AuthorizeRemoteSessionAsync(targetSession.Id, targetSession.WorkspacePath, targetSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
                    {
                        return denied;
                    }

                    // Pre-warm Redis session to prevent subsequent Redis miss
                    await _sessionManager.GetOrCreateSessionAsync(
                        targetSession.Id,
                        userId,
                        targetSession.WorkspacePath ?? string.Empty,
                        (Uri?)null);

                    _logger.LogInformation("[Gateway] BootstrapSession: 恢复用户指定 SessionId={SessionId}", request.TargetSessionId);
                    FullTextAuditLogger.Log(_logger, "session_bootstrap", request.TargetSessionId, 0, 0, FullTextAuditLogger.Fields(
                        ("userId", userId),
                        ("restored", true),
                        ("sessionId", request.TargetSessionId)));

                    return Ok(new SessionBootstrapResponse
                    {
                        SessionId = targetSession.Id,
                        WorkspaceName = targetSession.Name,
                        Created = false,
                        WorkspacePath = targetSession.WorkspacePath ?? string.Empty,
                        IsDefault = targetSession.Name == "默认工作区",
                        WorkMode = targetSession.WorkMode.ToString(),
                        ModeVersion = Math.Max(1, targetSession.ModeVersion),
                        PlanModeActive = targetSession.PlanModeActive,
                        PlanModeVersion = Math.Max(1, targetSession.PlanModeVersion)
                    });
                }

                // 验证失败，记录日志
                _logger.LogWarning("[Gateway] BootstrapSession: targetSessionId 无效或不属于当前用户, SessionId={SessionId}", request.TargetSessionId);
            }

            // 没有传入 targetSessionId 或验证失败，返回/创建默认工作区
            var (sessionId, created, workspacePath) = await _chatService.GetOrCreateDefaultWorkspace(userId);
            var defaultDbSession = await _chatService.GetSession(sessionId);

            // Pre-warm Redis session for the new or existing default workspace
            await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, workspacePath, (Uri?)null);

            FullTextAuditLogger.Log(
                _logger,
                "session_bootstrap",
                sessionId,
                0,
                0,
                FullTextAuditLogger.Fields(
                    ("userId", userId),
                    ("created", created),
                    ("workspacePath", workspacePath)));

            return Ok(new SessionBootstrapResponse
            {
                SessionId = sessionId,
                WorkspaceName = "默认工作区",
                Created = created,
                WorkspacePath = workspacePath,
                IsDefault = true,
                WorkMode = (defaultDbSession?.WorkMode ?? CodexFlow.Core.Models.SessionWorkMode.Development).ToString(),
                ModeVersion = Math.Max(1, defaultDbSession?.ModeVersion ?? 1),
                PlanModeActive = defaultDbSession?.PlanModeActive ?? false,
                PlanModeVersion = Math.Max(1, defaultDbSession?.PlanModeVersion ?? 1)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gateway] BootstrapSession failed for user {UserId}", userId);
            return StatusCode(500, new BootstrapErrorResponse("Failed to bootstrap session"));
        }
    }

    /// <summary>
    /// Returns all workspaces that belong to the current user.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current user's workspace list.</returns>
    public async Task<IResult> GetWorkspaces(CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var workspaces = await _chatService.GetWorkspaces(userId);
        if (!RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            return Ok(workspaces);
        }

        if (_remoteWorkspaceAuthorizationGuard is null)
        {
            return Ok(new List<CodexFlow.Contracts.HistoryViewModel>());
        }

        var filtered = new List<CodexFlow.Contracts.HistoryViewModel>(workspaces.Count);
        foreach (var item in workspaces)
        {
            var decision = await _remoteWorkspaceAuthorizationGuard
                .AuthorizeWorkspacePathAsync(item.WorkspacePath, ct)
                .ConfigureAwait(false);
            if (decision.Allowed)
            {
                filtered.Add(item);
            }
        }

        return Ok(filtered);
    }

    /// <summary>
    /// P3-3: Opens an arbitrary local folder as a workspace after enforcing strict admission checks,
    /// persisting it to <c>Workspace:AllowedLocalRepositoryRoots</c>, and recording an audit entry.
    /// Requires control-plane credentials.
    /// </summary>
    public async Task<IResult> OpenLocalWorkspace(
        OpenLocalWorkspaceRequest request,
        CancellationToken ct = default)
    {
        if (RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse("remote_open_local_workspace_forbidden", "远程控制通道禁止直接在宿主机打开或绑定任意本地目录。"));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var controlPlaneState = _controlPlane ?? HttpContext?.RequestServices.GetService<IControlPlaneStateProvider>();
        if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlaneState))
        {
            return Forbid();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "Workspace path is required."));
        }

        if (!string.IsNullOrWhiteSpace(request.Name)
            && (string.Equals(request.Name.Trim(), "默认工作区", StringComparison.Ordinal) ||
                string.Equals(request.Name.Trim(), "日常助手", StringComparison.Ordinal)))
        {
            return BadRequest(new GatewayErrorResponse("reserved_name", "The default workspace name is reserved."));
        }

        var admissionEnv = new WorkspaceAdmissionEnvironment(
            DataDirectory: _workspaceRootProvider?.DataDirectory ?? HttpContext?.RequestServices.GetService<IConfiguration>()?["Local:DataDirectory"],
            GlobalDirectory: CodexFlowPaths.GlobalDirectory());
        var decision = LocalWorkspaceAdmissionPolicy.Evaluate(request.Path, admissionEnv);
        if (!decision.Allowed || string.IsNullOrEmpty(decision.CanonicalPath))
        {
            return BadRequest(new GatewayErrorResponse($"workspace_denied:{decision.Reason}", decision.Message));
        }

        if (_workspaceSettingsService is null)
        {
            return StatusCode(503, new GatewayErrorResponse("settings_unavailable", "Workspace settings persistence is not configured on this host."));
        }

        var addedToAllowedRoots = _workspaceSettingsService.AddAllowedLocalRepositoryRoot(decision.CanonicalPath);
        string sessionId;
        string workspaceName;
        bool created;
        try
        {
            (sessionId, workspaceName, created) = await _chatService.CreateOrOpenExternalWorkspaceSession(
                userId,
                decision.CanonicalPath,
                request.Name).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new GatewayErrorResponse("daily_external_workspace_forbidden", ex.Message));
        }

        await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, decision.CanonicalPath, (Uri?)null).ConfigureAwait(false);
        if (created && _workspaceVectorStoreService is not null)
        {
            await _workspaceVectorStoreService.EnsureCollectionAsync(sessionId).ConfigureAwait(false);
        }

        var selfApprovalProtection = controlPlaneState?.SelfApprovalProtection ?? true;
        FullTextAuditLogger.Log(
            _logger,
            "workspace_opened",
            sessionId,
            0,
            0,
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("workspacePath", decision.CanonicalPath),
                ("workspaceName", workspaceName),
                ("created", created),
                ("addedToAllowedRoots", addedToAllowedRoots),
                ("selfApprovalProtection", selfApprovalProtection)));

        if (_auditStore is not null)
        {
            var auditId = $"ws-{Guid.NewGuid():N}";
            var payloadJson = JsonSerializer.Serialize(
                new WorkspaceOpenedAuditPayload(userId, decision.CanonicalPath, workspaceName, created,
                    addedToAllowedRoots, selfApprovalProtection),
                StreamingGatewayJsonContext.Default.WorkspaceOpenedAuditPayload);
            await _auditStore.StartAsync(new WorkflowRunRecord
            {
                Id = auditId,
                Name = "workspace_opened",
                SkillName = "gateway",
                SessionId = sessionId,
                UserId = userId,
                WorkspacePath = decision.CanonicalPath,
                Status = "completed",
                Output = payloadJson,
                CompletedAtUtc = DateTime.UtcNow
            }, ct).ConfigureAwait(false);
            await _auditStore.CompleteAsync(auditId, "completed", payloadJson, null, ct).ConfigureAwait(false);
        }

        return Ok(new OpenLocalWorkspaceResponse(
            sessionId,
            workspaceName,
            decision.CanonicalPath,
            created,
            addedToAllowedRoots));
    }

    /// <summary>
    /// Gets or creates the dedicated protected Daily Assistant workspace for quick desktop access.
    /// </summary>
    public async Task<IResult> GetOrCreateDailyAssistantSession(CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var (sessionId, created, workspacePath) = await _chatService.GetOrCreateDailyAssistantWorkspace(userId).ConfigureAwait(false);
        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, workspacePath, (Uri?)null).ConfigureAwait(false);

        return Ok(new SessionBootstrapResponse
        {
            SessionId = sessionId,
            WorkspaceName = "日常助手",
            Created = created,
            WorkspacePath = workspacePath,
            IsDefault = false,
            WorkMode = (dbSession?.WorkMode ?? CodexFlow.Core.Models.SessionWorkMode.Daily).ToString(),
            ModeVersion = Math.Max(1, dbSession?.ModeVersion ?? 1),
            PlanModeActive = dbSession?.PlanModeActive ?? false,
            PlanModeVersion = Math.Max(1, dbSession?.PlanModeVersion ?? 1)
        });
    }

    /// <summary>
    /// Creates a new workspace session for the current user.
    /// </summary>
    /// <param name="name">Workspace name.</param>
    /// <param name="workMode">Optional immutable work mode (<c>development</c> or <c>daily</c>; defaults to <c>development</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new session identifier and policy snapshot.</returns>
    public async Task<IResult> CreateSession(
        string name = "New Workspace",
        string? workMode = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (!TryParseWorkMode(workMode, out var parsedWorkMode))
        {
            return BadRequest(new GatewayErrorResponse("invalid_work_mode", "workMode must be 'development' or 'daily'."));
        }

        string sessionId;
        try
        {
            sessionId = await _chatService.CreateSession(userId, name, parsedWorkMode).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new GatewayErrorResponse("reserved_name", ex.Message));
        }

        if (_workspaceVectorStoreService != null)
        {
            _logger.LogInformation("[Gateway] Ensuring workspace vector collection after session creation. SessionId={SessionId}", sessionId);
            await _workspaceVectorStoreService.EnsureCollectionAsync(sessionId);
        }

        return Ok(new SessionCreationResponse(sessionId, parsedWorkMode.ToString()));
    }

    private static bool TryParseWorkMode(string? raw, out CodexFlow.Core.Models.SessionWorkMode mode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = CodexFlow.Core.Models.SessionWorkMode.Development;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "development" or "dev":
                mode = CodexFlow.Core.Models.SessionWorkMode.Development;
                return true;
            case "daily":
                mode = CodexFlow.Core.Models.SessionWorkMode.Daily;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>
    /// Gets the persisted task list snapshot for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest task list snapshot JSON when available.</returns>
    public async Task<IResult> GetTaskList(string sessionId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await _chatService.GetSession(sessionId);
        if (session == null || session.UserId != userId) return NotFound("Session not found");
        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
            return denied;

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
    /// <param name="conversationId">Optional conversation whose context usage should be returned.</param>
    /// <returns>Estimated context usage, model budget, compression threshold, and cumulative token totals.</returns>
    public async Task<IResult> GetContextWindowSnapshot(string sessionId, CancellationToken ct = default, string? conversationId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId);
        if (dbSession == null || dbSession.UserId != userId) return NotFound("Session not found");
        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
            return denied;

        var session = await _sessionManager.GetOrCreateSessionAsync(
            sessionId,
            userId,
            dbSession.WorkspacePath ?? string.Empty,
            (Uri?)null);

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Ok(BuildContextWindowResponse(sessionId, session));
        }

        var conversation = await (_conversationDirectory?.FindAsync(conversationId, ct) ?? _chatService.Get(conversationId));
        if (conversation is null || conversation.UserId != userId || conversation.SessionId != sessionId)
        {
            return NotFound("Conversation not found");
        }

        var summary = session.HistorySummary;
        var summaryChars = string.IsNullOrWhiteSpace(summary)
            ? 0
            : Math.Min(summary.Length, Math.Max(0, _contextCompressionOptions.GatewaySummaryMaxChars));
        long estimatedChars = summaryChars > 0 ? summaryChars + 32L : 0L;
        var recentTurns = 0;
        var history = await _chatService.GetConversationMessagesAfterAsync(
            conversationId, session.GetHistoryReplayCutoff(), hardLimit: _contextCompressionOptions.PromptHistoryHardLimit);
        foreach (var message in history)
        {
            if (!string.IsNullOrEmpty(message.UserContent))
            {
                estimatedChars += message.UserContent.Length + "user".Length + 8L;
                recentTurns++;
            }
            if (!string.IsNullOrEmpty(message.AiContent))
            {
                estimatedChars += message.AiContent.Length + "assistant".Length + 8L;
                recentTurns++;
            }
        }

        var estimatedTokens = CodexSessionManager.TryGetReportedConversationContextTokens(session, conversationId)
            ?? (long)Math.Ceiling(estimatedChars / _contextCompressionTriggerOptions.ResolveEstimatedCharsPerToken());
        return Ok(BuildContextWindowResponse(sessionId, session, (estimatedTokens, estimatedChars, recentTurns, summaryChars)));
    }

    /// <summary>
    /// Manually triggers conversation context compaction for the specified session (e.g. via the /compact slash command).
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="request">Optional compaction options (conversation fallback ID and focus instruction).</param>
    /// <param name="chatClient">Optional active model client used to synthesize a concise summary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compaction outcome along with the refreshed context-window usage snapshot.</returns>
    public async Task<IResult> CompactSessionContext(
        string sessionId,
        CompactSessionContextRequest? request = null,
        IChatClient? chatClient = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId);
        if (dbSession == null || dbSession.UserId != userId)
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var channelState = _gateway.GetChannelSnapshotState(sessionId);
        if (channelState is { IsTurnActive: true })
        {
            return Conflict(new GatewayErrorResponse("turn_in_progress", "当前回合正在执行中，请等待完成或取消后再压缩上下文。"));
        }

        var session = await _sessionManager.GetOrCreateSessionAsync(
            sessionId,
            userId,
            dbSession.WorkspacePath ?? string.Empty,
            (Uri?)null);

        List<ChatTurn>? fallbackTurns = null;
        if (session.RecentTurns.Count == 0 && !string.IsNullOrWhiteSpace(request?.ConversationId))
        {
            var conversation = await (_conversationDirectory?.FindAsync(request.ConversationId, ct) ?? _chatService.Get(request.ConversationId));
            if (conversation is not null &&
                string.Equals(conversation.UserId, userId, StringComparison.Ordinal) &&
                string.Equals(conversation.SessionId, sessionId, StringComparison.Ordinal))
            {
                var dbMessages = await _chatService.GetConversationMessagesAfterAsync(
                    conversation.Id,
                    session.GetHistoryReplayCutoff(),
                    hardLimit: 120);
                if (dbMessages is { Count: > 0 })
                {
                    fallbackTurns = new List<ChatTurn>(dbMessages.Count * 2);
                    foreach (var m in dbMessages)
                    {
                        if (!string.IsNullOrWhiteSpace(m.UserContent))
                        {
                            fallbackTurns.Add(new ChatTurn("user", m.UserContent, m.Created));
                        }
                        if (!string.IsNullOrWhiteSpace(m.AiContent) && string.IsNullOrWhiteSpace(m.MessageType))
                        {
                            fallbackTurns.Add(new ChatTurn("assistant", m.AiContent, m.Created));
                        }
                    }
                }
            }
        }

        var effectiveChatClient = chatClient ?? HttpContext?.RequestServices.GetService<IChatClient>();
        Func<string, IReadOnlyList<ChatTurn>, string?, CancellationToken, Task<string?>>? modelSummarizer =
            effectiveChatClient is null
                ? null
                : (existingSummary, turns, focus, token) =>
                    SummarizeContextWithModelAsync(effectiveChatClient, existingSummary, turns, focus, token);

        var result = await _sessionManager.CompressContextAsync(
            sessionId,
            focusInstruction: request?.Focus,
            fallbackTurns: fallbackTurns,
            modelSummarizer: modelSummarizer,
            ct: ct);

        var refreshedSession = await _sessionManager.GetOrCreateSessionAsync(
            sessionId,
            userId,
            dbSession.WorkspacePath ?? string.Empty,
            (Uri?)null);

        return Ok(new CompactSessionContextResponse
        {
            SessionId = sessionId,
            Compressed = result.Compressed,
            Status = result.Status,
            CompressedTurnCount = result.CompressedTurnCount,
            EstimatedTokensBefore = result.EstimatedTokensBefore,
            EstimatedTokensAfter = result.EstimatedTokensAfter,
            SummaryChars = result.SummaryChars,
            LastCompressedAtUtc = result.LastCompressedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            ContextWindow = BuildContextWindowResponse(sessionId, refreshedSession)
        });
    }

    private SessionContextWindowResponse BuildContextWindowResponse(
        string sessionId,
        CodexSession session,
        (long Tokens, long Chars, int Turns, long SummaryChars)? conversationUsage = null)
    {
        var estimatedTokens = conversationUsage?.Tokens ?? CodexSessionManager.ResolveActiveContextTokens(session);
        var estimatedChars = conversationUsage?.Chars ?? ParseLongMetadata(session.Metadata, "ContextWindowEstimatedChars");
        var recentTurns = conversationUsage?.Turns ?? (int)ParseLongMetadata(session.Metadata, "ContextWindowRecentTurns");
        var summaryChars = conversationUsage?.SummaryChars ?? ParseLongMetadata(session.Metadata, "ContextWindowSummaryChars");
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

        return new SessionContextWindowResponse
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
        };
    }

    private static async Task<string?> SummarizeContextWithModelAsync(
        IChatClient chatClient,
        string existingSummary,
        IReadOnlyList<ChatTurn> turns,
        string? focusInstruction,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.AppendLine("【已有历史摘要】");
            sb.AppendLine(existingSummary.Length > 8_000 ? existingSummary[..8_000] + "…" : existingSummary);
            sb.AppendLine();
        }

        if (turns.Count > 0)
        {
            sb.AppendLine("【待压缩的近期对话】");
            var budget = 24_000;
            foreach (var turn in turns)
            {
                var content = turn.Content.Length > 2_000 ? turn.Content[..2_000] + "…" : turn.Content;
                var line = $"[{turn.Role}] {content}";
                if (sb.Length + line.Length > budget)
                {
                    sb.AppendLine("...(其余中间内容已截断)");
                    break;
                }
                sb.AppendLine(line);
            }
        }

        var systemPrompt =
            "你是对话上下文压缩助手。请将已有历史摘要和近期对话合并为一份精炼、结构化的上下文摘要，供后续对话继续衔接。" +
            "务必保留：1) 用户核心目标与约束；2) 已确认的技术/业务决策；3) 涉及的关键文件路径、符号名、命令与配置；4) 未完成事项与尚未解决的报错。" +
            "省略寒暄、重复试错细节与冗余日志。请直接输出摘要正文，不要添加多余前言。";
        if (!string.IsNullOrWhiteSpace(focusInstruction))
        {
            systemPrompt += $"\n用户指定的优先保留焦点：{focusInstruction}";
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString())
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
    }

    /// <summary>
    /// Returns normalized conversation history for gateway-driven chat sessions.
    /// </summary>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered message history with assistant thinking content normalized for the UI.</returns>
    public async Task<IResult> GetHistory(string conversationId, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var conversation = await _chatService.Get(conversationId);
        if (conversation == null || conversation.UserId != userId) return NotFound("Conversation not found");
        if (await AuthorizeRemoteSessionAsync(conversation.SessionId, workspacePath: null, projectLocation: null, ct).ConfigureAwait(false) is { } denied)
            return denied;

        var messages = await _chatService.GetConversationMessages(conversationId);
        var result = new JsonArray();
        foreach (var m in messages)
        {
            if (!string.IsNullOrWhiteSpace(m.MessageType))
            {
                result.Add((JsonNode)new JsonObject
                {
                    ["id"] = m.Id + "_n",
                    ["role"] = "system",
                    ["content"] = m.AiContent ?? string.Empty,
                    ["messageType"] = m.MessageType,
                    ["notificationMeta"] = m.NotificationMeta
                });
                continue;
            }

            if (!string.IsNullOrEmpty(m.UserContent))
            {
                result.Add((JsonNode)new JsonObject
                {
                    ["id"] = m.Id + "_u",
                    ["role"] = "user",
                    ["content"] = m.UserContent
                });
            }

            if (!string.IsNullOrEmpty(m.AiContent) || !string.IsNullOrEmpty(m.ThinkingContent))
            {
                var (cleanContent, normalizedThinking) = NormalizeAssistantContentForHistory(m.AiContent, m.ThinkingContent);
                result.Add((JsonNode)new JsonObject
                {
                    ["id"] = m.Id + "_a",
                    ["role"] = "assistant",
                    ["content"] = cleanContent,
                    ["thinkingContent"] = normalizedThinking
                });
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// 上传单个本地文件到会话工作区的 <c>.codexflow-attachments/</c> 目录，返回工作区相对路径与元数据。
    /// </summary>
    public async Task<IResult> UploadAttachment(
        string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        if (dbSession is null || !string.Equals(dbSession.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound("Session not found");
        }

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        const long requestSizeLimit = GatewayAttachmentService.MaxAttachmentBytes + 1024 * 1024;
        if (Request.ContentLength > requestSizeLimit)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } requestSize)
        {
            requestSize.MaxRequestBodySize = requestSizeLimit;
        }

        IFormFile? file = null;
        if (Request.HasFormContentType)
        {
            try
            {
                var form = await Request.ReadFormAsync(ct).ConfigureAwait(false);
                file = form.Files.FirstOrDefault(part =>
                    (part.Length > 0 || !string.IsNullOrEmpty(part.FileName)) &&
                    string.Equals(part.Name, "file", StringComparison.OrdinalIgnoreCase));
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException && !ct.IsCancellationRequested)
            {
                return GatewayBinding.ValidationError(HttpContext, string.Empty, $"Failed to read the request form. {ex.Message}");
            }
        }

        if (file is null)
        {
            return BadRequest(new GatewayErrorResponse("invalid_attachment", "请选择要上传的附件文件。"));
        }

        if (RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            var remoteMeta = RemoteRequestContextMiddleware.GetMetadata(HttpContext);
            if (remoteMeta is null || _remoteAttachmentStagingCoordinator is null)
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new GatewayErrorResponse("remote_staging_unavailable", "远程附件写前占位服务未就绪或缺少配对凭据。"));
            }

            await using var remoteStream = file.OpenReadStream();
            var staged = await _remoteAttachmentStagingCoordinator.UploadRemoteAttachmentAsync(
                remoteMeta.Stamp.PairingId,
                sessionId,
                dbSession.WorkspacePath,
                remoteMeta.UploadId,
                remoteMeta.FileSha256,
                file.FileName,
                file.ContentType,
                remoteStream,
                file.Length,
                ct).ConfigureAwait(false);

            return staged.Outcome switch
            {
                RemoteAttachmentUploadOutcome.Success when staged.Attachment is not null => Ok(new UploadedAttachmentDto(
                    staged.Attachment.Name,
                    staged.Attachment.RelativePath,
                    staged.Attachment.SizeBytes,
                    staged.Attachment.ContentType)),
                RemoteAttachmentUploadOutcome.BadRequest => BadRequest(new GatewayErrorResponse(
                    staged.ErrorCode ?? "invalid_attachment",
                    staged.ErrorMessage ?? "远程附件参数或校验摘要无效。")),
                RemoteAttachmentUploadOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new GatewayErrorResponse(
                    staged.ErrorCode ?? "remote_attachment_forbidden",
                    staged.ErrorMessage ?? "远程附件存储被拒绝。")),
                _ => Conflict(new GatewayErrorResponse(
                    staged.ErrorCode ?? "upload_conflict",
                    staged.ErrorMessage ?? "远程附件写前占位冲突。"))
            };
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var saved = await GatewayAttachmentService.SaveUploadedAttachmentAsync(
                dbSession.WorkspacePath,
                file.FileName,
                file.ContentType,
                stream,
                file.Length,
                ct).ConfigureAwait(false);

            return Ok(new UploadedAttachmentDto(
                saved.Name,
                saved.RelativePath,
                saved.SizeBytes,
                saved.ContentType));
        }
        catch (GatewayAttachmentValidationException ex)
        {
            return BadRequest(new GatewayErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    /// <summary>
    /// 发送用户消息 — 入队到 Gateway，立即返回 202 Accepted
    /// </summary>
    public async Task<IResult> Chat(GatewayChatRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
            return BadRequest("请求体不能为空");

        var hasAttachments = request.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(request.Message) && !hasAttachments)
            return BadRequest("消息内容和附件不能同时为空");

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest("SessionId 不能为空");

        // Ownership gate (404, never reveals whether the id exists): session, conversation and task must all belong to this caller.
        if (await FindOwnershipViolationAsync(request.SessionId, request.ConversationId, request.TaskId, userId) is { } violation)
        {
            _logger.LogWarning("[Gateway] Chat refused: {Violation}. Session={SessionId}", violation, request.SessionId);
            return NotFound("Session not found");
        }

        var isRemote = RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext);
        var remoteMeta = isRemote ? RemoteRequestContextMiddleware.GetMetadata(HttpContext) : null;
        if (isRemote && remoteMeta is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse("remote_context_missing", "远程控制通道缺少已认证的配对上下文。"));
        }

        if (await AuthorizeRemoteSessionAsync(request.SessionId, workspacePath: null, projectLocation: null, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (isRemote)
        {
            if (string.IsNullOrWhiteSpace(remoteMeta!.ClientOpId))
            {
                return BadRequest(new GatewayErrorResponse("remote_client_op_id_required", "远程发送消息必须携带 X-CF-Client-Op-Id 幂等键。"));
            }

            if (!string.IsNullOrWhiteSpace(request.GitRepoUrl) ||
                !string.IsNullOrWhiteSpace(request.Branch) ||
                !string.IsNullOrWhiteSpace(request.SystemPrompt) ||
                request.ApproveGitPush)
            {
                return BadRequest(new GatewayErrorResponse(
                    "remote_restricted_chat_fields",
                    "远程控制通道禁止传递 gitRepoUrl、branch、systemPrompt 或 approveGitPush。"));
            }
        }

        var messageContent = request.Message ?? string.Empty;
        var attachmentCount = 0;
        IReadOnlyList<VerifiedRemoteAttachment>? verifiedRemoteAttachments = null;
        if (hasAttachments)
        {
            var dbSession = await _chatService.GetSession(request.SessionId).ConfigureAwait(false);
            try
            {
                if (isRemote)
                {
                    if (request.Attachments!.Any(static a => a is null || a.IsWorkspaceReference))
                    {
                        return BadRequest(new GatewayErrorResponse(
                            "remote_workspace_reference_forbidden",
                            "远程控制通道禁止直接引用工作区既有文件，只能引用本配对已上传核验的附件。"));
                    }

                    if (_remoteAttachmentUploadStore is null)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden,
                            new GatewayErrorResponse("remote_upload_store_unavailable", "远程附件核验账本未就绪。"));
                    }
                }

                var inputs = request.Attachments!
                    .Select(static a => new ChatAttachmentInput(
                        a.Name,
                        a.RelativePath,
                        a.SizeBytes,
                        a.ContentType,
                        a.IsWorkspaceReference))
                    .ToArray();
                var validated = GatewayAttachmentService.ValidateChatAttachments(dbSession?.WorkspacePath, inputs);
                var isMultimodal = _modelSettingsService?.Read().SupportsMultimodal
                    ?? ModelSettingsService.IsMultimodalConfigured(_configuration);
                var unsupportedImages = !isMultimodal
                    ? validated.Where(static a => DocumentFileFormats.IsImageFile(a.Name, a.ContentType)).ToArray()
                    : [];
                if (unsupportedImages.Length > 0)
                {
                    var names = string.Join("、", unsupportedImages.Select(static a => a.Name));
                    return BadRequest(new GatewayErrorResponse(
                        "multimodal_not_supported",
                        $"当前模型未启用多模态能力，无法读取图片文件（{names}）。请在「设置 -> 模型配置」中配置支持多模态的模型。"));
                }

                if (isRemote)
                {
                    var verifiedList = new List<VerifiedRemoteAttachment>(validated.Count);
                    var pairingIdStr = remoteMeta!.Stamp.PairingId.ToString("D");
                    foreach (var item in validated)
                    {
                        var record = await _remoteAttachmentUploadStore!
                            .FindCompletedByRelativePathAsync(request.SessionId, item.RelativePath, ct)
                            .ConfigureAwait(false);
                        if (record is null || !string.Equals(record.PairingId, pairingIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return StatusCode(StatusCodes.Status403Forbidden, new GatewayErrorResponse(
                                "remote_attachment_not_owned",
                                $"附件 {item.RelativePath} 未经当前远程配对上传确认，拒绝接入对话。"));
                        }

                        verifiedList.Add(new VerifiedRemoteAttachment(
                            record.UploadId,
                            item.RelativePath,
                            item.Name,
                            item.ContentType,
                            record.FileSha256,
                            item.SizeBytes));
                    }

                    verifiedRemoteAttachments = verifiedList;
                    attachmentCount = verifiedList.Count;
                    // Model A: never inject <codexflow_attachments> or local paths into messageContent in remote channel.
                    messageContent = string.IsNullOrWhiteSpace(request.Message)
                        ? GatewayAttachmentService.DefaultAttachmentMessage
                        : request.Message.Trim();
                }
                else
                {
                    attachmentCount = validated.Count;
                    messageContent = GatewayAttachmentService.FormatUserMessageWithAttachments(request.Message, validated);
                }
            }
            catch (GatewayAttachmentValidationException ex)
            {
                return BadRequest(new GatewayErrorResponse(ex.ErrorCode, ex.Message));
            }
        }

        GatewayStructuredAction? validatedStructuredAction = null;
        if (request.StructuredAction != null)
        {
            if (!GatewayStructuredActionMapper.TryCreate(request.StructuredAction, out validatedStructuredAction, out var structuredError))
            {
                return BadRequest(structuredError);
            }

            if (validatedStructuredAction!.Kind is GatewayStructuredActionKind.Approve or GatewayStructuredActionKind.Execute)
            {
                var controlPlaneState = HttpContext?.RequestServices.GetService<CodexFlow.Core.Execution.Approval.IControlPlaneStateProvider>();
                if (!CodexFlow.Core.Execution.Approval.ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlaneState))
                {
                    return Forbid();
                }

                if (isRemote &&
                    (string.IsNullOrWhiteSpace(remoteMeta!.VerifiedEnvelopeKind) ||
                     string.IsNullOrWhiteSpace(remoteMeta.VerifiedEnvelopeHash) ||
                     !RemoteAttachmentStagingCoordinator.TryNormalizeSha256(remoteMeta.VerifiedEnvelopeHash, out _)))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new GatewayErrorResponse(
                        "remote_unverified_envelope",
                        "远程控制通道批准或启动计划必须携带经宿主验签的 X-CF-Verified-Envelope-* 头。"));
                }
            }
        }

        var candidateMessageId = Guid.NewGuid().ToString("N");
        if (isRemote)
        {
            if (_remoteChatOpStore is null)
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new GatewayErrorResponse("remote_chat_op_store_unavailable", "远程消息幂等账本未就绪。"));
            }

            var requestDigest = ComputeRemoteChatDigest(request, verifiedRemoteAttachments);
            var now = DateTimeOffset.UtcNow;
            var reserveResult = await _remoteChatOpStore.TryReserveAsync(
                new RemoteChatOpRecord(
                    PairingId: remoteMeta!.Stamp.PairingId.ToString("D"),
                    SessionId: request.SessionId,
                    ClientOpId: remoteMeta.ClientOpId!,
                    RequestDigest: requestDigest,
                    Status: "queued",
                    ConversationId: request.ConversationId,
                    MessageId: candidateMessageId,
                    BindingVersion: remoteMeta.Stamp.BindingVersion,
                    AccountRevocationSeq: remoteMeta.Stamp.AccountRevocationSeq,
                    DeviceRevocationSeq: remoteMeta.Stamp.DeviceRevocationSeq,
                    ResponseDtoJson: null,
                    CreatedAtUtc: now,
                    UpdatedAtUtc: now),
                ct).ConfigureAwait(false);

            if (reserveResult.Outcome == RemoteChatOpReserveOutcome.DigestConflict)
            {
                return Conflict(new GatewayErrorResponse(
                    "client_op_conflict",
                    "相同的 X-CF-Client-Op-Id 不能对应不同的消息请求体。"));
            }

            if (reserveResult.Outcome == RemoteChatOpReserveOutcome.DuplicateSameDigest && reserveResult.Existing is { } existingOp)
            {
                if (string.Equals(existingOp.Status, "cancelled_revoked", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new GatewayErrorResponse("remote_principal_revoked", "该远程消息已因配对或设备吊销而被取消。"));
                }

                if (!string.IsNullOrWhiteSpace(existingOp.ResponseDtoJson))
                {
                    var cachedAccepted = TryDeserializeAcceptedResponse(existingOp.ResponseDtoJson);
                    if (cachedAccepted is not null)
                    {
                        return Accepted(cachedAccepted);
                    }
                }

                return Accepted(new GatewayChatAcceptedResponse(
                    existingOp.MessageId ?? candidateMessageId,
                    existingOp.SessionId));
            }
        }

        FullTextAuditLogger.Log(
            _logger,
            "gateway_chat_received",
            request.SessionId,
            0,
            0,
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("conversationId", request.ConversationId),
                ("taskId", request.TaskId),
                ("messageLength", messageContent.Length),
                ("attachmentCount", attachmentCount),
                ("hasGitRepoUrl", !string.IsNullOrWhiteSpace(request.GitRepoUrl)),
                ("branch", request.Branch),
                ("approveGitPush", request.ApproveGitPush)));

        var message = new GatewayMessage
        {
            MessageId = candidateMessageId,
            Source = GatewayMessageSource.UserChat,
            SessionId = request.SessionId,
            UserId = userId,
            ConversationId = request.ConversationId,
            TaskId = request.TaskId,
            Content = messageContent,
            ControlChannel = isRemote ? ControlChannel.Remote : ControlChannel.Local,
            RemoteStamp = remoteMeta?.Stamp,
            VerifiedRemoteAttachments = verifiedRemoteAttachments,
            RemoteClientOpId = remoteMeta?.ClientOpId,
            Meta = new GatewayMessageMeta
            {
                GitRepoUrl = isRemote ? null : request.GitRepoUrl,
                Branch = isRemote ? null : request.Branch,
                ApproveGitPush = !isRemote && request.ApproveGitPush,
                ReplyId = request.ReplyId,
                SystemPrompt = isRemote ? null : request.SystemPrompt,
                ConversationMode = request.ConversationMode // P3-01: 传递会话模式
            }
        };

        // Trusted structured action: attached only here, after authentication; message text can never create one.
        if (validatedStructuredAction != null)
        {
            GatewayStructuredActions.Attach(message, validatedStructuredAction);
        }

        string messageId;
        try
        {
            messageId = await _gateway.EnqueueAsync(message, ct);
        }
        catch (GatewayMessageRejectedException ex)
        {
            if (isRemote && _remoteChatOpStore is not null && remoteMeta is not null)
            {
                await _remoteChatOpStore.UpdateStatusAsync(
                    remoteMeta.Stamp.PairingId.ToString("D"),
                    request.SessionId,
                    remoteMeta.ClientOpId!,
                    "rejected",
                    request.ConversationId,
                    candidateMessageId,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }

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
                FullTextAuditLogger.Fields(
                    ("userId", userId),
                    ("conversationId", request.ConversationId),
                    ("taskId", request.TaskId),
                    ("reason", ex.Reason),
                    ("message", ex.Message)));

            return Conflict(new GatewayChatRejectedResponse(false, ex.Reason, ex.Message, request.SessionId));
        }

        var acceptedResponse = new GatewayChatAcceptedResponse(messageId, request.SessionId);
        if (isRemote && _remoteChatOpStore is not null && remoteMeta is not null)
        {
            var acceptedJson = JsonSerializer.Serialize(
                acceptedResponse,
                StreamingGatewayJsonContext.Default.GatewayChatAcceptedResponse);
            await _remoteChatOpStore.UpdateStatusAsync(
                remoteMeta.Stamp.PairingId.ToString("D"),
                request.SessionId,
                remoteMeta.ClientOpId!,
                "queued",
                request.ConversationId,
                messageId,
                responseDtoJson: acceptedJson,
                ct: CancellationToken.None).ConfigureAwait(false);
        }

        FullTextAuditLogger.Log(
            _logger,
            "gateway_chat_accepted",
            request.SessionId,
            0,
            0,
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("messageId", messageId),
                ("conversationId", request.ConversationId),
                ("taskId", request.TaskId)));

        return Accepted(acceptedResponse);
    }

    /// <summary>
    /// SSE 订阅 — UI 通过此端点接收所有 AI 输出和系统事件
    /// <para>
    /// 支持断线重连：传入 Last-Event-ID header 或 lastEventId query param
    /// </para>
    /// </summary>
    public async Task Sse(
        string sessionId,
        long lastEventId = 0,
        CancellationToken ct = default,
        string? epoch = null)
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

        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        if (dbSession is null || !string.Equals(dbSession.UserId, userId, StringComparison.Ordinal))
        {
            Response.StatusCode = 404;
            return;
        }

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is not null)
        {
            Response.StatusCode = 403;
            return;
        }

        var isRemote = RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext);
        if (!isRemote && lastEventId == 0 && Request.Headers.TryGetValue("Last-Event-ID", out var headerValue)
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
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("lastEventId", lastEventId),
                ("userAgent", Request.Headers.UserAgent.ToString())));

        try
        {
            await foreach (var evt in _gateway.SubscribeAsync(sessionId, userId, lastEventId: lastEventId, ct: ct, channelEpoch: epoch))
            {
                var data = StreamingGatewayEventJson.Serialize(evt.Payload);
                if (isRemote)
                {
                    data = RemotePayloadSanitizer.SanitizeSsePayloadJson(data);
                }

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
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("lastEventId", lastEventId)));
    }

    /// <summary>
    /// 检查 Session 是否有活跃的 Gateway 通道
    /// </summary>
    public async Task<IResult> GetGatewayStatus(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest("sessionId is required");

        var statusUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(statusUserId)) return Unauthorized();
        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        if (dbSession is null || !string.Equals(dbSession.UserId, statusUserId, StringComparison.Ordinal))
            return NotFound("Session not found");

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, HttpContext.RequestAborted).ConfigureAwait(false) is { } denied)
            return denied;

        return Ok(new GatewayStatusResponse(
            sessionId, _gateway.IsSessionActive(sessionId), _gateway.HasActiveSubscribers(sessionId)));
    }

    public async Task<IResult> QueryRemoteUploadStatus(
        string sessionId,
        string uploadId,
        string? fileSha256 = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        if (dbSession is null || !string.Equals(dbSession.UserId, userId, StringComparison.Ordinal))
            return NotFound("Session not found");

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
            return denied;

        var remoteMeta = RemoteRequestContextMiddleware.GetMetadata(HttpContext);
        if (remoteMeta is null || _remoteAttachmentStagingCoordinator is null)
            return NotFound(new RemoteAttachmentUploadStatusResponse("not_found", uploadId, null, null));

        var result = await _remoteAttachmentStagingCoordinator
            .QueryUploadStatusAsync(remoteMeta.Stamp.PairingId, sessionId, uploadId, ct)
            .ConfigureAwait(false);
        if (string.Equals(result.Status, "not_found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new RemoteAttachmentUploadStatusResponse("not_found", uploadId, null, null));
        }

        if (!string.IsNullOrWhiteSpace(fileSha256) &&
            !string.Equals(result.FileSha256, fileSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new GatewayErrorResponse("upload_id_conflict", "Upload ID already exists with a different fileSha256."));
        }

        UploadedAttachmentDto? dto = result.Attachment is null
            ? null
            : new UploadedAttachmentDto(
                result.Attachment.Name,
                result.Attachment.RelativePath,
                result.Attachment.SizeBytes,
                result.Attachment.ContentType);
        return Ok(new RemoteAttachmentUploadStatusResponse(result.Status, result.UploadId ?? uploadId, result.FileSha256, dto));
    }

    public async Task<IResult> QueryRemoteChatOpStatus(
        string sessionId,
        string clientOpId,
        string? requestDigest = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var dbSession = await _chatService.GetSession(sessionId).ConfigureAwait(false);
        if (dbSession is null || !string.Equals(dbSession.UserId, userId, StringComparison.Ordinal))
            return NotFound("Session not found");

        if (await AuthorizeRemoteSessionAsync(dbSession.Id, dbSession.WorkspacePath, dbSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
            return denied;

        var remoteMeta = RemoteRequestContextMiddleware.GetMetadata(HttpContext);
        if (remoteMeta is null || _remoteChatOpStore is null || string.IsNullOrWhiteSpace(clientOpId))
            return NotFound(new RemoteChatOpStatusResponse("not_found", clientOpId, null, null, null));

        var op = await _remoteChatOpStore
            .FindAsync(remoteMeta.Stamp.PairingId.ToString("D"), sessionId, clientOpId.Trim(), ct)
            .ConfigureAwait(false);
        if (op is null)
            return NotFound(new RemoteChatOpStatusResponse("not_found", clientOpId, null, null, null));

        if (!string.IsNullOrWhiteSpace(requestDigest) &&
            !string.Equals(op.RequestDigest, requestDigest.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new GatewayErrorResponse("chat_op_conflict", "Client op ID already exists with a different requestDigest."));
        }

        var accepted = TryDeserializeAcceptedResponse(op.ResponseDtoJson);
        return Ok(new RemoteChatOpStatusResponse(op.Status, op.ClientOpId, op.ConversationId, op.MessageId, accepted));
    }

    public async Task<IResult> GetEventsSummary(
        string? mode = "snapshot",
        string? snapshotId = null,
        string? pageToken = null,
        long? afterEventId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var isRemote = RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext);
        var remoteMeta = isRemote ? RemoteRequestContextMiddleware.GetMetadata(HttpContext) : null;
        var bindingVersion = isRemote ? remoteMeta?.Stamp.BindingVersion : null;

        if (_remotePendingProjection is null)
        {
            return Ok(new RemotePendingSummarySnapshotResponse(
                mode ?? "snapshot",
                snapshotId,
                0,
                0,
                null,
                true,
                false,
                [],
                BindingVersion: bindingVersion));
        }

        var callerScopeKey = isRemote && remoteMeta is not null
            ? $"{userId}:{remoteMeta.Stamp.PairingId:D}"
            : userId;

        var result = await _remotePendingProjection.QuerySummaryAsync(
            mode ?? "snapshot",
            snapshotId,
            pageToken,
            afterEventId,
            Math.Clamp(limit, 1, 200),
            async (sid, token) =>
            {
                var s = await _chatService.GetSession(sid).ConfigureAwait(false);
                if (s is null || !string.Equals(s.UserId, userId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!isRemote)
                {
                    return true;
                }

                if (_remoteWorkspaceAuthorizationGuard is null)
                {
                    return false;
                }

                var decision = await _remoteWorkspaceAuthorizationGuard
                    .AuthorizeSessionAsync(s.Id, s.WorkspacePath, s.ProjectUrl, token)
                    .ConfigureAwait(false);
                return decision.Allowed;
            },
            ct,
            callerScopeKey).ConfigureAwait(false);

        return Ok(bindingVersion is null ? result : result with { BindingVersion = bindingVersion });
    }

    public static string BuildCanonicalStructuredActionV1(GatewayStructuredActionRequest? sa)
    {
        if (sa is null)
        {
            return string.Empty;
        }

        var action = (sa.Action ?? string.Empty).Trim().ToLowerInvariant();
        var startExecution = sa.StartExecution switch
        {
            true => "true",
            false => "false",
            null => string.Empty
        };
        return string.Join(
            '|',
            action,
            sa.PlanArtifactId ?? string.Empty,
            sa.PlanVersion ?? string.Empty,
            sa.PlanHash ?? string.Empty,
            sa.IdempotencyKey ?? string.Empty,
            startExecution);
    }

    public static string ComputeRemoteChatDigest(
        GatewayChatRequest request,
        IReadOnlyList<VerifiedRemoteAttachment>? verifiedRemoteAttachments = null)
    {
        var mode = string.IsNullOrWhiteSpace(request.ConversationMode)
            ? "execution"
            : request.ConversationMode.Trim().ToLowerInvariant();
        var uploadIdsCsv = string.Join(
            ",",
            (verifiedRemoteAttachments ?? [])
                .Select(static a => a.UploadId)
                .OrderBy(static id => id, StringComparer.Ordinal));
        var canonicalAction = BuildCanonicalStructuredActionV1(request.StructuredAction);
        var payload = string.Join(
            "\n",
            "CF-CHAT-OP-V1",
            request.SessionId,
            request.ConversationId ?? string.Empty,
            mode,
            uploadIdsCsv,
            canonicalAction,
            request.Message ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static GatewayChatAcceptedResponse? TryDeserializeAcceptedResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, StreamingGatewayJsonContext.Default.GatewayChatAcceptedResponse);
        }
        catch (JsonException)
        {
            return null;
        }
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
/// Request payload for manually compacting a session's conversation context.
/// </summary>
public class CompactSessionContextRequest
{
    /// <summary>Optional active conversation identifier used as fallback history source when hot memory is empty.</summary>
    public string? ConversationId { get; set; }
    /// <summary>Optional user instruction describing what information should be prioritized during compaction.</summary>
    public string? Focus { get; set; }
}

/// <summary>
/// Result of a manual session context compaction request.
/// </summary>
public class CompactSessionContextResponse
{
    /// <summary>Owning session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Whether compaction ran and updated the persisted history summary.</summary>
    public bool Compressed { get; set; }
    /// <summary>Machine-readable outcome status (<c>compressed</c> or <c>nothing_to_compress</c>).</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>Number of recent conversation turns archived into the compressed summary.</summary>
    public int CompressedTurnCount { get; set; }
    /// <summary>Estimated active context tokens before compaction.</summary>
    public long EstimatedTokensBefore { get; set; }
    /// <summary>Estimated active context tokens after compaction.</summary>
    public long EstimatedTokensAfter { get; set; }
    /// <summary>Character count of the updated history summary.</summary>
    public long SummaryChars { get; set; }
    /// <summary>UTC ISO-8601 timestamp of the latest compression watermark.</summary>
    public string? LastCompressedAtUtc { get; set; }
    /// <summary>Refreshed context-window snapshot for immediate UI synchronization.</summary>
    public SessionContextWindowResponse ContextWindow { get; set; } = new();
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

    /// <summary>Optional file attachments located inside the session workspace.</summary>
    public IReadOnlyList<ChatAttachmentDto>? Attachments { get; set; }
}

/// <summary>Metadata returned after uploading a local file attachment into the session workspace.</summary>
public sealed record UploadedAttachmentDto(
    string Name,
    string RelativePath,
    long SizeBytes,
    string ContentType);

/// <summary>Attachment descriptor sent alongside <see cref="GatewayChatRequest"/>.</summary>
public sealed record ChatAttachmentDto(
    string Name,
    string RelativePath,
    long SizeBytes,
    string? ContentType = null,
    bool IsWorkspaceReference = false);

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
    /// <summary>Immutable work mode of this session (<c>Development</c> or <c>Daily</c>).</summary>
    public string WorkMode { get; set; } = nameof(CodexFlow.Core.Models.SessionWorkMode.Development);
    /// <summary>Monotonic work mode policy version.</summary>
    public int ModeVersion { get; set; } = 1;
    /// <summary>Whether read-only Plan Mode is currently active on this session.</summary>
    public bool PlanModeActive { get; set; }
    /// <summary>Monotonic Plan Mode state version used for optimistic CAS transitions.</summary>
    public int PlanModeVersion { get; set; } = 1;
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

public sealed record OpenLocalWorkspaceRequest(
    string Path,
    string? Name = null);

public sealed record OpenLocalWorkspaceResponse(
    string SessionId,
    string WorkspaceName,
    string WorkspacePath,
    bool Created,
    bool AddedToAllowedRoots);

public sealed record GatewayErrorResponse(
    string Code,
    string Message);

internal sealed record BootstrapErrorResponse(string Error);

internal sealed record SessionCreationResponse(string SessionId, string WorkMode);

public sealed record GatewayChatAcceptedResponse(string MessageId, string SessionId);

public sealed record RemoteAttachmentUploadStatusResponse(
    string Status,
    string UploadId,
    string? FileSha256,
    UploadedAttachmentDto? Attachment);

public sealed record RemoteChatOpStatusResponse(
    string Status,
    string ClientOpId,
    string? ConversationId,
    string? MessageId,
    GatewayChatAcceptedResponse? Accepted);

internal sealed record GatewayChatRejectedResponse(bool Success, string Reason, string Message, string SessionId);

internal sealed record GatewayStatusResponse(string SessionId, bool Active, bool HasSubscribers);

internal sealed record WorkspaceOpenedAuditPayload(
    string UserId,
    string WorkspacePath,
    string WorkspaceName,
    bool Created,
    bool AddedToAllowedRoots,
    bool SelfApprovalProtection);

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
