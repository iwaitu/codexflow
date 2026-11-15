// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Jobs
{
    /// <summary>
    /// 发件箱事件 — Transactional Outbox 模式，保障跨服务事件的 at-least-once 投递
    /// </summary>
    public class OutboxEvent
    {
        public OutboxEvent()
        {
            EventId = Ulid.NewUlid().ToString();
            OccurredAtUtc = DateTime.UtcNow;
        }

        /// <summary>ULID 主键</summary>
        public string EventId { get; set; }

        /// <summary>事件类型标识（e.g. "JobCompleted", "AgentToolCalled"）</summary>
        public string EventType { get; set; } = string.Empty;

        public string? JobId { get; set; }

        public string? SessionId { get; set; }

        public string? TaskId { get; set; }

        public long Seq { get; set; }

        /// <summary>关联的聚合根 ID（SessionId / JobId 等）</summary>
        [Obsolete("Use JobId or SessionId explicitly")]
        public string? AggregateId { get; set; }

        /// <summary>JSON 序列化的事件内容</summary>
        public string Payload { get; set; } = "{}";

        public string? CorrelationId { get; set; }

        public string? DedupeKey { get; set; }

        /// <summary>事件业务发生时间（UTC）</summary>
        public DateTime OccurredAtUtc { get; set; }

        /// <summary>事件投递完成时间（UTC），null 表示尚未处理</summary>
        public DateTime? ProcessedAt { get; set; }

        /// <summary>投递重试次数</summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>投递错误信息（最后一次）</summary>
        public string? LastError { get; set; }
    }
}
