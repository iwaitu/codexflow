// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Domain.Chat
{
    public class IvilsonChatMessage
    {
        public IvilsonChatMessage()
        {
            Created = DateTime.UtcNow;
            Id = Ulid.NewUlid().ToString();
        }
        public string Id { get; set; }
        public string? SystemPrompt { get; set; }
        public string ConversationId { get; set; } = string.Empty;
        public string? UserContent { get; set; }
        public string? AiContent { get; set; }
        public string? ThinkingContent { get; set; } // ✅ 思维链内容（结构化存储）
        public string? ReplyId { get; set; }
        public DateTime Created { get; set; }
        public int TokenUsed { get; set; }
        public string? ToolCallResult { get; set; }

        /// <summary>消息类型：null=普通, "notification"=系统通知</summary>
        public string? MessageType { get; set; }

        /// <summary>通知元数据 JSON（event_type, suppression, job_id 等）</summary>
        public string? NotificationMeta { get; set; }
    }
}
