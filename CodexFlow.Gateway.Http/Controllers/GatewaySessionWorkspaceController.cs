// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Domain.Chat;
using CodexFlow.Gateway;
using CodexFlow.Orchestration.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    string? ActiveTaskId);

public sealed record UpdateSessionRequest(
    string? Name = null,
    bool? IsArchived = null);

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
[ApiController]
[Route("api/gateway")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "ActiveUser")]
public sealed class GatewaySessionWorkspaceController(
    ICodexGateway gateway,
    IChatService chatService,
    CodexSessionManager? sessionManager = null,
    IPlanArtifactStore? planArtifactStore = null,
    ITaskListService? taskListService = null,
    IExecutionApprovalBroker? approvalBroker = null,
    IJobViewService? jobViewService = null,
    IGitService? gitService = null,
    IControlPlaneStateProvider? controlPlane = null) : ControllerBase
{
    private const string DefaultWorkspaceName = "默认工作区";
    private const int MaxSnapshotMessageContentChars = 32_768;

    [HttpGet("sessions")]
    [ProducesResponseType(typeof(PagedResponseDto<SessionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponseDto<SessionSummaryDto>>> ListSessions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var paged = await chatService.GetSessionsPaged(userId, page, pageSize, includeArchived).ConfigureAwait(false);
        var items = paged.List.Select(ToSessionDto).ToArray();
        return Ok(new PagedResponseDto<SessionSummaryDto>(items, paged.Total, paged.Page, paged.PageSize));
    }

    [HttpPatch("session/{sessionId}")]
    [ProducesResponseType(typeof(SessionSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SessionSummaryDto>> UpdateSession(
        [FromRoute] string sessionId,
        [FromBody] UpdateSessionRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null || (request.Name is null && request.IsArchived is null))
        {
            return BadRequest(new GatewayErrorResponse("invalid_request", "At least one field (name or isArchived) must be specified."));
        }

        var existing = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        if (string.Equals(existing.Name, DefaultWorkspaceName, StringComparison.Ordinal))
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

            if (string.Equals(request.Name.Trim(), DefaultWorkspaceName, StringComparison.Ordinal))
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

    [HttpDelete("session/{sessionId}")]
    [ProducesResponseType(typeof(DeleteResourceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResourceResponse>> DeleteSession(
        [FromRoute] string sessionId,
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

        if (string.Equals(existing.Name, DefaultWorkspaceName, StringComparison.Ordinal))
        {
            return BadRequest(new GatewayErrorResponse("default_workspace_protected", "The default workspace cannot be deleted."));
        }

        await gateway.CloseSessionAsync(sessionId).ConfigureAwait(false);
        await chatService.DeleteSession(sessionId, userId).ConfigureAwait(false);
        return Ok(new DeleteResourceResponse(true, sessionId));
    }

    [HttpGet("session/{sessionId}/conversations")]
    [ProducesResponseType(typeof(PagedResponseDto<ConversationSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponseDto<ConversationSummaryDto>>> ListSessionConversations(
        [FromRoute] string sessionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool includeArchived = false,
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

        var paged = await chatService.GetSessionConversations(sessionId, userId, page, pageSize, includeArchived).ConfigureAwait(false);
        var items = paged.List.Select(ToConversationDto).ToArray();
        return Ok(new PagedResponseDto<ConversationSummaryDto>(items, paged.Total, paged.Page, paged.PageSize));
    }

    [HttpPost("session/{sessionId}/conversations")]
    [ProducesResponseType(typeof(ConversationSummaryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationSummaryDto>> CreateConversation(
        [FromRoute] string sessionId,
        [FromBody] CreateConversationRequest? request = null,
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

    [HttpPatch("conversations/{conversationId}")]
    [ProducesResponseType(typeof(ConversationSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationSummaryDto>> UpdateConversation(
        [FromRoute] string conversationId,
        [FromBody] UpdateConversationRequest request,
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

    [HttpDelete("conversations/{conversationId}")]
    [ProducesResponseType(typeof(DeleteResourceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResourceResponse>> DeleteConversation(
        [FromRoute] string conversationId,
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

        await chatService.DeleteConversation(conversationId, userId).ConfigureAwait(false);
        return Ok(new DeleteResourceResponse(true, conversationId));
    }

    [HttpPost("session/{sessionId}/cancel")]
    [ProducesResponseType(typeof(CancelTurnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CancelTurnResponse>> CancelTurn(
        [FromRoute] string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        var cancelled = await gateway.CancelTurnAsync(sessionId, ct).ConfigureAwait(false);
        return Ok(new CancelTurnResponse(sessionId, cancelled, cancelled ? "cancelled" : "idle"));
    }

    [HttpGet("session/{sessionId}/plan")]
    [ProducesResponseType(typeof(SessionPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SessionPlanDto>> GetPlan(
        [FromRoute] string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        var plan = await ResolveCurrentPlanDtoAsync(session, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return NotFound(new GatewayErrorResponse("plan_not_found", "No plan exists for this session."));
        }

        return Ok(plan);
    }

    [HttpGet("session/{sessionId}/files")]
    [ProducesResponseType(typeof(WorkspaceDirectoryListingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceDirectoryListingDto>> ListWorkspaceFiles(
        [FromRoute] string sessionId,
        [FromQuery] string? path = null,
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

    [HttpGet("session/{sessionId}/files/content")]
    [ProducesResponseType(typeof(WorkspaceFileContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceFileContentDto>> ReadWorkspaceFileContent(
        [FromRoute] string sessionId,
        [FromQuery] string? path = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
        }

        var content = await WorkspaceFileExplorerService
            .ReadFileContentAsync(session.WorkspacePath, path, ct: ct)
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

    [HttpGet("session/{sessionId}/changes")]
    [ProducesResponseType(typeof(WorkspaceChangesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceChangesDto>> GetWorkspaceChanges(
        [FromRoute] string sessionId,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
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

    [HttpGet("session/{sessionId}/snapshot")]
    [ProducesResponseType(typeof(SessionSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GatewayErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SessionSnapshotDto>> GetSnapshot(
        [FromRoute] string sessionId,
        [FromQuery] string? conversationId = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var session = await chatService.GetSession(sessionId).ConfigureAwait(false);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return NotFound(new GatewayErrorResponse("session_not_found", "Session not found."));
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
        if (!string.IsNullOrWhiteSpace(resolvedConvId))
        {
            var rawMessages = await chatService.GetConversationMessages(resolvedConvId).ConfigureAwait(false);
            foreach (var m in rawMessages)
            {
                if (!string.IsNullOrWhiteSpace(m.MessageType))
                {
                    var (bounded, truncated) = ToolEventSanitizer.TruncateText(m.AiContent, MaxSnapshotMessageContentChars);
                    messages.Add(new SnapshotMessageDto(
                        m.Id + "_n",
                        "system",
                        bounded,
                        null,
                        m.MessageType,
                        m.NotificationMeta,
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
                    var (boundedTool, toolTruncated) = ToolEventSanitizer.TruncateText(m.ToolCallResult, MaxSnapshotMessageContentChars);
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
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var results = doc.RootElement.TryGetProperty("results", out var resEl)
                    ? System.Text.Json.JsonSerializer.Deserialize<GatewayChannelToolResultSnapshot[]>(resEl.GetRawText(), options) ?? []
                    : [];
                var outputs = doc.RootElement.TryGetProperty("outputs", out var outEl)
                    ? System.Text.Json.JsonSerializer.Deserialize<GatewayChannelToolOutputSnapshot[]>(outEl.GetRawText(), options) ?? []
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

    private ActionResult MapFileExplorerError(WorkspaceFileErrorCode code, string? message)
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
            s.ActiveTaskId);

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
