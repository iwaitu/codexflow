// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts;

/// <summary>
/// 任务清单快照 DTO，通过 SignalR 推送给客户端实时展示进度。
/// </summary>
public class TaskListDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<TaskItemDto> Items { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TaskItemDto
{
    public string Id { get; set; } = string.Empty;
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum TaskItemStatus
{
    Pending,
    InProgress,
    Completed,
    CompletedWithWarnings,
    Failed,
    Skipped
}
