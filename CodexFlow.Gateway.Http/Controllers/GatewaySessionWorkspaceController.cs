// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Documents;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Remote;
using CodexFlow.Domain.Chat;
using CodexFlow.Gateway;
using CodexFlow.Orchestration.Services;

using CodexFlow.Gateway.Http;
using CodexFlow.Gateway.Http.Remote;

namespace CodexFlow.Controllers;

public sealed record SessionSummaryDto(
    string SessionId,
    string Name,
    string WorkspacePath,
    int CurrentStage,
    bool IsArchived,
    bool IsExternalWorkspace,
    DateTime CreatedUtc,
    string? CurrentPlanArtifactId,
    string? ActiveTaskId,
    string WorkMode = nameof(CodexFlow.Core.Models.SessionWorkMode.Development),
    int ModeVersion = 1,
    bool PlanModeActive = false,
    int PlanModeVersion = 1);

public sealed record UpdateSessionRequest(
    string? Name = null,
    bool? IsArchived = null,
    string? WorkMode = null,
    string? Mode = null);

public sealed record UpdatePlanModeRequest(
    bool Active,
    int? ExpectedPlanModeVersion = null,
    string? Reason = null,
    string? Objective = null);

public sealed record PlanModeResponse(
    string SessionId,
    string WorkMode,
    int ModeVersion,
    bool PlanModeActive,
    int PlanModeVersion,
    DateTime? PlanModeChangedAtUtc,
    string? PlanModeChangedBy);

public sealed record ConversationSummaryDto(
    string ConversationId,
    string? SessionId,
    string Name,
    string ThreadType,
    bool IsArchived,
    string? TaskId,
    DateTime CreatedUtc,
    DateTime UpdatedAtUtc);

public sealed record CreateConversationRequest(
    string? Name = null);

public sealed record UpdateConversationRequest(
    string? Name = null,
    bool? IsArchived = null);

public sealed record PagedResponseDto<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record DeleteResourceResponse(
    bool Deleted,
    string Id);

public sealed record CancelTurnResponse(
    string SessionId,
    bool Cancelled,
    string Status);

public sealed record PlanTaskItemDto(
    string Id,
    string Title,
    string Description,
    string TaskType,
    string Status,
    IReadOnlyList<string> Dependencies,
    string? ResultNotes);

public sealed record SessionPlanDto(
    string PlanArtifactId,
    string Version,
    string MarkdownHash,
    string Status,
    string Goal,
    string Markdown,
    IReadOnlyList<PlanTaskItemDto> Tasks,
    string? HostRefusal,
    DateTime UpdatedAtUtc);

