// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace CodexFlow.Infrastructure.Services;

/// <summary>
/// 基于 Redis 的任务清单服务：将 Session Plan 快照写入 Redis Hash，
/// 每次状态变更时更新并返回完整快照 JSON 供 SignalR 推送。
/// </summary>
public class RedisTaskListService : ITaskListService
{
    private readonly IKeyValueTextStore _store;
    private readonly ILogger<RedisTaskListService> _logger;
    private const string KeyPrefix = "Codex:TaskList:";
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Uses any <see cref="IKeyValueTextStore"/> (for example the SQLite one of the personal edition).</summary>
    public RedisTaskListService(IKeyValueTextStore store, ILogger<RedisTaskListService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<string> SaveSnapshotAsync(CodexSession session, CancellationToken ct = default)
    {
        var snapshot = BuildSnapshot(session);
        LogSnapshotSummary("SaveSnapshot", session.Id, snapshot);
        return await PersistSnapshotAsync(session.Id, snapshot).ConfigureAwait(false);
    }

    public async Task<string?> GetSnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await _store.GetAsync(KeyPrefix + sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read task list snapshot from Redis. SessionId={SessionId}", sessionId);
            return null;
        }
    }

    public async Task<string> UpdateTaskStatusAsync(CodexSession session, string taskId, CodexTaskStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        // 先更新 session 中的任务状态（调用方通常已更新，此处做保险同步）
        var task = session.Plan?.FirstOrDefault(t => t.Id == taskId);
        if (task != null)
        {
            task.Status = status;
            if (status == CodexTaskStatus.Executing && task.StartedAt == null)
                task.StartedAt = DateTime.UtcNow;
            if (status is CodexTaskStatus.Success or CodexTaskStatus.Failed or CodexTaskStatus.Skipped or CodexTaskStatus.CompletedWithWarnings)
                task.FinishedAt ??= DateTime.UtcNow;
        }

        var existingSnapshot = await TryGetExistingSnapshotModelAsync(session.Id).ConfigureAwait(false);
        TaskListSnapshot? snapshot = null;

        if (existingSnapshot != null && existingSnapshot.Items.Count > 0)
        {
            snapshot = BuildSnapshot(session, existingSnapshot);
        }
        else if (session.Plan == null || session.Plan.Count == 0)
        {
            _logger.LogWarning(
                "TaskList update could not use snapshot merge because both session plan and existing snapshot were empty. SessionId={SessionId}, TaskId={TaskId}, Status={Status}",
                session.Id,
                taskId,
                status);
        }

        snapshot ??= BuildSnapshot(session);

        var snapshotItem = snapshot.Items.FirstOrDefault(i => string.Equals(i.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (snapshotItem == null && existingSnapshot != null && existingSnapshot.Items.Count > 0)
        {
            _logger.LogWarning(
                "TaskList update could not find task in merged snapshot. SessionId={SessionId}, TaskId={TaskId}, Status={Status}",
                session.Id,
                taskId,
                status);
        }
        else if (snapshotItem != null)
        {
            ApplyStatus(snapshotItem, status, errorMessage);
        }

        _logger.LogInformation(
            "TaskList status update requested. SessionId={SessionId}, TaskId={TaskId}, Status={Status}, ErrorMessagePresent={HasErrorMessage}",
            session.Id,
            taskId,
            status,
            !string.IsNullOrWhiteSpace(errorMessage));

        LogSnapshotSummary(existingSnapshot == null ? "UpdateTaskStatus" : "UpdateTaskStatusMergedExistingSnapshot", session.Id, snapshot);
        return await PersistSnapshotAsync(session.Id, snapshot).ConfigureAwait(false);
    }

    public async Task ClearSnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _store.DeleteAsync(KeyPrefix + sessionId, ct);
            _logger.LogDebug("TaskList snapshot cleared for session {SessionId}", sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear task list snapshot from Redis. SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 从 CodexSession.Plan 构建匹配 TaskListDto 结构的匿名对象。
    /// 字段名与 CodexFlow.Contracts.TaskListDto 保持一致（camelCase 序列化）。
    /// </summary>
    private static TaskListSnapshot BuildSnapshot(CodexSession session, TaskListSnapshot? existingSnapshot = null)
    {
        if ((session.Plan == null || session.Plan.Count == 0) && existingSnapshot != null)
        {
            return CloneSnapshot(existingSnapshot, session.Id, BuildTitle(session, existingSnapshot));
        }

        var existingItems = existingSnapshot?.Items
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var items = (session.Plan ?? [])
            .Select((t, idx) =>
            {
                TaskListSnapshotItem? existingItem = null;
                if (existingItems != null)
                {
                    existingItems.TryGetValue(t.Id, out existingItem);
                }
                return new TaskListSnapshotItem
                {
                    Id = t.Id,
                    Index = idx + 1,
                    Title = t.Title,
                    Description = t.Description,
                    Status = existingItem?.Status ?? MapStatus(t.Status),
                    StartedAt = existingItem?.StartedAt ?? t.StartedAt,
                    FinishedAt = existingItem?.FinishedAt ?? t.FinishedAt,
                    ErrorMessage = existingItem?.ErrorMessage ?? (t.Status == CodexTaskStatus.Failed ? t.ResultNotes : null)
                };
            })
            .ToList();

        if (existingSnapshot != null)
        {
            var plannedIds = new HashSet<string>(items.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var existingItem in existingSnapshot.Items)
            {
                if (plannedIds.Contains(existingItem.Id))
                {
                    continue;
                }

                items.Add(CloneItem(existingItem, items.Count + 1));
            }
        }

        return new TaskListSnapshot
        {
            SessionId = session.Id,
            Title = BuildTitle(session, existingSnapshot),
            Items = items,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static string MapStatus(CodexTaskStatus status) => status switch
    {
        CodexTaskStatus.Pending => "pending",
        CodexTaskStatus.Planning => "pending",
        CodexTaskStatus.Executing => "running",
        CodexTaskStatus.Success => "completed",
        CodexTaskStatus.CompletedWithWarnings => "completedWithWarnings",
        CodexTaskStatus.Failed => "failed",
        CodexTaskStatus.Skipped => "skipped",
        _ => "pending"
    };

    private async Task<TaskListSnapshot?> TryGetExistingSnapshotModelAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var existingJson = await GetSnapshotAsync(sessionId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return null;
        }

        TaskListSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<TaskListSnapshot>(existingJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize existing task list snapshot. SessionId={SessionId}", sessionId);
            return null;
        }

        return snapshot;
    }

    private static string BuildTitle(CodexSession session, TaskListSnapshot? existingSnapshot = null)
    {
        if (!string.IsNullOrWhiteSpace(session.ProjectSummary))
        {
            return session.ProjectSummary.Length > 60
                ? session.ProjectSummary[..60] + "..."
                : session.ProjectSummary;
        }

        return string.IsNullOrWhiteSpace(existingSnapshot?.Title)
            ? "任务计划"
            : existingSnapshot.Title;
    }

    private static TaskListSnapshot CloneSnapshot(TaskListSnapshot snapshot, string? sessionId, string title)
    {
        return new TaskListSnapshot
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? snapshot.SessionId : sessionId,
            Title = title,
            UpdatedAt = DateTime.UtcNow,
            Items = snapshot.Items
                .OrderBy(item => item.Index)
                .Select((item, index) => CloneItem(item, index + 1))
                .ToList()
        };
    }

    private static TaskListSnapshotItem CloneItem(TaskListSnapshotItem item, int index)
    {
        return new TaskListSnapshotItem
        {
            Id = item.Id,
            Index = index,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            StartedAt = item.StartedAt,
            FinishedAt = item.FinishedAt,
            ErrorMessage = item.ErrorMessage
        };
    }

    private static void ApplyStatus(TaskListSnapshotItem item, CodexTaskStatus status, string? errorMessage)
    {
        item.Status = MapStatus(status);
        if (status == CodexTaskStatus.Executing && item.StartedAt == null)
        {
            item.StartedAt = DateTime.UtcNow;
        }

        if (status is CodexTaskStatus.Success or CodexTaskStatus.Failed or CodexTaskStatus.Skipped or CodexTaskStatus.CompletedWithWarnings)
        {
            item.FinishedAt ??= DateTime.UtcNow;
        }

        item.ErrorMessage = status == CodexTaskStatus.Failed ? errorMessage : null;
    }

    private async Task<string> PersistSnapshotAsync(string? sessionId, TaskListSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);

        try
        {
            await _store.SetAsync(KeyPrefix + sessionId, json, SnapshotTtl).ConfigureAwait(false);
            _logger.LogInformation(
                "TaskList snapshot persisted. SessionId={SessionId}, Count={Count}, Statuses=[{Statuses}], Items=[{Items}]",
                sessionId,
                snapshot.Items.Count,
                SummarizeStatuses(snapshot),
                SummarizeItems(snapshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist task list snapshot to Redis. SessionId={SessionId}", sessionId);
        }

        return json;
    }

    private void LogSnapshotSummary(string operation, string? sessionId, TaskListSnapshot snapshot)
    {
        _logger.LogInformation(
            "TaskList snapshot prepared. Operation={Operation}, SessionId={SessionId}, Count={Count}, Statuses=[{Statuses}], Items=[{Items}]",
            operation,
            sessionId,
            snapshot.Items.Count,
            SummarizeStatuses(snapshot),
            SummarizeItems(snapshot));
    }

    private static string SummarizeStatuses(TaskListSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
            return "empty";

        return string.Join(
            ", ",
            snapshot.Items
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Status) ? "<blank>" : item.Status.Trim())
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}:{group.Count()}"));
    }

    private static string SummarizeItems(TaskListSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
            return "<none>";

        return string.Join(
            ", ",
            snapshot.Items
                .OrderBy(item => item.Index)
                .Take(5)
                .Select(item => $"{item.Index}:{item.Id}[{item.Status}]"));
    }

    // 内部快照结构（与 Contracts.TaskListDto 字段一致，camelCase 序列化）
    private sealed class TaskListSnapshot
    {
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "";
        public List<TaskListSnapshotItem> Items { get; set; } = [];
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class TaskListSnapshotItem
    {
        public string Id { get; set; } = "";
        public int Index { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "Pending";
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
