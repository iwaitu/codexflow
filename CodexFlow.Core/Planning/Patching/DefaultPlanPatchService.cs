// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Planning.Patching;

public sealed class DefaultPlanPatchService : IPlanPatchService
{
    public Task<PlanPatchResult> CreatePendingPatchAsync(
        PlanPatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var session = request.Session;
        var revision = 1;
        if (session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanPatchRevision, out var rawRevision) &&
            int.TryParse(rawRevision, out var parsedRevision))
        {
            revision = parsedRevision + 1;
        }

        var requiresConfirmation = true;

        session.Metadata[PlanGuardMetadataKeys.PlanPatchPending] = bool.TrueString;
        session.Metadata[PlanGuardMetadataKeys.PlanPatchRevision] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        session.Metadata[PlanGuardMetadataKeys.PlanPatchReason] = request.Deviation.Summary;
        session.Metadata[PlanGuardMetadataKeys.PlanPatchTaskId] = request.FailedTask.Id;
        session.Metadata[PlanGuardMetadataKeys.PlanPatchCreatedAtUtc] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(request.DiffArtifactPath))
        {
            session.Metadata[PlanGuardMetadataKeys.PlanPatchArtifactPath] = request.DiffArtifactPath;
        }

        if (request.Deviation.SuggestedScopeFiles.Count > 0)
        {
            session.Metadata[PlanGuardMetadataKeys.PlanPatchSuggestedFiles] =
                string.Join(";", request.Deviation.SuggestedScopeFiles.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        var warnings = new List<string>();
        var downstreamBlocked = session.Plan
            .Where(task => task.Dependencies.Any(dep => string.Equals(dep, request.FailedTask.Id, StringComparison.OrdinalIgnoreCase)))
            .Where(task => task.UnsafeIfDependencyFallbackPassed)
            .Select(task => task.Id)
            .ToList();

        if (downstreamBlocked.Count > 0)
        {
            warnings.Add("下游任务保持 UnsafeIfDependencyFallbackPassed 门禁，需先让上游任务真实通过或由用户显式确认风险。受影响任务：" +
                string.Join(", ", downstreamBlocked));
        }

        return Task.FromResult(new PlanPatchResult(
            Applied: false,
            RequiresUserConfirmation: requiresConfirmation,
            PatchedTasks: Array.Empty<CodexTask>(),
            Summary: request.Deviation.Summary,
            Warnings: warnings));
    }

    public Task<PlanPatchResult> ResolvePendingPatchAsync(
        PlanPatchResolutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var session = request.Session;
        if (!session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanPatchPending, out var pendingRaw) ||
            !bool.TryParse(pendingRaw, out var pending) ||
            !pending)
        {
            return Task.FromResult(new PlanPatchResult(
                Applied: false,
                RequiresUserConfirmation: false,
                PatchedTasks: Array.Empty<CodexTask>(),
                Summary: "没有待处理的 Plan Patch。",
                Warnings: Array.Empty<string>()));
        }

        var metadataTaskId = session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchTaskId);
        var taskId = string.IsNullOrWhiteSpace(request.TaskId) ? metadataTaskId : request.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new PlanPatchResult(
                Applied: false,
                RequiresUserConfirmation: true,
                PatchedTasks: Array.Empty<CodexTask>(),
                Summary: "Plan Patch 缺少 task id，无法安全应用。",
                Warnings: ["请重新生成计划或手动清理异常 patch metadata。"]));
        }

        if (!string.IsNullOrWhiteSpace(metadataTaskId) &&
            !string.Equals(taskId, metadataTaskId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new PlanPatchResult(
                Applied: false,
                RequiresUserConfirmation: true,
                PatchedTasks: Array.Empty<CodexTask>(),
                Summary: $"Plan Patch 属于任务 {metadataTaskId}，不能应用到 {taskId}。",
                Warnings: Array.Empty<string>()));
        }

        var task = session.Plan.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return Task.FromResult(new PlanPatchResult(
                Applied: false,
                RequiresUserConfirmation: true,
                PatchedTasks: Array.Empty<CodexTask>(),
                Summary: $"任务不存在，无法应用 Plan Patch：{taskId}",
                Warnings: Array.Empty<string>()));
        }

        var warnings = new List<string>();
        if (request.Approved)
        {
            var preservedWorkspacePath = session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchWorkspacePath);
            var approvedFiles = request.ApprovedFiles ??
                ParseSuggestedFiles(session.Metadata.GetValueOrDefault(PlanGuardMetadataKeys.PlanPatchSuggestedFiles));

            if (approvedFiles.Count == 0)
            {
                warnings.Add("Plan Patch 未携带建议扩展文件；已清理 pending，但任务范围未自动扩展。");
            }
            else
            {
                var outputs = task.Outputs
                    .Concat(approvedFiles)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                task.ReplaceOutputs(outputs);
            }

            task.Status = CodexTaskStatus.Pending;
            task.FinishedAt = null;
            task.ResultNotes = string.IsNullOrWhiteSpace(request.Notes)
                ? "[PLAN_PATCH_APPROVED] 已确认局部扩展任务范围。"
                : $"[PLAN_PATCH_APPROVED] {request.Notes}";

            if (!string.IsNullOrWhiteSpace(preservedWorkspacePath))
            {
                session.Metadata[PlanGuardMetadataKeys.PlanPatchApprovedWorkspacePath] = preservedWorkspacePath;
                session.Metadata[PlanGuardMetadataKeys.PlanPatchApprovedTaskId] = task.Id;
            }
        }
        else
        {
            task.Status = CodexTaskStatus.Failed;
            task.FinishedAt ??= DateTime.UtcNow;
            task.ResultNotes = string.IsNullOrWhiteSpace(request.Notes)
                ? "[PLAN_PATCH_REJECTED] 已拒绝局部计划修补。"
                : $"[PLAN_PATCH_REJECTED] {request.Notes}";
        }

        foreach (var key in PlanGuardMetadataKeys.PlanPatchKeys)
        {
            session.Metadata.Remove(key);
        }

        session.PlanVersion = Guid.NewGuid().ToString("N")[..8];
        return Task.FromResult(new PlanPatchResult(
            Applied: request.Approved,
            RequiresUserConfirmation: false,
            PatchedTasks: [task],
            Summary: request.Approved
                ? $"Plan Patch 已确认并应用到任务 {task.Id}。"
                : $"Plan Patch 已拒绝并从任务 {task.Id} 清理。",
            Warnings: warnings));
    }

    private static string[] ParseSuggestedFiles(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
