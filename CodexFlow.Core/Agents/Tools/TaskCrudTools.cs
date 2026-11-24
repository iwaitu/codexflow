// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Planning.Patching;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexFlow.Core.Agents.Tools;

internal static class TaskCrudToolSupport
{
    public static async Task<CodexSession?> GetSessionAsync(
        CodexSessionManager sessionManager,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        _ = ct;
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
    }

    public static string Serialize<T>(T value)
        => PlanToolJson.Write(value, indented: true);

    public static bool TryParseStatus(object? value, out CodexTaskStatus status)
    {
        status = CodexTaskStatus.Pending;
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out status);
    }

    public static IReadOnlyList<string> ParseStringList(object? value)
    {
        if (value == null)
        {
            return [];
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseStringList(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
        {
            return jArray.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.TrimStart()[0] == '[')
        {
            try
            {
                return JsonSerializer.Deserialize(text, BuiltInToolJsonContext.Default.StringArray) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static IReadOnlyList<TaskChecklistItem> ParseChecklist(object? value)
    {
        if (value == null)
        {
            return [];
        }

        try
        {
            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    return PlanToolJson.Read<List<TaskChecklistItem>>(jsonElement.GetRawText()) ?? [];
                }

                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    return ParseChecklist(jsonElement.GetString());
                }
            }

            if (value is JArray jArray)
            {
                return PlanToolJson.Read<List<TaskChecklistItem>>(jArray.ToString(Newtonsoft.Json.Formatting.None)) ?? [];
            }

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            if (text.TrimStart()[0] == '[')
            {
                return PlanToolJson.Read<List<TaskChecklistItem>>(text) ?? [];
            }

            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((item, index) => new TaskChecklistItem
                {
                    Id = $"CHK_{index + 1:000}",
                    Text = item,
                    Status = TaskChecklistItemStatus.Pending
                })
                .ToArray();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return [];
        }
    }

    public static async Task<string?> SaveTaskListSnapshotAsync(
        ITaskListService? taskListService,
        CodexSession session,
        CancellationToken ct)
    {
        if (taskListService == null)
        {
            return null;
        }

        return await taskListService.SaveSnapshotAsync(session, ct).ConfigureAwait(false);
    }
}

public sealed class TaskCreateTool(
    CodexSessionManager sessionManager,
    ITaskListService? taskListService,
    ILogger<TaskCreateTool> logger) : ICodexTool
{
    public string Name => "task_create";
    public string Description => "向当前 session 的任务列表增量新增一个任务。参数: session_id, title, description?, task_type?, status?, stage_id?, dependencies?, inputs?, outputs?, checklist_items?。`create_session_plan` 用于一次性生成计划；task_create 用于计划审批后的增量维护。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 12_000);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var title = arguments.GetValueOrDefault("title")?.ToString();
        if (string.IsNullOrWhiteSpace(title))
        {
            return CodexToolResult.Error("Missing title.");
        }

        var task = new CodexTask
        {
            Id = arguments.GetValueOrDefault("task_id")?.ToString() ?? BuildTaskId(session),
            Title = title.Trim(),
            Description = arguments.GetValueOrDefault("description")?.ToString() ?? string.Empty,
            TaskType = arguments.GetValueOrDefault("task_type")?.ToString() ?? CodexTaskClassifier.CodeTaskType,
            StageId = TryParseInt(arguments.GetValueOrDefault("stage_id"), out var stageId) ? stageId : session.CurrentStage
        };

        if (TaskCrudToolSupport.TryParseStatus(arguments.GetValueOrDefault("status"), out var status))
        {
            task.Status = status;
        }

        task.ReplaceDependencies(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("dependencies")));
        task.ReplaceInputs(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("inputs")));
        task.ReplaceOutputs(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("outputs")));
        task.ReplaceChecklistItems(TaskCrudToolSupport.ParseChecklist(arguments.GetValueOrDefault("checklist_items")));
        CodexTaskProgressNormalizer.NormalizeTaskChecklist(task);

        if (session.Plan.Any(existing => string.Equals(existing.Id, task.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return CodexToolResult.Error($"Task already exists: {task.Id}");
        }

        session.Plan.Add(task);
        session.PlanVersion = Guid.NewGuid().ToString("N")[..8];
        session.PlanGeneratedAtUtc ??= DateTime.UtcNow;

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            var snapshot = await TaskCrudToolSupport.SaveTaskListSnapshotAsync(taskListService, session, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"TASK_CREATED\n{TaskCrudToolSupport.Serialize(task)}",
                ToolResultMetadata.FromJson(new JsonObject { ["task"] = ToolResultMetadata.ToNode(task, SourceGeneratedToolJsonContext.Default.CodexTask), ["snapshotJson"] = snapshot }),
                summary: $"task created: {task.Id}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "task_create failed for session {SessionId}", session.Id);
            return CodexToolResult.Error($"task_create failed: {ex.Message}");
        }
    }

    private static string BuildTaskId(CodexSession session)
        => $"TASK-{session.Plan.Count + 1:000}";

    private static bool TryParseInt(object? value, out int result)
        => int.TryParse(value?.ToString(), out result);
}

