// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Gateway;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using System.Text.Json;

namespace CodexFlow.Gateway.Adapters;

/// <summary>
/// Runtime 以失败终态结束（Exception / RecoveryExhausted）时抛出，
/// 由 Gateway 现有失败路径转成带 messageId 的 error，而不是成功文本 + done。
/// </summary>
public sealed class GatewayRuntimeFailureException : Exception
{
    /// <summary>Creates a failure exception for a runtime termination.</summary>
    /// <param name="reason">Runtime termination reason.</param>
    /// <param name="detailCode">Optional terminal detail code.</param>
    /// <param name="message">Failure message.</param>
    public GatewayRuntimeFailureException(QueryTerminationReason reason, string? detailCode, string message)
        : base(message)
    {
        Reason = reason;
        DetailCode = detailCode;
    }

    /// <summary>Runtime termination reason.</summary>
    public QueryTerminationReason Reason { get; }

    /// <summary>Optional terminal detail code reported by the runtime.</summary>
    public string? DetailCode { get; }

    /// <summary>
    /// True when the termination means the turn errored or did not finish its work, so it must not be
    /// reported as success. Normal, NoToolCalls, AwaitingUserConfirmation, AutoDispatched and
    /// EmptyResponseFallback (handled by the empty-text path) are not failures.
    /// </summary>
    public static bool IsFailure(QueryTerminationReason reason)
        => reason is QueryTerminationReason.Exception
            or QueryTerminationReason.RecoveryExhausted
            or QueryTerminationReason.MaxRoundsReached
            or QueryTerminationReason.ContextHardLimit
            or QueryTerminationReason.StallDetected;
}

/// <summary>
/// Plan task dispatch could not start (invalid plan projection or missing session). Propagated through the
/// Gateway failure path so the client sees a correlated error instead of success text.
/// </summary>
public sealed class GatewayPlanDispatchFailedException : Exception
{
    /// <summary>Creates the exception for a failed dispatch.</summary>
    /// <param name="status">Structured dispatch status.</param>
    /// <param name="message">Dispatch failure message.</param>
    public GatewayPlanDispatchFailedException(CodexFlow.Services.Background.PlanTaskDispatchStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>Structured dispatch status.</summary>
    public CodexFlow.Services.Background.PlanTaskDispatchStatus Status { get; }
}

/// <summary>
/// Phase 3: Gateway Runtime 事件适配器 — 将 QueryRuntimeEvent 映射为 GatewaySseEvent
/// </summary>
/// <remarks>
/// Gateway 的 SSE 格式使用 GatewaySseEvent 包装，通过 emit 回调发送。
///
/// 事件类型映射：
/// - content, thinking_start, thinking_content, thinking_end
/// - tool_call, tool_result (通过 TaskStatus 或自定义事件)
/// - setconversationid, done
/// </remarks>
public sealed class GatewayRuntimeEventAdapter : IQueryRuntimeEventSink
{
    private static readonly Dictionary<string, string> WorkerNoticeTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["worker_started"] = GatewaySseEventType.WorkerStarted,
        ["worker_waiting_user"] = GatewaySseEventType.WorkerWaitingUser,
        ["worker_waiting"] = GatewaySseEventType.WorkerWaitingUser,
        ["worker_completed"] = GatewaySseEventType.WorkerCompleted,
        ["worker_failed"] = GatewaySseEventType.WorkerFailed
    };

    private readonly Func<GatewaySseEvent, ValueTask> _emit;
    private readonly HashSet<QueryRuntimeEventType> _enabledEvents;
    // 绑定的 GatewayMessage.MessageId；仅来自宿主，绝不取自 provider 文本。
    private readonly string? _messageId;
    private string? _conversationId;
    private long _seq;

