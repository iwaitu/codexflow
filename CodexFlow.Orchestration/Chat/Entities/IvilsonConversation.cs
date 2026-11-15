// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Chat
{
    public enum ConversationThreadType
    {
        Chat,     // 纯咨询
        Master,   // 主控（分析/规划）
        Worker    // 任务执行者
    }

    public class IvilsonConversation
    {
        public IvilsonConversation()
        {
            Created = DateTime.UtcNow;
            UpdateAt = DateTime.UtcNow;
            Id = Ulid.NewUlid().ToString();
            ChatMessages = new HashSet<IvilsonChatMessage>();
            ThreadType = ConversationThreadType.Chat;
        }
        public string Id { get; set; }
        public string? SessionId { get; set; }
        public virtual IvilsonSession? Session { get; set; }

        public string? TaskId { get; set; } // 关联的任务 ID
        public ConversationThreadType ThreadType { get; set; }
        public bool IsArchived { get; set; } = false;

        public required string Model { get; set; }
        public required string Name { get; set; }
        public required string UserId { get; set; }
        public DateTime Created { get; set; }
        public DateTime UpdateAt { get; set; }
        public virtual ICollection<IvilsonChatMessage> ChatMessages { get; set; }
        public bool IsIndexed { get; set; } = false;
    }
}
