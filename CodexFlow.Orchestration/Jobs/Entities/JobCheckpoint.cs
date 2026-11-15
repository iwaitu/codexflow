// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Jobs
{
    /// <summary>
    /// 作业检查点 — 记录长时任务的中间状态，支持断点续传
    /// </summary>
    public class JobCheckpoint
    {
        public JobCheckpoint()
        {
            CheckpointId = Ulid.NewUlid().ToString();
            CreatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>ULID 主键</summary>
        public string CheckpointId { get; set; }

        /// <summary>所属作业的 JobId</summary>
        public string JobId { get; set; } = string.Empty;

        public string? SessionId { get; set; }

        public string? TaskId { get; set; }

        /// <summary>检查点序号（单个 Job 内单调递增）</summary>
        public int SequenceNumber { get; set; }

        public string? CurrentStep { get; set; }

        public string? PendingActionType { get; set; }

        public string? PendingPayloadJson { get; set; }

        public string? ResumeToken { get; set; }

        public string? WorkspacePath { get; set; }

        public string? ActiveTaskId { get; set; }

        public int Attempt { get; set; }

        public long LastEventSeq { get; set; }

        /// <summary>JSON 序列化的检查点状态快照</summary>
        public string CheckpointData { get; set; } = "{}";

        public DateTime CreatedAtUtc { get; set; }

        // Navigation property
        public virtual BackgroundJob? Job { get; set; }
    }
}
