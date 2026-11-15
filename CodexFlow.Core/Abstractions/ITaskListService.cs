// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// 任务清单服务：将 Session 的 Plan 快照写入 Redis 并触发 SignalR 推送。
/// </summary>
public interface ITaskListService
{
    /// <summary>
    /// 从 Session 的 Plan 生成快照并持久化到 Redis，返回序列化后的快照 JSON。
    /// </summary>
    Task<string> SaveSnapshotAsync(CodexSession session, CancellationToken ct = default);

    /// <summary>
    /// 获取指定 Session 的最新任务清单快照 JSON。
    /// </summary>
    Task<string?> GetSnapshotAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 更新单个任务的状态并重新保存快照，返回更新后的快照 JSON。
    /// </summary>
    Task<string> UpdateTaskStatusAsync(CodexSession session, string taskId, CodexTaskStatus status, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>
    /// 清空指定 Session 的任务清单快照。
    /// </summary>
    Task ClearSnapshotAsync(string sessionId, CancellationToken ct = default);
}