public sealed record WorkspaceFileEntryDto(
    string Name,
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

public sealed record WorkspaceDirectoryListingDto(
    string SessionId,
    string Path,
    IReadOnlyList<WorkspaceFileEntryDto> Entries,
    bool Truncated);

public sealed record WorkspaceFileContentDto(
    string SessionId,
    string Path,
    string Content,
    long SizeBytes,
    bool Truncated,
    bool IsBinary,
    string Encoding);

public sealed record WorkspaceChangedFileDto(
    string FilePath,
    string Status,
    int Additions,
    int Deletions);

public sealed record WorkspaceChangesDto(
    string SessionId,
    bool HasGitRepository,
    string? BaseRef,
    IReadOnlyList<WorkspaceChangedFileDto> Files,
    string UnifiedDiff,
    bool Truncated);

public sealed record SnapshotActiveTurnDto(
    bool IsRunning,
    string? MessageId,
    string? ConversationId,
    string? PartialContent = null,
    string? PartialThinking = null,
    bool ContentTruncated = false);

public sealed record SnapshotMessageDto(
    string Id,
    string Role,
    string Content,
    string? ThinkingContent,
    string? MessageType,
    string? NotificationMeta,
    string? ToolCallResult,
    bool Truncated,
    DateTime CreatedUtc);

public sealed record SessionSnapshotDto(
    string SessionId,
    long AsOfEventId,
    string? ChannelEpoch,
    SnapshotActiveTurnDto ActiveTurn,
    SessionSummaryDto Session,
    string? ActiveConversationId,
    IReadOnlyList<SnapshotMessageDto> Messages,
    IReadOnlyList<PendingApprovalItem> PendingApprovals,
    SessionPlanDto? CurrentPlan,
    string? TaskListJson,
    IReadOnlyList<GatewayChannelToolResultSnapshot> ActiveToolResults,
    IReadOnlyList<BackgroundJobDto> Jobs,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection,
    bool ToolResultsTruncated = false,
    IReadOnlyList<GatewayChannelToolOutputSnapshot>? ActiveToolOutputs = null,
    bool ToolOutputsTruncated = false);

/// <summary>
/// P3-4/5/6/8/11 endpoints for session &amp; conversation management, turn cancellation,
/// plan inspection, workspace file tree &amp; diff browsing, and authoritative state snapshots.
/// </summary>
public sealed class GatewaySessionWorkspaceController(
    ICodexGateway gateway,
    IChatService chatService,
    CodexSessionManager? sessionManager = null,
    IPlanArtifactStore? planArtifactStore = null,
    ITaskListService? taskListService = null,
    IExecutionApprovalBroker? approvalBroker = null,
    IJobViewService? jobViewService = null,
    IGitService? gitService = null,
    IControlPlaneStateProvider? controlPlane = null,
    DocumentReaderRouter? documentReaderRouter = null,
    IRemoteWorkspaceAuthorizationGuard? remoteWorkspaceAuthorizationGuard = null) : GatewayEndpointHandler
{
    private const string DefaultWorkspaceName = "默认工作区";
    private const string DailyAssistantWorkspaceName = "日常助手";
    private const int MaxSnapshotMessageContentChars = 32_768;

    private static bool IsReservedWorkspaceName(string? name)
        => string.Equals(name?.Trim(), DefaultWorkspaceName, StringComparison.Ordinal)
        || string.Equals(name?.Trim(), DailyAssistantWorkspaceName, StringComparison.Ordinal);

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

        if (remoteWorkspaceAuthorizationGuard is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse("remote_workspace_not_authorized", "远程工作区访问守卫不可用。"));
        }

        var decision = await remoteWorkspaceAuthorizationGuard
            .AuthorizeSessionAsync(sessionId, workspacePath, projectLocation, ct)
            .ConfigureAwait(false);
        return decision.Allowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden,
                new GatewayErrorResponse(decision.ReasonCode, decision.Message));
    }

    public async Task<IResult> ListSessions(
        int page = 1,
        int pageSize = 20,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (!RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            var paged = await chatService.GetSessionsPaged(userId, page, pageSize, includeArchived).ConfigureAwait(false);
            var items = paged.List.Select(ToSessionDto).ToArray();
            return Ok(new PagedResponseDto<SessionSummaryDto>(items, paged.Total, paged.Page, paged.PageSize));
        }

        if (remoteWorkspaceAuthorizationGuard is null)
        {
            return Ok(new PagedResponseDto<SessionSummaryDto>([], 0, page, pageSize));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var all = await chatService.GetSessionsPaged(userId, 1, 500, includeArchived).ConfigureAwait(false);
        var authorized = new List<SessionSummaryDto>();
        foreach (var s in all.List)
        {
            var decision = await remoteWorkspaceAuthorizationGuard
                .AuthorizeSessionAsync(s.Id, s.WorkspacePath, s.ProjectUrl, ct)
                .ConfigureAwait(false);
            if (decision.Allowed)
            {
                authorized.Add(ToSessionDto(s));
            }
        }

        var skip = (safePage - 1) * safePageSize;
        var pageItems = authorized.Skip(skip).Take(safePageSize).ToArray();
        return Ok(new PagedResponseDto<SessionSummaryDto>(pageItems, authorized.Count, safePage, safePageSize));
    }
    public async Task<IResult> UpdateSession(
        string sessionId,
        UpdateSessionRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "Request body is required."));
        }

        if (request.WorkMode is not null || request.Mode is not null)
        {
            return BadRequest(new GatewayErrorResponse("mode_immutable", "Session workMode is immutable after creation; start a new /daily or /dev session instead."));
        }

        if (request.Name is null && request.IsArchived is null)
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "At least one field (name or isArchived) must be specified."));
        }

        var existing = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(existing.Id, existing.WorkspacePath, existing.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (IsReservedWorkspaceName(existing.Name))
        {
            return BadRequest(new GatewayErrorResponse("default_workspace_protected", "The default workspace cannot be renamed or archived."));
        }

        var updated = existing;
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new GatewayErrorResponse("invalid_name", "Session name cannot be empty."));
            }

            if (IsReservedWorkspaceName(request.Name))
            {
                return BadRequest(new GatewayErrorResponse("reserved_name", "The default workspace name is reserved."));
            }

            updated = await chatService.RenameSession(sessionId, userId, request.Name).ConfigureAwait(false) ?? updated;
        }

        if (request.IsArchived.HasValue)
        {
            updated = await chatService.SetSessionArchived(sessionId, userId, request.IsArchived.Value).ConfigureAwait(false) ?? updated;
        }

        return Ok(ToSessionDto(updated));
    }
    public async Task<IResult> UpdatePlanMode(
        string sessionId,
        UpdatePlanModeRequest request,
        CodexFlow.Orchestration.Chat.SessionPolicyResolver? policyResolver = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "Request body is required."));
        }

        // Exiting Plan Mode is a trust-boundary transition and ALWAYS requires control-plane credentials + CAS version.
        if (!request.Active)
        {
            if (!ControlPlaneAuthConstants.IsControlPlanePrincipal(User, controlPlane))
            {
                return Forbid();
            }

            if (!request.ExpectedPlanModeVersion.HasValue)
            {
                return BadRequest(new GatewayErrorResponse("expected_version_required", "expectedPlanModeVersion is required when exiting Plan Mode."));
            }
        }

        var resolver = policyResolver ?? HttpContext?.RequestServices.GetService<CodexFlow.Orchestration.Chat.SessionPolicyResolver>();
        if (resolver is null)
        {
            return StatusCode(503, new GatewayErrorResponse("policy_resolver_unavailable", "Session policy resolver is not configured."));
        }

        if (RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext))
        {
            var planModeSession = await chatService.GetSession(sessionId).ConfigureAwait(false);
            if (planModeSession is null || !string.Equals(planModeSession.UserId, userId, StringComparison.Ordinal))
            {
                return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
            }

            if (await AuthorizeRemoteSessionAsync(planModeSession.Id, planModeSession.WorkspacePath, planModeSession.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }
        }

        var transition = await resolver.SetPlanModeAsync(
            sessionId,
            userId,
            request.Active,
            request.ExpectedPlanModeVersion,
            changedBy: request.Active ? "user_slash_or_ui" : "user_control_plane",
            reason: request.Reason,
            objective: request.Objective,
            messageId: null,
            ct: ct).ConfigureAwait(false);

        return transition.Outcome switch
        {
            CodexFlow.Orchestration.Chat.PlanModeTransitionOutcome.Updated
            or CodexFlow.Orchestration.Chat.PlanModeTransitionOutcome.NoOp when transition.Policy is not null =>
                Ok(new PlanModeResponse(
                    sessionId,
                    transition.Policy.WorkMode.ToString(),
                    transition.Policy.ModeVersion,
                    transition.Policy.PlanModeActive,
                    transition.Policy.PlanModeVersion,
                    transition.Policy.PlanModeChangedAtUtc,
                    transition.Policy.PlanModeChangedBy)),
            CodexFlow.Orchestration.Chat.PlanModeTransitionOutcome.VersionConflict =>
                Conflict(new GatewayErrorResponse("plan_mode_version_conflict", transition.Message)),
            CodexFlow.Orchestration.Chat.PlanModeTransitionOutcome.NotFound =>
                NotFound(new GatewayErrorResponse("session_not_found", transition.Message)),
            _ => BadRequest(new GatewayErrorResponse("plan_mode_transition_failed", transition.Message))
        };
    }
    public async Task<IResult> DeleteSession(
        string sessionId,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var existing = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(existing.Id, existing.WorkspacePath, existing.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (IsReservedWorkspaceName(existing.Name))
        {
            return BadRequest(new GatewayErrorResponse("default_workspace_protected", "The default workspace cannot be deleted."));
        }

        await gateway.CloseSessionAsync(sessionId).ConfigureAwait(false);
        await chatService.DeleteSession(sessionId, userId).ConfigureAwait(false);
        return Ok(new DeleteResourceResponse(true, sessionId));
    }
    public async Task<IResult> ListSessionConversations(
        string sessionId,
        int page = 1,
        int pageSize = 20,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var paged = await chatService.GetSessionConversations(sessionId, userId, page, pageSize, includeArchived).ConfigureAwait(false);
        var items = paged.List.Select(ToConversationDto).ToArray();
        return Ok(new PagedResponseDto<ConversationSummaryDto>(items, paged.Total, paged.Page, paged.PageSize));
    }
    public async Task<IResult> CreateConversation(
        string sessionId,
        CreateConversationRequest? request = null,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var initialTitle = string.IsNullOrWhiteSpace(request?.Name)
            ? CodexFlow.Gateway.ConversationTitlePolicy.DefaultNewConversationTitle
            : request.Name.Trim();

        var conversationId = await chatService.CreateConversationInSession(
            userId,
            sessionId,
            "codex-v2",
            initialTitle,
            null,
            ConversationThreadType.Chat).ConfigureAwait(false);

        var created = await chatService.Get(conversationId).ConfigureAwait(false);
        var dto = created is not null
            ? ToConversationDto(created)
            : new ConversationSummaryDto(
                conversationId,
                sessionId,
                initialTitle,
                ConversationThreadType.Chat.ToString(),
                false,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow);

        return StatusCode(StatusCodes.Status201Created, dto);
    }
    public async Task<IResult> UpdateConversation(
        string conversationId,
        UpdateConversationRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null || (request.Name is null && request.IsArchived is null))
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "At least one field (name or isArchived) must be specified."));
        }

        var existing = await chatService.Get(conversationId).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("conversation_not_found", "Conversation not found."));
        }

        if (await AuthorizeRemoteSessionAsync(existing.SessionId, workspacePath: null, projectLocation: null, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var updated = existing;
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new GatewayErrorResponse("invalid_name", "Conversation name cannot be empty."));
            }

            updated = await chatService.RenameConversation(conversationId, userId, request.Name).ConfigureAwait(false) ?? updated;
        }

        if (request.IsArchived.HasValue)
        {
            updated = await chatService.SetConversationArchived(conversationId, userId, request.IsArchived.Value).ConfigureAwait(false) ?? updated;
        }

        return Ok(ToConversationDto(updated));
    }
    public async Task<IResult> DeleteConversation(
        string conversationId,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var existing = await chatService.Get(conversationId).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("conversation_not_found", "Conversation not found."));
        }

        if (await AuthorizeRemoteSessionAsync(existing.SessionId, workspacePath: null, projectLocation: null, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        await chatService.DeleteConversation(conversationId, userId).ConfigureAwait(false);
        return Ok(new DeleteResourceResponse(true, conversationId));
    }
    public async Task<IResult> CancelTurn(
        string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var cancelled = await gateway.CancelTurnAsync(sessionId, ct).ConfigureAwait(false);
        return Ok(new CancelTurnResponse(sessionId, cancelled, cancelled ? "cancelled" : "idle"));
    }
    public async Task<IResult> GetPlan(
        string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var plan = await ResolveCurrentPlanDtoAsync(session, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return NotFound(new GatewayErrorResponse("plan_not_found", "No plan exists for this session."));
        }

        return Ok(plan);
    }
    public async Task<IResult> ListWorkspaceFiles(
        string sessionId,
        string? path = null,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var listing = WorkspaceFileExplorerService.ListDirectory(session.WorkspacePath, path);
        if (!listing.Success)
        {
            return MapFileExplorerError(listing.ErrorCode, listing.ErrorMessage);
        }

        var entries = listing.Entries
            .Select(e => new WorkspaceFileEntryDto(e.Name, e.Path, e.IsDirectory, e.SizeBytes, e.LastModifiedUtc))
            .ToArray();
        return Ok(new WorkspaceDirectoryListingDto(sessionId, listing.Path, entries, listing.Truncated));
    }
    public async Task<IResult> ReadWorkspaceFileContent(
        string sessionId,
        string? path = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var content = await WorkspaceFileExplorerService
            .ReadFileContentAsync(session.WorkspacePath, path, documentRouter: documentReaderRouter, ct: ct)
            .ConfigureAwait(false);
        if (!content.Success)
        {
            return MapFileExplorerError(content.ErrorCode, content.ErrorMessage);
        }

        return Ok(new WorkspaceFileContentDto(
            sessionId,
            content.Path,
            content.Content,
            content.SizeBytes,
            content.Truncated,
            content.IsBinary,
            content.Encoding));
    }
    public async Task<IResult> GetWorkspaceChanges(
        string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (gitService is null)
        {
            return Ok(new WorkspaceChangesDto(sessionId, false, null, [], string.Empty, false));
        }

        var result = await WorkspaceFileExplorerService
            .GetChangesAsync(session.WorkspacePath, gitService, ct: ct)
            .ConfigureAwait(false);
        var files = result.Files
            .Select(f => new WorkspaceChangedFileDto(f.FilePath, f.Status, f.Additions, f.Deletions))
            .ToArray();
        return Ok(new WorkspaceChangesDto(
            sessionId,
            result.HasGitRepository,
            result.BaseRef,
            files,
            result.UnifiedDiff,
            result.Truncated));
    }
    public async Task<IResult> GetSnapshot(
        string sessionId,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (await AuthorizeRemoteSessionAsync(session.Id, session.WorkspacePath, session.ProjectUrl, ct).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var channelState = gateway.GetChannelSnapshotState(sessionId);
        string? resolvedConvId;
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            var owned = session.Conversations.FirstOrDefault(c =>
                string.Equals(c.Id, conversationId, StringComparison.Ordinal));
            if (owned is null)
            {
                var fetched = await chatService.Get(conversationId).ConfigureAwait(false);
                if (fetched is null
                    || !string.Equals(fetched.SessionId, sessionId, StringComparison.Ordinal)
                    || !string.Equals(fetched.UserId, userId, StringComparison.Ordinal))
                {
                    return NotFound(new GatewayErrorResponse("conversation_not_found", "Conversation not found."));
                }

                owned = fetched;
            }

            resolvedConvId = owned.Id;
        }
        else
        {
            resolvedConvId = ResolveSnapshotConversationId(session, channelState);
        }

        var messages = new List<SnapshotMessageDto>();
        string? latestPersistedToolEnvelope = null;
        var latestPersistedToolTextTruncated = false;
        var isRemote = RemoteRequestContextMiddleware.IsRemoteRequest(HttpContext);
        if (!string.IsNullOrWhiteSpace(resolvedConvId))
        {
            var rawMessages = await chatService.GetConversationMessages(resolvedConvId).ConfigureAwait(false);
            foreach (var m in rawMessages)
            {
                if (!string.IsNullOrWhiteSpace(m.MessageType))
                {
                    var (bounded, truncated) = ToolEventSanitizer.TruncateText(m.AiContent, MaxSnapshotMessageContentChars);
                    var safeMeta = isRemote && !string.IsNullOrWhiteSpace(m.NotificationMeta)
                        ? RemotePayloadSanitizer.SanitizeSsePayloadJson(m.NotificationMeta)
                        : m.NotificationMeta;
                    messages.Add(new SnapshotMessageDto(
                        m.Id + "_n",
                        "system",
                        bounded,
                        null,
                        m.MessageType,
                        safeMeta,
                        null,
                        truncated,
                        m.Created));
                    continue;
                }

                if (!string.IsNullOrEmpty(m.UserContent))
                {
                    var (boundedUser, userTruncated) = ToolEventSanitizer.TruncateText(m.UserContent, MaxSnapshotMessageContentChars);
                    messages.Add(new SnapshotMessageDto(
                        m.Id + "_u",
                        "user",
                        boundedUser,
                        null,
                        null,
                        null,
                        null,
                        userTruncated,
                        m.Created));
                }

                if (!string.IsNullOrEmpty(m.AiContent) || !string.IsNullOrEmpty(m.ThinkingContent) || !string.IsNullOrEmpty(m.ToolCallResult))
                {
                    var (boundedAi, aiTruncated) = ToolEventSanitizer.TruncateText(m.AiContent, MaxSnapshotMessageContentChars);
                    var (boundedThinking, thinkTruncated) = ToolEventSanitizer.TruncateText(m.ThinkingContent, MaxSnapshotMessageContentChars);
                    var (boundedTool, toolTruncated) = BoundSnapshotToolHistory(m.ToolCallResult);
                    if (isRemote && !string.IsNullOrEmpty(boundedTool))
                    {
                        boundedTool = RemotePayloadSanitizer.SanitizeSsePayloadJson(boundedTool);
                    }
                    if (!string.IsNullOrWhiteSpace(m.ToolCallResult))
                    {
                        latestPersistedToolEnvelope = m.ToolCallResult;
                        latestPersistedToolTextTruncated = toolTruncated;
                    }

                    messages.Add(new SnapshotMessageDto(
                        m.Id + "_a",
                        "assistant",
                        boundedAi,
                        string.IsNullOrEmpty(boundedThinking) ? null : boundedThinking,
                        null,
                        null,
                        string.IsNullOrEmpty(boundedTool) ? null : boundedTool,
                        aiTruncated || thinkTruncated || toolTruncated,
                        m.Created));
                }
            }
        }

        IReadOnlyList<GatewayChannelToolResultSnapshot> recoveredToolResults;
        bool toolResultsTruncated;
        IReadOnlyList<GatewayChannelToolOutputSnapshot> recoveredToolOutputs;
        bool toolOutputsTruncated;
        if (channelState is { IsTurnActive: true })
        {
            recoveredToolResults = channelState.ActiveToolResults;
            toolResultsTruncated = channelState.ToolResultsTruncated;
            recoveredToolOutputs = channelState.ActiveToolOutputs ?? [];
            toolOutputsTruncated = channelState.ToolOutputsTruncated;
        }
        else
        {
            (recoveredToolResults, toolResultsTruncated, recoveredToolOutputs, toolOutputsTruncated) =
                ParsePersistedToolEnvelope(latestPersistedToolEnvelope, latestPersistedToolTextTruncated);

            // If this live channel instance has already observed completed-turn or post-restart external telemetry,
            // merge its in-memory state cleanly on top of SQLite persistence without ever masking pre-restart DB state.
            if (channelState is { HasTurnTelemetry: true })
            {
                if (channelState.ActiveToolResults.Count > 0)
                {
                    recoveredToolResults = channelState.ActiveToolResults;
                }

                toolResultsTruncated |= channelState.ToolResultsTruncated;
                if (channelState.ActiveToolOutputs is { Count: > 0 } liveOut)
                {
                    var mergedOut = new List<GatewayChannelToolOutputSnapshot>(recoveredToolOutputs);
                    foreach (var item in liveOut)
                    {
                        if (!mergedOut.Contains(item))
                        {
                            if (mergedOut.Count < 200)
                            {
                                mergedOut.Add(item);
                            }
                            else
                            {
                                toolOutputsTruncated = true;
                                break;
                            }
                        }
                    }

                    recoveredToolOutputs = mergedOut;
                }

                toolOutputsTruncated |= channelState.ToolOutputsTruncated;
            }
        }

        var pendingApprovals = approvalBroker?.GetPendingApprovals(sessionId) ?? [];
        var currentPlan = await ResolveCurrentPlanDtoAsync(session, ct).ConfigureAwait(false);
        var taskListJson = taskListService is not null
            ? await taskListService.GetSnapshotAsync(sessionId, ct).ConfigureAwait(false)
            : null;
        var jobs = jobViewService is not null
            ? (await jobViewService.GetSessionJobsAsync(sessionId).ConfigureAwait(false)).ToArray()
            : [];

        return Ok(new SessionSnapshotDto(
            SessionId: sessionId,
            AsOfEventId: channelState?.AsOfEventId ?? 0,
            ChannelEpoch: channelState?.Epoch,
            ActiveTurn: new SnapshotActiveTurnDto(
                channelState?.IsTurnActive ?? false,
                channelState?.ActiveMessageId,
                channelState?.ActiveConversationId,
                channelState?.PartialContent,
                channelState?.PartialThinking,
                channelState?.ActiveContentTruncated ?? false),
            Session: ToSessionDto(session),
            ActiveConversationId: resolvedConvId,
            Messages: messages,
            PendingApprovals: pendingApprovals,
            CurrentPlan: currentPlan,
            TaskListJson: taskListJson,
            ActiveToolResults: recoveredToolResults,
            Jobs: jobs,
            ControlPlaneAvailable: controlPlane?.IsControlPlaneAvailable ?? true,
            SelfApprovalProtection: controlPlane?.SelfApprovalProtection ?? true,
            ToolResultsTruncated: toolResultsTruncated,
            ActiveToolOutputs: recoveredToolOutputs,
            ToolOutputsTruncated: toolOutputsTruncated));
    }

    private static (
        IReadOnlyList<GatewayChannelToolResultSnapshot> Results,
        bool ResultsTruncated,
        IReadOnlyList<GatewayChannelToolOutputSnapshot> Outputs,
        bool OutputsTruncated) ParsePersistedToolEnvelope(string? rawToolCallResult, bool rawTextTruncated)
    {
        if (string.IsNullOrWhiteSpace(rawToolCallResult))
        {
            return ([], false, [], false);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawToolCallResult);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var results = doc.RootElement.TryGetProperty("results", out var resEl)
                    ? System.Text.Json.JsonSerializer.Deserialize(resEl, StreamingGatewayJsonContext.Default.GatewayChannelToolResultSnapshotArray) ?? []
                    : [];
                var outputs = doc.RootElement.TryGetProperty("outputs", out var outEl)
                    ? System.Text.Json.JsonSerializer.Deserialize(outEl, StreamingGatewayJsonContext.Default.GatewayChannelToolOutputSnapshotArray) ?? []
                    : [];
                var resTruncated = rawTextTruncated ||
                    (doc.RootElement.TryGetProperty("resultsTruncated", out var rtEl) && rtEl.ValueKind == System.Text.Json.JsonValueKind.True);
                var outTruncated = rawTextTruncated ||
                    (doc.RootElement.TryGetProperty("outputsTruncated", out var otEl) && otEl.ValueKind == System.Text.Json.JsonValueKind.True) ||
                    (outputs.Length == 0 && results.Length > 0 && !doc.RootElement.TryGetProperty("outputs", out _));
                return (results, resTruncated, outputs, outTruncated);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Legacy raw tool text row: outputs are unavailable after restart, so signal truncation accurately.
        }

        return ([], rawTextTruncated, [], true);
    }

    private static (string Text, bool Truncated) BoundSnapshotToolHistory(string? stored)
    {
        if (stored is null || stored.Length <= MaxSnapshotMessageContentChars)
        {
            return (stored ?? string.Empty, false);
        }

        try
        {
            if (JsonNode.Parse(stored) is JsonObject envelope && envelope["results"] is JsonArray results)
            {
                var outputs = envelope["outputs"] as JsonArray ?? [];
                // Reserve room for the envelope and delimiters. Keep failures first when the budget runs out.
                var remaining = MaxSnapshotMessageContentChars - 128;
                var retainedResults = new List<(int Index, JsonObject Result)>();
                var resultsTruncated = envelope["resultsTruncated"]?.GetValue<bool>() == true;
                foreach (var (node, index) in results.Select((node, index) => (node, index))
                             .OrderBy(item => ResultSucceeded(item.node)))
                {
                    if (node is not JsonObject result)
                    {
                        resultsTruncated = true;
                        continue;
                    }

                    var copy = (JsonObject)result.DeepClone();
                    var messageKey = copy.ContainsKey("Result") ? "Result" : "result";
                    if (copy[messageKey] is JsonValue value && value.TryGetValue<string>(out var resultText))
                    {
                        var (excerpt, cropped) = ToolEventSanitizer.TruncateText(
                            resultText, ResultSucceeded(copy) ? 180 : 1024);
                        if (cropped)
                        {
                            copy[messageKey] = excerpt;
                            copy[copy.ContainsKey("Truncated") ? "Truncated" : "truncated"] = true;
                            resultsTruncated = true;
                        }
                    }

                    var size = copy.ToJsonString().Length + 1;
                    if (size > remaining)
                    {
                        resultsTruncated = true;
                        continue;
                    }

                    remaining -= size;
                    retainedResults.Add((index, copy));
                }

                var retainedOutputs = new JsonArray();
                foreach (var output in outputs)
                {
                    if (output is null) continue;
                    var size = output.ToJsonString().Length + 1;
                    if (size > remaining) continue;
                    retainedOutputs.Add(output.DeepClone());
                    remaining -= size;
                }

                return (new JsonObject
                {
                    ["results"] = new JsonArray(retainedResults.OrderBy(item => item.Index)
                        .Select(item => (JsonNode)item.Result).ToArray()),
                    ["resultsTruncated"] = resultsTruncated,
                    ["outputs"] = retainedOutputs,
                    ["outputsTruncated"] = retainedOutputs.Count != outputs.Count ||
                        envelope["outputsTruncated"]?.GetValue<bool>() == true
                }.ToJsonString(), true);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A legacy text result or an invalid envelope can still be shown as an excerpt.
        }

        return ToolEventSanitizer.TruncateText(stored, MaxSnapshotMessageContentChars);
    }

    private static bool ResultSucceeded(JsonNode? result)
        => result is JsonObject row &&
           (row["Success"] ?? row["success"]) is JsonValue value &&
           value.TryGetValue<bool>(out var succeeded) && succeeded;

    private async Task<SessionPlanDto?> ResolveCurrentPlanDtoAsync(IvilsonSession dbSession, CancellationToken ct)
    {
        PlanArtifact? artifact = null;
        if (planArtifactStore is not null)
        {
            artifact = !string.IsNullOrWhiteSpace(dbSession.CurrentPlanArtifactId)
                ? await planArtifactStore.GetAsync(dbSession.CurrentPlanArtifactId, ct).ConfigureAwait(false)
                : await planArtifactStore.GetCurrentAsync(dbSession.Id, ct).ConfigureAwait(false);
        }

        if (artifact is not null && string.Equals(artifact.SessionId, dbSession.Id, StringComparison.Ordinal))
        {
            var tasks = artifact.Tasks
                .Select(t => new PlanTaskItemDto(
                    t.Id,
                    t.Title,
                    t.Description,
                    t.TaskType.ToString(),
                    t.Status.ToString(),
                    t.Dependencies,
                    t.ResultNotes))
                .ToArray();

            var refusal = PlanHostRefusal.Refuses(artifact, out var refusalText) ? refusalText : null;
            return new SessionPlanDto(
                artifact.Id,
                artifact.Version,
                PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown),
                artifact.Status.ToString(),
                artifact.Goal,
                artifact.Markdown,
                tasks,
                refusal,
                artifact.UpdatedAtUtc);
        }

        if (sessionManager is not null)
        {
            var memSession = await sessionManager.GetExistingSessionAsync(dbSession.Id).ConfigureAwait(false);
            if (memSession is not null && memSession.Plan.Count > 0)
            {
                var tasks = memSession.Plan
                    .Select(t => new PlanTaskItemDto(
                        t.Id,
                        t.Title,
                        t.Description,
                        t.TaskType.ToString(),
                        t.Status.ToString(),
                        t.Dependencies,
                        t.ResultNotes))
                    .ToArray();

                return new SessionPlanDto(
                    "session-plan",
                    memSession.PlanVersion ?? "0",
                    GatewayPlanIdentityResolver.HashTaskList(memSession.Plan),
                    "Active",
                    memSession.ProjectSummary ?? dbSession.Name,
                    string.Empty,
                    tasks,
                    null,
                    memSession.PlanGeneratedAtUtc ?? dbSession.Created);
            }
        }

        return null;
    }

    private static string? ResolveSnapshotConversationId(
        IvilsonSession session,
        GatewayChannelSnapshotState? channelState)
    {
        if (!string.IsNullOrWhiteSpace(channelState?.ActiveConversationId))
        {
            return channelState.ActiveConversationId;
        }

        return session.Conversations
            .Where(c => !c.IsArchived)
            .OrderByDescending(c => c.ThreadType == ConversationThreadType.Master)
            .ThenByDescending(c => c.UpdateAt)
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    private IResult MapFileExplorerError(WorkspaceFileErrorCode code, string? message)
    {
        var err = new GatewayErrorResponse($"workspace_file:{code}", message ?? "Invalid file request.");
        return code switch
        {
            WorkspaceFileErrorCode.NotFound => NotFound(err),
            WorkspaceFileErrorCode.PathTraversal or WorkspaceFileErrorCode.LinkedPath or WorkspaceFileErrorCode.InvalidPath or WorkspaceFileErrorCode.IsADirectory => BadRequest(err),
            WorkspaceFileErrorCode.WorkspaceUnavailable => NotFound(err),
            _ => BadRequest(err)
        };
    }

    private static SessionSummaryDto ToSessionDto(IvilsonSession s)
        => new(
            s.Id,
            s.Name,
            s.WorkspacePath,
            s.CurrentStage,
            s.IsArchived,
            s.IsExternalWorkspace,
            s.Created,
            s.CurrentPlanArtifactId,
            s.ActiveTaskId,
            s.WorkMode.ToString(),
            Math.Max(1, s.ModeVersion),
            s.PlanModeActive,
            Math.Max(1, s.PlanModeVersion));

    private static ConversationSummaryDto ToConversationDto(IvilsonConversation c)
        => new(
            c.Id,
            c.SessionId,
            c.Name,
            c.ThreadType.ToString(),
            c.IsArchived,
            c.TaskId,
            c.Created,
            c.UpdateAt);
}
