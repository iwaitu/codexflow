// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CodexFlow.Domain.Chat
{
    public class IvilsonSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string WorkspacePath { get; set; } = string.Empty;
        public string? ProjectUrl { get; set; } // [NEW] 关联的远程 Git 仓库地址
        public int CurrentStage { get; set; } = 0;
        public string FactsJson { get; set; } = "[]";
        public string? GlobalPlanJson { get; set; } // 全局任务规划 (Stage 2 产物)
        public string? CurrentPlanArtifactId { get; set; }
        public string? ActiveTaskId { get; set; } // 当前物理工作区正在执行的任务 ID
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string? HistorySummary { get; set; }
        public DateTime? LastCompressedAt { get; set; }

        // Navigation property
        public virtual ICollection<IvilsonConversation> Conversations { get; set; } = new List<IvilsonConversation>();
    }
}
