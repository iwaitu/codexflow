// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Gateway;

/// <summary>
/// Gateway 向前端 SSE 推送的事件类型
/// </summary>
public static class GatewaySseEventType
{
    // --- AI 流式内容（复用现有 SSE 协议）---
    public const string Content = "content";
    public const string ThinkingStart = "thinking_start";
    public const string ThinkingContent = "thinking_content";
    public const string ThinkingEnd = "thinking_end";
    public const string ToolCall = "tool_call";
    public const string ToolStarted = "tool_started";

    // --- Gateway 特有事件 ---
    public const string SetConversationId = "setconversationid";
    public const string UpdateConversationTitle = "updateconversationtitle";
    public const string TaskStatus = "task_status";
    public const string SystemMessage = "system_message";
    public const string NextTaskStarted = "next_task_started";
    public const string RetryNotice = "retry_notice";
    public const string PlanChangeRequest = "plan_change_request";
    public const string PlanApprovalRequested = "plan_approval_requested";
    public const string PlanningSummary = "planning_summary";
    public const string WorkerStarted = "worker_started";
    public const string WorkerWaitingUser = "worker_waiting_user";
    public const string WorkerCompleted = "worker_completed";
    public const string WorkerFailed = "worker_failed";
    public const string FileTreeUpdated = "file_tree_updated";
    public const string ToolApprovalRequest = "tool_approval_request";
    public const string ToolApprovalResolved = "tool_approval_resolved";
    public const string ToolOutput = "tool_output";
    public const string Error = "error";
    public const string Heartbeat = "heartbeat";
    public const string Done = "done";

    /// <summary>
    /// Events the client asked to replay are no longer available. Carries no <c>id:</c> line (Seq 0), so it
    /// never moves the client's Last-Event-ID. The marker only reports the loss; recovering the missing
    /// range means re-reading persisted session history.
    /// </summary>
    public const string ReplayGap = "replay_gap";
}

/// <summary>Why a <see cref="GatewaySseEventType.ReplayGap"/> marker was emitted.</summary>
public static class GatewayReplayGapReason
{
    /// <summary>The session's replay buffer evicted events the reconnecting client had not seen.</summary>
    public const string BufferEvicted = "buffer_evicted";

    /// <summary>This subscriber read too slowly and its own pending queue dropped its oldest events.</summary>
    public const string SubscriberOverflow = "subscriber_overflow";

    /// <summary>
    /// The channel the client was following no longer exists (gateway restart or channel recreation), so its Last-Event-ID belongs to
    /// a different stream. Sequence numbers started over: everything still buffered is replayed from the start and the client must
    /// reset its own sequence. The lost range cannot be stated; recovering it means re-reading persisted history.
    /// </summary>
    public const string SequenceReset = "sequence_reset";
}

/// <summary>
/// SSE 推送的单个事件
/// </summary>
public sealed record GatewaySseEvent
{
    /// <summary>SSE event id (用于 Last-Event-ID 断线重连)</summary>
    public long Seq { get; init; }

    /// <summary>事件类型，见 <see cref="GatewaySseEventType"/></summary>
    public required string Type { get; init; }

    /// <summary>事件负载（JSON 序列化后写入 SSE data 字段）</summary>
    public required object Payload { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
