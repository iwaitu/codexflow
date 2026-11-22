// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Core.Agents;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// 默认上下文窗口治理实现。
/// 当前仅负责把 query turn 回写到 SessionManager，由它执行自动压缩阈值判断。
/// </summary>
public sealed class DefaultContextWindowManager : IContextWindowManager
{
    private readonly CodexSessionManager _sessionManager;
    private readonly ILogger<DefaultContextWindowManager> _logger;

    public DefaultContextWindowManager(
        CodexSessionManager sessionManager,
        ILogger<DefaultContextWindowManager> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capture = request.ConversationCapture;
        if (capture == null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        var inputTurns = capture.InputTurns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Content))
            .Select(turn => (turn.Role, turn.Content))
            .ToArray();

        if (inputTurns.Length == 0)
        {
            return;
        }

        await _sessionManager.RecordMessagesAsync(request.SessionId, inputTurns).ConfigureAwait(false);
        await _sessionManager
            .ApplyContextGovernanceAsync(
                request.SessionId,
                request.AdapterHints?.ContextWarnLimit,
                request.AdapterHints?.ContextHardLimit,
                ct)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Context window preflight persisted by runtime for session {SessionId}. InputTurns={InputTurns}",
            request.SessionId,
            inputTurns.Length);
    }

    /// <inheritdoc />
    public async Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var capture = request.ConversationCapture;
        if (capture == null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.FinalText))
        {
            await _sessionManager.RecordMessagesAsync(
                request.SessionId,
                (capture.AssistantRole, result.FinalText)).ConfigureAwait(false);
        }

        var lastContextTokens = ResolveLastContextTokens(result);
        if ((result.TotalPromptTokens ?? 0) > 0 ||
            (result.TotalCompletionTokens ?? 0) > 0 ||
            lastContextTokens is > 0)
        {
            await _sessionManager
                .RecordRuntimeUsageAsync(
                    request.SessionId,
                    result.TotalPromptTokens ?? 0,
                    result.TotalCompletionTokens ?? 0,
                    result.TotalCachedPromptTokens ?? 0,
                    lastContextTokens,
                    request.ConversationCapture?.ConversationId,
                    ct)
                .ConfigureAwait(false);
        }

        if (result.RuntimeCheckpoint != null)
        {
            await _sessionManager
                .RecordRuntimeCheckpointAsync(
                    request.SessionId,
                    result.RuntimeCheckpoint,
                    ct)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Context window completion persisted by runtime for session {SessionId}. Termination={Termination}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, LastContextTokens={LastContextTokens}, Checkpoint={Checkpoint}",
            request.SessionId,
            result.TerminationReason,
            result.TotalPromptTokens ?? 0,
            result.TotalCompletionTokens ?? 0,
            lastContextTokens ?? 0,
            result.RuntimeCheckpoint != null);
    }

    private static int? ResolveLastContextTokens(QueryRuntimeResult result)
    {
        var lastPrompt = result.LastStepPromptTokens ?? 0;
        var lastCompletion = result.LastStepCompletionTokens ?? 0;
        if (lastPrompt > 0 || lastCompletion > 0)
        {
            return checked(lastPrompt + lastCompletion);
        }

        if (result.TotalRounds <= 1)
        {
            var totalPrompt = result.TotalPromptTokens ?? 0;
            var totalCompletion = result.TotalCompletionTokens ?? 0;
            if (totalPrompt > 0 || totalCompletion > 0)
            {
                return checked(totalPrompt + totalCompletion);
            }
        }

        var snapshotPrompt = result.LastPromptAssemblySnapshot?.EstimatedPromptTokens ?? 0;
        if (snapshotPrompt > 0)
        {
            var replyBytes = string.IsNullOrEmpty(result.FinalText)
                ? 0
                : Encoding.UTF8.GetByteCount(result.FinalText);
            var replyEstimatedTokens = replyBytes > 0 ? (int)Math.Ceiling(replyBytes / 4.0d) : 0;
            return checked(snapshotPrompt + replyEstimatedTokens);
        }

        return null;
    }
}