public sealed class TaskGetTool(CodexSessionManager sessionManager) : ICodexTool
{
    public string Name => "task_get";
    public string Description => "读取当前 session 中单个任务。参数: session_id, task_id。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return CodexToolResult.Error("Missing task_id.");
        }

        var task = session.Plan.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        return task == null
            ? CodexToolResult.Error($"Task not found: {taskId}")
            : CodexToolResult.Succeeded(TaskCrudToolSupport.Serialize(task), ToolResultMetadata.From(task, SourceGeneratedToolJsonContext.Default.CodexTask), summary: $"task read: {task.Id}");
    }
}

public sealed class TaskListTool(CodexSessionManager sessionManager, ITaskListService? taskListService) : ICodexTool
{
    public string Name => "task_list";
    public string Description => "列出当前 session 的任务列表，并在可用时附带 task list snapshot。参数: session_id。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        string? snapshot = null;
        if (taskListService != null && !string.IsNullOrWhiteSpace(session.Id))
        {
            snapshot = await taskListService.GetSnapshotAsync(session.Id, ct).ConfigureAwait(false);
        }

        var payload = new PlanTaskListPayload(session.Id, session.PlanVersion, session.PlanGeneratedAtUtc, session.Plan, snapshot);

        return CodexToolResult.Succeeded(
            TaskCrudToolSupport.Serialize(payload),
            ToolResultMetadata.From(payload, SourceGeneratedToolJsonContext.Default.PlanTaskListPayload),
            summary: $"tasks listed: {session.Plan.Count}");
    }
}

public sealed class TaskUpdateTool(
    CodexSessionManager sessionManager,
    ITaskListService? taskListService,
    ILogger<TaskUpdateTool> logger) : ICodexTool
{
    public string Name => "task_update";
    public string Description => "增量更新当前 session 的单个任务。参数: session_id, task_id, title?, description?, task_type?, status?, stage_id?, dependencies?, inputs?, outputs?, checklist_items?, result_notes?, error_message?。状态更新会同步 task list snapshot。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 12_000);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return CodexToolResult.Error("Missing task_id.");
        }

        var task = session.Plan.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return CodexToolResult.Error($"Task not found: {taskId}");
        }

        ApplyUpdates(task, arguments);
        session.PlanVersion = Guid.NewGuid().ToString("N")[..8];

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            var snapshot = await SaveSnapshotOrStatusAsync(session, task, arguments, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"TASK_UPDATED\n{TaskCrudToolSupport.Serialize(task)}",
                ToolResultMetadata.FromJson(new JsonObject { ["task"] = ToolResultMetadata.ToNode(task, SourceGeneratedToolJsonContext.Default.CodexTask), ["snapshotJson"] = snapshot }),
                summary: $"task updated: {task.Id}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "task_update failed for session {SessionId} task {TaskId}", session.Id, task.Id);
            return CodexToolResult.Error($"task_update failed: {ex.Message}");
        }
    }

    private static void ApplyUpdates(CodexTask task, Dictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title?.ToString()))
        {
            task.Title = title.ToString()!.Trim();
        }

        if (arguments.TryGetValue("description", out var description))
        {
            task.Description = description?.ToString() ?? string.Empty;
        }

        if (arguments.TryGetValue("task_type", out var taskType) && !string.IsNullOrWhiteSpace(taskType?.ToString()))
        {
            task.TaskType = taskType.ToString()!.Trim();
        }

        if (arguments.TryGetValue("status", out var statusRaw) &&
            TaskCrudToolSupport.TryParseStatus(statusRaw, out var status))
        {
            task.Status = status;
            if (status == CodexTaskStatus.Executing)
            {
                task.StartedAt ??= DateTime.UtcNow;
            }
            else if (status is CodexTaskStatus.Success or CodexTaskStatus.Failed or CodexTaskStatus.Skipped or CodexTaskStatus.CompletedWithWarnings)
            {
                task.FinishedAt ??= DateTime.UtcNow;
            }
        }

        if (arguments.TryGetValue("stage_id", out var stageIdRaw) &&
            int.TryParse(stageIdRaw?.ToString(), out var stageId))
        {
            task.StageId = stageId;
        }

        if (arguments.TryGetValue("dependencies", out var dependencies))
        {
            task.ReplaceDependencies(TaskCrudToolSupport.ParseStringList(dependencies));
        }

        if (arguments.TryGetValue("inputs", out var inputs))
        {
            task.ReplaceInputs(TaskCrudToolSupport.ParseStringList(inputs));
        }

        if (arguments.TryGetValue("outputs", out var outputs))
        {
            task.ReplaceOutputs(TaskCrudToolSupport.ParseStringList(outputs));
        }

        if (arguments.TryGetValue("checklist_items", out var checklistItems))
        {
            task.ReplaceChecklistItems(TaskCrudToolSupport.ParseChecklist(checklistItems));
        }

        if (arguments.TryGetValue("result_notes", out var resultNotes))
        {
            task.ResultNotes = resultNotes?.ToString();
        }
    }

    private async Task<string?> SaveSnapshotOrStatusAsync(
        CodexSession session,
        CodexTask task,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        if (taskListService == null)
        {
            return null;
        }

        if (arguments.ContainsKey("status"))
        {
            var errorMessage = arguments.GetValueOrDefault("error_message")?.ToString();
            return await taskListService.UpdateTaskStatusAsync(session, task.Id, task.Status, errorMessage, ct).ConfigureAwait(false);
        }

        return await taskListService.SaveSnapshotAsync(session, ct).ConfigureAwait(false);
    }
}

