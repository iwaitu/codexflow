// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts
{
    /// <summary>
    /// 通知中心数据传输对象
    /// </summary>
    public class NotificationDto
    {
        public required string JobId { get; set; }
        public string? TaskId { get; set; }
        public string? SessionId { get; set; }
        public string JobType { get; set; } = string.Empty;
        public string? JobStatus { get; set; }
        public string? StateKind { get; set; }
        public string? NotificationStatus { get; set; }
        public string? ShortSummary { get; set; }
        public bool HasMainSessionSummary { get; set; }
        public string? SummaryConversationId { get; set; }
        public string? SummaryMessageId { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public bool IsRead { get; set; }
    }
}