    /// <summary>
    /// Initializes the gateway runtime event adapter.
    /// </summary>
    /// <param name="emit">Callback used to publish mapped gateway SSE events.</param>
    /// <param name="messageId">Host-bound request identifier for correlated error events.</param>
    public GatewayRuntimeEventAdapter(Func<GatewaySseEvent, ValueTask> emit, string? messageId = null)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _messageId = string.IsNullOrWhiteSpace(messageId) ? null : messageId;
        _seq = 0;
        // Gateway 启用的事件类型
        _enabledEvents = new HashSet<QueryRuntimeEventType>
        {
            QueryRuntimeEventType.AssistantDelta,
            QueryRuntimeEventType.ThinkingStarted,
            QueryRuntimeEventType.ThinkingDelta,
            QueryRuntimeEventType.ThinkingEnded,
            QueryRuntimeEventType.ToolCallRequested,
            QueryRuntimeEventType.ToolExecutionCompleted,
            QueryRuntimeEventType.ConversationIdSet,
            QueryRuntimeEventType.SystemNotice,
            QueryRuntimeEventType.Error
        };
    }

    /// <summary>
    /// 设置 Conversation ID
    /// </summary>
    public void SetConversationId(string conversationId)
    {
        _conversationId = conversationId;
    }

    /// <inheritdoc/>
    public bool IsEnabled(QueryRuntimeEventType eventType)
    {
        return _enabledEvents.Contains(eventType);
    }

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        var gatewayEvent = MapEventToGatewayEvent(runtimeEvent);
        if (gatewayEvent == null) return;

        await _emit(gatewayEvent);
    }

    /// <summary>
    /// 发送 Conversation ID 设置事件
    /// </summary>
    public async ValueTask SendConversationIdAsync()
    {
        if (string.IsNullOrEmpty(_conversationId)) return;

        await _emit(new GatewaySseEvent
        {
            Seq = NextSeq(),
            Type = GatewaySseEventType.SetConversationId,
            Payload = new { conversationId = _conversationId }
        });
    }

    /// <summary>
    /// 发送最终 Done 事件
    /// </summary>
    public async ValueTask SendDoneAsync()
    {
        await _emit(new GatewaySseEvent
        {
            Seq = NextSeq(),
            Type = GatewaySseEventType.Done,
            Payload = new { type = "done" }
        });
    }

    /// <summary>
    /// 发送文件树更新事件
    /// </summary>
    public async ValueTask SendFileTreeUpdatedAsync(string toolName)
    {
        await _emit(new GatewaySseEvent
        {
            Seq = NextSeq(),
            Type = GatewaySseEventType.FileTreeUpdated,
            Payload = new { tool = toolName }
        });
    }

    /// <summary>
    /// 发送 worker 生命周期事件
    /// </summary>
    public async ValueTask SendWorkerLifecycleEventAsync(string eventType, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        await _emit(new GatewaySseEvent
        {
            Seq = NextSeq(),
            Type = eventType,
            Payload = payload
        });
    }

    private long NextSeq()
    {
        return Interlocked.Increment(ref _seq);
    }

    private GatewaySseEvent? MapEventToGatewayEvent(QueryRuntimeEvent evt)
    {
        var (type, payload) = evt switch
        {
            // 内容事件
            AssistantDeltaEvent e => (
                GatewaySseEventType.Content,
                (object)new { type = "content", content = e.Delta, text = e.Delta }),

            // 思考事件
            ThinkingStartedEvent => (
                GatewaySseEventType.ThinkingStart,
                (object)new { type = "thinking_start" }),

            ThinkingDeltaEvent e => (
                GatewaySseEventType.ThinkingContent,
                (object)new { type = "thinking_content", content = e.Delta, text = e.Delta }),

            ThinkingEndedEvent e => (
                GatewaySseEventType.ThinkingEnd,
                (object)new { type = "thinking_end", thinking_content = e.FullThinking }),

            // 工具调用事件
            ToolCallRequestedEvent e => (
                GatewaySseEventType.ToolCall,
                (object)new { type = "tool_call", name = e.ToolName, callId = e.CallId }),

            ToolExecutionCompletedEvent e => (
                GatewaySseEventType.TaskStatus,
                (object)new { type = "tool_result", name = e.ToolName, success = e.Success, result = e.Result }),

            // Conversation ID
            ConversationIdSetEvent e => (
                GatewaySseEventType.SetConversationId,
                (object)new { conversationId = e.ConversationId }),

            // 系统消息 / worker 生命周期消息
            SystemNoticeEvent e when TryMapWorkerNoticeEvent(e, out var workerEventType, out var workerPayload) => (
                workerEventType,
                workerPayload),

            SystemNoticeEvent e => (
                GatewaySseEventType.SystemMessage,
                (object)new { type = "system_message", noticeType = e.NoticeType, content = e.Content, text = e.Content }),

            // 错误事件
            ErrorEvent e => (
                GatewaySseEventType.Error,
                (object)new { messageId = _messageId, error = e.ErrorType, message = e.Message }),

            // 以下事件不直接映射到 SSE
            // RoundStartedEvent, RoundCompletedEvent, TerminatedEvent
            // RecoveryTriggeredEvent, ToolExecutionStartedEvent
            _ => (null, null)
        };

        if (type == null) return null;

        return new GatewaySseEvent
        {
            Seq = NextSeq(),
            Type = type,
            Payload = payload!
        };
    }

    private static bool TryMapWorkerNoticeEvent(SystemNoticeEvent evt, out string type, out object payload)
    {
        type = string.Empty;
        payload = null!;

        if (!WorkerNoticeTypeMap.TryGetValue(NormalizeNoticeType(evt.NoticeType), out var mappedType))
        {
            return false;
        }

        type = mappedType;
        payload = BuildWorkerNoticePayload(mappedType, evt.Content);
        return true;
    }

    private static object BuildWorkerNoticePayload(string eventType, string content)
    {
        if (TryParseJsonObject(content, out var payload))
        {
            payload["type"] = eventType;

            if (!payload.ContainsKey("content") && !payload.ContainsKey("text"))
            {
                var summary = ReadString(payload, "summary")
                    ?? ReadString(payload, "message")
                    ?? ReadString(payload, "waitingReason")
                    ?? ReadString(payload, "result");

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    payload["content"] = summary;
                    payload["text"] = summary;
                }
            }

            return payload;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = eventType,
            ["content"] = content,
            ["text"] = content
        };
    }

    private static string NormalizeNoticeType(string? noticeType)
        => string.IsNullOrWhiteSpace(noticeType)
            ? string.Empty
            : noticeType.Trim().Replace('-', '_');

    private static string? ReadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            _ => value.ToString()
        };
    }

    private static bool TryParseJsonObject(string content, out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload = document.RootElement
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => ConvertJsonValue(property.Value),
                    StringComparer.Ordinal);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ConvertJsonValue(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }
}