public sealed class PlanPatchResolveTool(
    CodexSessionManager sessionManager,
    IPlanPatchService planPatchService,
    IGitService gitService,
    ITaskListService? taskListService,
    ILogger<PlanPatchResolveTool> logger) : ICodexTool
{
    public string Name => "plan_patch_resolve";
    public string Description => "确认或拒绝待处理的局部 Plan Patch。参数: session_id, action(approve|reject), task_id?, notes?, approved_files?。approve 会将建议文件写入任务 outputs 并清理 pending；reject 只清理 pending 并保留失败状态。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 12_000);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var action = arguments.GetValueOrDefault("action")?.ToString();
        if (string.IsNullOrWhiteSpace(action))
        {
            return CodexToolResult.Error("Missing action. Use approve or reject.");
        }

        var approved = action.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("approved", StringComparison.OrdinalIgnoreCase);
        var rejected = action.Equals("reject", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("rejected", StringComparison.OrdinalIgnoreCase);
        if (!approved && !rejected)
        {
            return CodexToolResult.Error("Invalid action. Use approve or reject.");
        }

        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        var notes = arguments.GetValueOrDefault("notes")?.ToString();
        var approvedFiles = arguments.ContainsKey("approved_files")
            ? TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("approved_files"))
            : null;
        var cleanupWarnings = new List<string>();
        var patchTaskId = session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchTaskId);
        var patchArtifactPath = session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchArtifactPath);
        var patchWorkspacePath = session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchWorkspacePath);

        try
        {
            if (rejected)
            {
                cleanupWarnings.AddRange(await CleanupRejectedPatchChangesAsync(
                    session,
                    string.IsNullOrWhiteSpace(taskId) ? patchTaskId : taskId,
                    patchArtifactPath,
                    patchWorkspacePath,
                    ct).ConfigureAwait(false));
            }

            var result = await planPatchService.ResolvePendingPatchAsync(
                new PlanPatchResolutionRequest(session, taskId, approved, notes, approvedFiles),
                ct).ConfigureAwait(false);
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            var snapshot = await TaskCrudToolSupport.SaveTaskListSnapshotAsync(taskListService, session, ct).ConfigureAwait(false);

            return CodexToolResult.Succeeded(
                $"PLAN_PATCH_RESOLVED\n{TaskCrudToolSupport.Serialize(new PlanPatchResolutionPayload(result, cleanupWarnings, snapshot, session.Metadata))}",
                ToolResultMetadata.FromJson(new JsonObject { ["result"] = ToolResultMetadata.ToNode(result, SourceGeneratedToolJsonContext.Default.PlanPatchResult), ["cleanupWarnings"] = ToolResultMetadata.StringArray(cleanupWarnings), ["snapshotJson"] = snapshot }),
                summary: result.Summary);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "plan_patch_resolve failed for session {SessionId}", session.Id);
            return CodexToolResult.Error($"plan_patch_resolve failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> CleanupRejectedPatchChangesAsync(
        CodexSession session,
        string? taskId,
        string? artifactPath,
        string? preservedWorkspacePath,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(session.WorkspacePath))
        {
            warnings.Add("无法清理 rejected patch：session workspace path 为空。");
            return warnings;
        }

        if (!string.IsNullOrWhiteSpace(preservedWorkspacePath))
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                warnings.Add("无法清理 shadow patch：task id 为空。");
                return warnings;
            }

            var removed = await gitService.RemoveShadowWorktreeAsync(session.WorkspacePath, taskId).ConfigureAwait(false);
            if (!removed)
            {
                warnings.Add($"shadow worktree 清理失败，请手动检查：{preservedWorkspacePath}");
            }

            return warnings;
        }

        string? baseRef = null;
        try
        {
            baseRef = await ReadPatchBaseRefAsync(session.WorkspacePath, artifactPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            warnings.Add($"读取 patch artifact 失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(baseRef))
        {
            warnings.Add("无法清理 non-shadow patch：patch artifact 缺少 diff_base_ref。");
            return warnings;
        }

        var reset = await gitService.ResetToCommitAsync(session.WorkspacePath, baseRef).ConfigureAwait(false);
        if (!reset)
        {
            warnings.Add($"non-shadow patch reset 失败，请手动回滚到：{baseRef}");
        }

        return warnings;
    }

    private static async Task<string?> ReadPatchBaseRefAsync(string workspacePath, string? artifactPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return null;
        }

        var fullPath = Path.IsPathRooted(artifactPath)
            ? artifactPath
            : Path.Combine(workspacePath, artifactPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(fullPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("diff_base_ref", out var baseRef) &&
               baseRef.ValueKind == JsonValueKind.String
            ? baseRef.GetString()
            : null;
    }
}
