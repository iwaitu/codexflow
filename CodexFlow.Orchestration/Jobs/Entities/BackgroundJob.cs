// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Jobs
{
    public enum BackgroundJobStatus
    {
        Queued = 0,
        Running = 1,
        WaitingUser = 2,
        Completed = 3,
        Failed = 4,
        Cancelled = 5,
        FailedRecoveryNeeded = 6
    }

    /// <summary>
    /// 后台异步作业实体 — 代表一个可检查点恢复的长时任务
    /// </summary>
    public class BackgroundJob
    {
        public BackgroundJob()
        {
            JobId = Ulid.NewUlid().ToString();
            CreatedAtUtc = DateTime.UtcNow;
            UpdatedAtUtc = DateTime.UtcNow;
            Status = BackgroundJobStatus.Queued;
        }

        /// <summary>ULID 主键</summary>
        public string JobId { get; set; }

        public string? SessionId { get; set; }

        public string? TaskId { get; set; }

        /// <summary>作业类型（e.g. "ExecuteCodeTask"）</summary>
        public string JobType { get; set; } = string.Empty;

        public BackgroundJobStatus Status { get; set; }

        /// <summary>JSON 序列化的输入参数</summary>
        public string PayloadJson { get; set; } = "{}";

        /// <summary>JSON 序列化的执行结果（可选）</summary>
        public string? ResultPayloadJson { get; set; }

        /// <summary>失败时的错误信息</summary>
        public string? ErrorMessage { get; set; }

        public string? RunnerId { get; set; }

        public string? ClaimToken { get; set; }

        public DateTime? LeaseExpiresAtUtc { get; set; }

        public DateTime? HeartbeatAtUtc { get; set; }

        public DateTime? DequeuedAtUtc { get; set; }

        public int Attempt { get; set; }

        public long LastEventSeq { get; set; }

        public string? WaitingReason { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? FinishedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public DateTime? CancelledAtUtc { get; set; }

        /// <summary>是否已发送完成通知（心跳服务使用，兼容字段）</summary>
        public bool Notified { get; set; }

        public DateTime? NotifiedAtUtc { get; set; }

        // ── 通知状态机字段 ──

        /// <summary>通知派发状态机</summary>
        public NotificationStatus? NotificationStatus { get; set; }

        /// <summary>总派发尝试次数</summary>
        public int NotificationAttemptCount { get; set; }

        /// <summary>最近一次尝试时间</summary>
        public DateTime? NotificationLastAttemptAtUtc { get; set; }

        /// <summary>最近失败错误</summary>
        public string? NotificationLastError { get; set; }

        /// <summary>当前派发实例标识</summary>
        public string? NotificationLeaseOwner { get; set; }

        /// <summary>当前通知租约超时时间</summary>
        public DateTime? NotificationLeaseExpiresAtUtc { get; set; }

        /// <summary>被抑制原因</summary>
        public string? NotificationSuppressedReason { get; set; }

        /// <summary>目标渠道快照，例如 signalr_sync,main_session</summary>
        public string? NotificationChannels { get; set; }

        /// <summary>摘要模式：template / llm</summary>
        public string? SummaryMode { get; set; }

        /// <summary>主会话注入后的消息 ID</summary>
        public string? SummaryMessageId { get; set; }

        /// <summary>被注入的主会话 ID</summary>
        public string? SummaryConversationId { get; set; }

        /// <summary>摘要输入 Token 用量</summary>
        public int? SummaryTokenUsageInput { get; set; }

        /// <summary>摘要输出 Token 用量</summary>
        public int? SummaryTokenUsageOutput { get; set; }

        /// <summary>摘要生成时间</summary>
        public DateTime? SummaryGeneratedAtUtc { get; set; }

        /// <summary>摘要短文本（通知中心卡片摘要，最多 500 字符）</summary>
        public string? ShortSummary { get; set; }

        // ── 通知已读追踪（Phase 4）──

        /// <summary>用户是否已阅读该通知</summary>
        public bool NotificationRead { get; set; }

        /// <summary>用户阅读通知的时间</summary>
        public DateTime? NotificationReadAtUtc { get; set; }

        // Navigation property
        public virtual ICollection<JobCheckpoint> Checkpoints { get; set; } = new List<JobCheckpoint>();
    }
}
