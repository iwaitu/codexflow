// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using CodexFlow.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CodexFlow.Orchestration.Persistence;

namespace CodexFlow.Services.Background;

/// <summary>
/// Dispatches approved plan tasks through the background worker queue.
/// </summary>
public interface IPlanTaskDispatchService
{
    /// <summary>
    /// Starts the next pending or failed task for the session when no worker is already active.
    /// </summary>
    Task<PlanTaskDispatchResult> DispatchNextAsync(
        PlanTaskDispatchRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a completed plan-managed worker task as successful and attempts to start the next task.
    /// </summary>
    Task<PlanTaskDispatchResult> ContinueAfterCompletedWorkerAsync(
        BackgroundJob completedJob,
        string? resultPayloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Automatically resumes a plan-managed worker that reached a recovery-needed state.
    /// </summary>
    Task<PlanTaskDispatchResult> ContinueAfterRecoveryNeededWorkerAsync(
        BackgroundJob recoveryJob,
        string? resultPayloadJson,
        CancellationToken ct = default);
}

/// <summary>
/// Input for plan task dispatch.
/// </summary>
/// <param name="SessionId">Session whose plan should be advanced.</param>
/// <param name="UserId">User that owns the session.</param>
/// <param name="WorkspacePath">Workspace path used when reloading session state.</param>
/// <param name="Source">Source marker written to the new worker payload.</param>
/// <param name="PreviousJobId">Completed job that triggered automatic continuation, when any.</param>
/// <param name="PreviousTaskId">Completed task that triggered automatic continuation, when any.</param>
/// <param name="PreviousCommitHash">Forge commit that should be used as the next task baseline, when any.</param>
/// <param name="PreviousChangedFiles">Files changed by the previous forge task, when any.</param>
/// <param name="PreviousShadowWorktreePath">Retained Forge shadow worktree from the previous task, when any.</param>
/// <param name="PreviousWorktreeKey">Worktree key for the retained Forge shadow worktree, when any.</param>
/// <param name="PreviousWorktreeRetained">Whether the previous Forge task retained its shadow worktree for continuation.</param>
/// <param name="EnforcePlanArtifactGate">Whether approved plan artifact gates must be checked before dispatch.</param>
public sealed record PlanTaskDispatchRequest(
    string SessionId,
    string UserId,
    string? WorkspacePath,
    string? Source = null,
    string? PreviousJobId = null,
    string? PreviousTaskId = null,
    string? PreviousCommitHash = null,
    IReadOnlyList<string>? PreviousChangedFiles = null,
    string? PreviousShadowWorktreePath = null,
    string? PreviousWorktreeKey = null,
    bool PreviousWorktreeRetained = false,
    bool EnforcePlanArtifactGate = true);

/// <summary>
/// Result of a plan task dispatch attempt.
/// </summary>
/// <param name="Status">Structured dispatch outcome.</param>
/// <param name="Message">Human-readable message suitable for the gateway tool result.</param>
/// <param name="TaskId">Target task identifier, when one was selected.</param>
/// <param name="Title">Target task title, when one was selected.</param>
/// <param name="JobId">Created or existing background job identifier.</param>
/// <param name="JobType">Created or existing background job type.</param>
/// <param name="WorkerType">Worker type selected for the task.</param>
/// <param name="ExistingJobStatus">Status for an existing duplicate or active job.</param>
/// <param name="StateKind">UI-facing state kind for the job.</param>
public sealed record PlanTaskDispatchResult(
    PlanTaskDispatchStatus Status,
    string Message,
    string? TaskId = null,
    string? Title = null,
    string? JobId = null,
    string? JobType = null,
    WorkerType? WorkerType = null,
    BackgroundJobStatus? ExistingJobStatus = null,
    string? StateKind = null)
{
    /// <summary>
    /// Gets whether this result created a new background job.
    /// </summary>
    public bool Dispatched => Status is PlanTaskDispatchStatus.Dispatched;
}

/// <summary>
/// Structured status for plan task dispatch.
/// </summary>
    public enum PlanTaskDispatchStatus
    {
    /// <summary>A new background job was queued.</summary>
    Dispatched,
    /// <summary>The target task already has an active job.</summary>
    DuplicateActiveJob,
    /// <summary>The session already has an active worker.</summary>
    ActiveTaskRunning,
    /// <summary>No pending or failed task is available.</summary>
    NoActionableTask,
    /// <summary>The plan failed reset or artifact execution gates.</summary>
    PlanInvalid,
    /// <summary>The session could not be loaded.</summary>
    SessionNotFound,
        /// <summary>The current execution mode requires inline execution rather than queue dispatch.</summary>
        InlineExecutionRequired,
        /// <summary>A recovery-needed worker was requeued for continuation.</summary>
        RecoveryResumed,
        /// <summary>The worker is recovery-needed but is not eligible for automatic recovery.</summary>
        RecoveryNotEligible
    }

/// <summary>
/// Default implementation for queueing plan tasks and backend auto-continuation.
/// </summary>
public sealed class PlanTaskDispatchService(
    IJobReadStore jobs,
    IBackgroundJobService jobService,
    CodexSessionManager sessionManager,
    IServiceScopeFactory scopeFactory,
    IOptions<OpenSpecExecutionOptions>? openSpecExecutionOptions,
    ILogger<PlanTaskDispatchService> logger) : IPlanTaskDispatchService
{
    private readonly OpenSpecExecutionOptions _openSpecExecutionOptions =
        openSpecExecutionOptions?.Value ?? new OpenSpecExecutionOptions();
    private const int MaxAutomaticRecoveryAttempts = 2;

    /// <inheritdoc />
    public async Task<PlanTaskDispatchResult> DispatchNextAsync(
        PlanTaskDispatchRequest request,
        CancellationToken ct = default)
    {
        var retainedShadowPathForSession = request.PreviousWorktreeRetained &&
                                           !string.IsNullOrWhiteSpace(request.PreviousShadowWorktreePath) &&
                                           Directory.Exists(request.PreviousShadowWorktreePath)
            ? request.PreviousShadowWorktreePath
            : null;
        var sessionLoadWorkspacePath = retainedShadowPathForSession
            ?? request.WorkspacePath
            ?? string.Empty;

        var latestSession = await sessionManager.GetOrCreateSessionAsync(
            request.SessionId,
            request.UserId,
            sessionLoadWorkspacePath,
            (Uri?)null).ConfigureAwait(false);

        if (latestSession == null)
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.SessionNotFound,
                "⛔ [系统错误] 无法加载当前会话状态，不能启动下一任务。");
        }

        if (retainedShadowPathForSession == null &&
            IsShadowWorkspacePath(latestSession.WorkspacePath) &&
            !IsShadowWorkspacePath(request.WorkspacePath))
        {
            latestSession.WorkspacePath = request.WorkspacePath ?? latestSession.WorkspacePath;
        }

        if (latestSession.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetRequired, out var resetRequiredRaw) &&
            bool.TryParse(resetRequiredRaw, out var resetRequired) &&
            resetRequired)
        {
            latestSession.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetReason, out var resetReason);
            var suffix = string.IsNullOrWhiteSpace(resetReason)
                ? string.Empty
                : $" 当前失效原因：{resetReason}";
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.PlanInvalid,
                "⛔ 当前计划已失效，不能继续执行旧计划中的任务。请先重新生成新计划，再继续执行。" + suffix);
        }

        if (latestSession.PlanGeneratedAtUtc.HasValue && latestSession.Plan.Count == 0)
        {
            logger.LogError(
                "[PlanTaskDispatch] 检测到计划状态丢失，阻止启动下一任务。SessionId={SessionId}, PlanGeneratedAtUtc={PlanGeneratedAtUtc}",
                request.SessionId,
                latestSession.PlanGeneratedAtUtc);

            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.PlanInvalid,
                $"⛔ [系统错误] 检测到计划状态丢失：本会话于 {latestSession.PlanGeneratedAtUtc:u} 已生成计划，但当前计划列表为空。请先恢复计划或重新生成计划。");
        }

        if (request.EnforcePlanArtifactGate)
        {
            var planGateError = await ValidatePlanArtifactExecutionGateAsync(latestSession, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(planGateError))
            {
                return new PlanTaskDispatchResult(PlanTaskDispatchStatus.PlanInvalid, planGateError);
            }
        }

        if (!string.IsNullOrEmpty(latestSession.ActiveTaskId))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.ActiveTaskRunning,
                $"⏳ [SEQUENTIAL_GUARD] 任务 [{latestSession.ActiveTaskId}] 仍在执行，请等待完成后再提交。",
                TaskId: latestSession.ActiveTaskId);
        }

        var target = await ResolveNextActionableTaskAsync(request.SessionId, latestSession, ct).ConfigureAwait(false);
        if (target == null)
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.NoActionableTask,
                "✅ 当前计划中没有待执行任务，所有任务已完成或正在执行。");
        }

        var workerType = ResolveWorkerTypeForTask(target);
        var jobType = WorkerJobTypes.ToJobType(workerType);
        var existingJob = await jobs.FindActiveTaskJobAsync(request.SessionId, target.Id, jobType, ct)
            .ConfigureAwait(false);
        if (existingJob != null)
        {
            var stateKind = ResolveBackgroundJobStateKind(existingJob);
            logger.LogInformation(
                "[PlanTaskDispatch] 命中已有后台作业，跳过重复派发: TaskId={TaskId}, WorkerType={WorkerType}, JobId={JobId}, Status={Status}",
                target.Id,
                workerType,
                existingJob.JobId,
                existingJob.Status);

            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.DuplicateActiveJob,
                $"⏳ 任务 [{target.Id}] {target.Title} 已存在后台作业 {existingJob.JobId}（状态：{existingJob.Status}），已跳过重复派发。",
                target.Id,
                target.Title,
                existingJob.JobId,
                jobType,
                workerType,
                existingJob.Status,
                stateKind);
        }

        var activeSessionJob = await jobs.FindActiveSessionWorkerAsync(request.SessionId, ct)
            .ConfigureAwait(false);
        if (activeSessionJob != null)
        {
            var activeTaskLabel = string.IsNullOrWhiteSpace(activeSessionJob.TaskId)
                ? activeSessionJob.JobId
                : activeSessionJob.TaskId;

            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.ActiveTaskRunning,
                $"⏳ [SEQUENTIAL_GUARD] 任务 [{activeTaskLabel}] 仍在执行，请等待完成后再提交。",
                TaskId: activeSessionJob.TaskId,
                JobId: activeSessionJob.JobId,
                ExistingJobStatus: activeSessionJob.Status,
                StateKind: ResolveBackgroundJobStateKind(activeSessionJob));
        }

        if (!_openSpecExecutionOptions.UseBackgroundJob)
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.InlineExecutionRequired,
                "⛔ 当前配置要求同步执行任务，服务端自动续跑只支持后台作业模式。");
        }

        var executionWorkspacePath = ToolPathResolver.ResolveProjectRoot(
            latestSession.WorkspacePath,
            null,
            latestSession.ProjectUrl,
            latestSession.Metadata);

        if (retainedShadowPathForSession != null &&
            !string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            executionWorkspacePath = request.WorkspacePath;
        }
        else if (IsShadowWorkspacePath(executionWorkspacePath) &&
            !IsShadowWorkspacePath(request.WorkspacePath))
        {
            executionWorkspacePath = request.WorkspacePath;
        }

        if (string.IsNullOrWhiteSpace(executionWorkspacePath))
        {
            executionWorkspacePath = latestSession.WorkspacePath;
        }

        var workerPrompt = BuildWorkerExecutionPrompt(target, workerType);
        var source = string.IsNullOrWhiteSpace(request.Source) ? "start_next_task" : request.Source;
        var jobPayload = new JObject
        {
            ["sessionId"] = request.SessionId,
            ["userId"] = request.UserId,
            ["workspacePath"] = executionWorkspacePath,
            ["prompt"] = workerPrompt,
            ["workerType"] = workerType.ToString().ToLowerInvariant(),
            ["source"] = source,
            ["spawnedAtUtc"] = DateTime.UtcNow,
            ["description"] = target.Title,
            ["taskId"] = target.Id
        };

        // Capture the approved plan at dispatch, before a later edit can reuse task identifiers.
        await using (var planScope = scopeFactory.CreateAsyncScope())
        {
            var planStore = planScope.ServiceProvider.GetService<IPlanArtifactStore>();
            var boundPlan = planStore is null ? null : await planStore.GetCurrentAsync(request.SessionId, ct).ConfigureAwait(false);
            if (boundPlan is not null)
            {
                jobPayload["planArtifactId"] = boundPlan.Id;
                jobPayload["planVersion"] = boundPlan.Version;
                jobPayload["planHash"] = PlanArtifactSupport.ComputeMarkdownHash(boundPlan.Markdown);
                jobPayload["requiredTaskIds"] = OrchestrationPayloadJson.Array(boundPlan.Tasks.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray());
            }
        }

        if (!string.IsNullOrWhiteSpace(request.PreviousJobId))
        {
            jobPayload["previousJobId"] = request.PreviousJobId;
        }

        if (!string.IsNullOrWhiteSpace(request.PreviousTaskId))
        {
            jobPayload["previousTaskId"] = request.PreviousTaskId;
        }

        if (!string.IsNullOrWhiteSpace(request.PreviousCommitHash))
        {
            jobPayload["previousCommitHash"] = request.PreviousCommitHash;
            jobPayload["lastCommitHash"] = request.PreviousCommitHash;
        }

        if (request.PreviousChangedFiles is { Count: > 0 })
        {
            jobPayload["previousChangedFiles"] = OrchestrationPayloadJson.Array(request.PreviousChangedFiles);
            jobPayload["lastChangedFiles"] = OrchestrationPayloadJson.Array(request.PreviousChangedFiles);
        }

        if (string.Equals(source, "auto_continuation", StringComparison.OrdinalIgnoreCase))
        {
            jobPayload["autoContinuation"] = true;
        }

        if (workerType == WorkerType.Forge)
        {
            jobPayload["mainWorkspacePath"] = executionWorkspacePath;
            var retainedShadowPath = request.PreviousWorktreeRetained
                ? request.PreviousShadowWorktreePath
                : null;
            var canReuseRetainedShadow = !string.IsNullOrWhiteSpace(retainedShadowPath) &&
                                         Directory.Exists(retainedShadowPath);
            var worktreeKey = canReuseRetainedShadow && !string.IsNullOrWhiteSpace(request.PreviousWorktreeKey)
                ? request.PreviousWorktreeKey
                : string.IsNullOrWhiteSpace(target.Id) ? $"forge-{Guid.NewGuid():N}" : target.Id;

            jobPayload["worktreeKey"] = worktreeKey;
            if (!string.IsNullOrWhiteSpace(request.PreviousCommitHash))
            {
                jobPayload["baseRef"] = request.PreviousCommitHash;
            }

            if (canReuseRetainedShadow)
            {
                jobPayload["workspacePath"] = retainedShadowPath;
                jobPayload["shadowWorktreePath"] = retainedShadowPath;
                jobPayload["worktreeRetained"] = true;
                jobPayload["continuedShadowFromTaskId"] = request.PreviousTaskId;
                jobPayload["continuedShadowFromJobId"] = request.PreviousJobId;
            }
        }

        var jobId = await jobService.CreateJobAsync(
            request.SessionId,
            target.Id,
            jobType,
            jobPayload.ToString(Formatting.None),
            startRunning: false).ConfigureAwait(false);

        var sessionTask = latestSession.Plan.FirstOrDefault(t =>
            string.Equals(t.Id, target.Id, StringComparison.OrdinalIgnoreCase)) ?? target;
        latestSession.ActiveTaskId = target.Id;
        sessionTask.Status = CodexTaskStatus.Executing;
        sessionTask.StartedAt ??= DateTime.UtcNow;
        sessionTask.FinishedAt = null;
        if (latestSession.CurrentStage < 3)
        {
            latestSession.CurrentStage = 3;
        }

        await sessionManager.UpdateSessionAsync(latestSession).ConfigureAwait(false);
        await TryUpdateTaskListStatusAsync(latestSession, target.Id, CodexTaskStatus.Executing, null, ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[PlanTaskDispatch] 派发 {WorkerType} worker: SessionId={SessionId}, TaskId={TaskId}, JobId={JobId}, Source={Source}",
            workerType,
            request.SessionId,
            target.Id,
            jobId,
            source);

        return new PlanTaskDispatchResult(
            PlanTaskDispatchStatus.Dispatched,
            workerType == WorkerType.Forge
                ? $"✅ 已按计划顺序派发任务 [{target.Id}] {target.Title}（作业 ID: {jobId}）。系统将通过 forge worker 在隔离 shadow worktree 中后台执行，完成后通过通知推送结果。"
                : $"✅ 已按计划顺序派发任务 [{target.Id}] {target.Title}（作业 ID: {jobId}）。系统将通过 {workerType.ToString().ToLowerInvariant()} worker 在后台执行，完成后通过通知推送结果。",
            target.Id,
            target.Title,
            jobId,
            jobType,
            workerType,
            BackgroundJobStatus.Queued,
            "queued");
    }

    /// <inheritdoc />
    public async Task<PlanTaskDispatchResult> ContinueAfterCompletedWorkerAsync(
        BackgroundJob completedJob,
        string? resultPayloadJson,
        CancellationToken ct = default)
    {
        if (completedJob.Status != BackgroundJobStatus.Completed ||
            !WorkerJobTypes.IsWorkerJobType(completedJob.JobType) ||
            string.IsNullOrWhiteSpace(completedJob.SessionId) ||
            string.IsNullOrWhiteSpace(completedJob.TaskId))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.NoActionableTask,
                "当前作业不满足自动续跑条件。");
        }

        var payload = TryParseJobPayload(completedJob.PayloadJson);
        var source = payload?["source"]?.Value<string>();
        if (!IsPlanManagedWorkerSource(source))
        {
            logger.LogDebug(
                "[PlanTaskDispatch] 跳过非计划派发 worker 的自动续跑。JobId={JobId}, Source={Source}",
                completedJob.JobId,
                source ?? "<null>");

            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.NoActionableTask,
                "当前作业不是计划派发 worker，跳过自动续跑。");
        }

        var userId = payload?["userId"]?.Value<string>() ?? string.Empty;
        var workspacePath = ResolveContinuationWorkspacePath(payload);
        var resultPayload = TryParseJobPayload(resultPayloadJson);
        var previousCommitHash = ExtractCommitHash(resultPayload);
        var previousChangedFiles = ExtractChangedFiles(resultPayload);
        var retainedWorktree = ExtractRetainedWorktreeContext(resultPayload, payload);
        var sessionWorkspacePath = retainedWorktree.Retained &&
                                   !string.IsNullOrWhiteSpace(retainedWorktree.ShadowWorktreePath) &&
                                   Directory.Exists(retainedWorktree.ShadowWorktreePath)
            ? retainedWorktree.ShadowWorktreePath!
            : workspacePath;

        var session = await sessionManager.GetOrCreateSessionAsync(
            completedJob.SessionId!,
            userId,
            sessionWorkspacePath,
            (Uri?)null).ConfigureAwait(false);

        await ApplyCompletedTaskStatusToSessionAsync(
            session,
            completedJob.TaskId,
            ExtractResultNotes(resultPayloadJson),
            ct).ConfigureAwait(false);

        var result = await DispatchNextAsync(
            new PlanTaskDispatchRequest(
                completedJob.SessionId!,
                userId,
                workspacePath,
                Source: "auto_continuation",
                PreviousJobId: completedJob.JobId,
                PreviousTaskId: completedJob.TaskId,
                PreviousCommitHash: previousCommitHash,
                PreviousChangedFiles: previousChangedFiles,
                PreviousShadowWorktreePath: retainedWorktree.ShadowWorktreePath,
                PreviousWorktreeKey: retainedWorktree.WorktreeKey,
                PreviousWorktreeRetained: retainedWorktree.Retained),
            ct).ConfigureAwait(false);

        if (result.Dispatched)
        {
            logger.LogInformation(
                "[PlanTaskDispatch] worker 完成后已自动派发下一任务。PreviousJobId={PreviousJobId}, PreviousTaskId={PreviousTaskId}, NextTaskId={NextTaskId}, NextJobId={NextJobId}",
                completedJob.JobId,
                completedJob.TaskId,
                result.TaskId,
                result.JobId);
        }
        else
        {
            logger.LogInformation(
                "[PlanTaskDispatch] worker 完成后未派发下一任务。PreviousJobId={PreviousJobId}, PreviousTaskId={PreviousTaskId}, Status={Status}, Message={Message}",
                completedJob.JobId,
                completedJob.TaskId,
                result.Status,
                result.Message);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PlanTaskDispatchResult> ContinueAfterRecoveryNeededWorkerAsync(
        BackgroundJob recoveryJob,
        string? resultPayloadJson,
        CancellationToken ct = default)
    {
        if (recoveryJob.Status != BackgroundJobStatus.FailedRecoveryNeeded ||
            !WorkerJobTypes.IsWorkerJobType(recoveryJob.JobType) ||
            string.IsNullOrWhiteSpace(recoveryJob.SessionId) ||
            string.IsNullOrWhiteSpace(recoveryJob.TaskId))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                "当前作业不满足自动恢复条件。");
        }

        var payload = TryParseJobPayload(recoveryJob.PayloadJson) ?? new JObject();
        var source = payload["source"]?.Value<string>();
        if (!IsPlanManagedWorkerSource(source))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                "当前作业不是计划派发 worker，跳过自动恢复。");
        }

        var resultPayload = TryParseJobPayload(resultPayloadJson);
        if (resultPayload?["recoveryNeeded"]?.Value<bool>() != true ||
            !string.Equals(resultPayload["resumeStrategy"]?.Value<string>(), "continue_worker", StringComparison.OrdinalIgnoreCase))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                "当前 recovery-needed 作业未声明 continue_worker 恢复策略，跳过自动恢复。");
        }

        var recoveryAttempt = Math.Max(0, payload["automaticRecoveryAttempt"]?.Value<int?>() ?? 0);
        if (recoveryAttempt >= MaxAutomaticRecoveryAttempts)
        {
            logger.LogWarning(
                "[PlanTaskDispatch] 自动恢复次数已达上限，保留 recovery-needed 供人工介入。JobId={JobId}, TaskId={TaskId}, Attempts={Attempts}",
                recoveryJob.JobId,
                recoveryJob.TaskId,
                recoveryAttempt);

            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                $"当前作业已自动恢复 {recoveryAttempt} 次，达到上限，等待人工介入。",
                recoveryJob.TaskId,
                JobId: recoveryJob.JobId,
                JobType: recoveryJob.JobType,
                ExistingJobStatus: recoveryJob.Status,
                StateKind: "recovery_needed");
        }

        if (!WorkerJobTypes.TryResolveWorkerType(recoveryJob.JobType, out var workerType))
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                "无法解析 worker 类型，跳过自动恢复。");
        }

        payload["prompt"] = BuildAutomaticRecoveryPrompt(payload, resultPayload, workerType);
        payload["continuedFromJobId"] = recoveryJob.JobId;
        payload["continuedAtUtc"] = DateTime.UtcNow;
        payload["requiresUserConfirmation"] = false;
        payload["source"] = source;
        payload["autoRecovery"] = true;
        payload["automaticRecoveryAttempt"] = recoveryAttempt + 1;

        var resumeResult = await jobService.ResumeJobAsync(
            recoveryJob.JobId,
            resumeToken: null,
            payload.ToString(Formatting.None)).ConfigureAwait(false);

        if (resumeResult != BackgroundJobCommandResult.Success)
        {
            return new PlanTaskDispatchResult(
                PlanTaskDispatchStatus.RecoveryNotEligible,
                $"自动恢复失败：{resumeResult}。",
                recoveryJob.TaskId,
                JobId: recoveryJob.JobId,
                JobType: recoveryJob.JobType,
                ExistingJobStatus: recoveryJob.Status,
                StateKind: "recovery_needed");
        }

        logger.LogInformation(
            "[PlanTaskDispatch] recovery-needed worker 已自动续跑。JobId={JobId}, TaskId={TaskId}, Attempt={Attempt}",
            recoveryJob.JobId,
            recoveryJob.TaskId,
            recoveryAttempt + 1);

        return new PlanTaskDispatchResult(
            PlanTaskDispatchStatus.RecoveryResumed,
            $"✅ recovery-needed worker 已自动续跑：任务 [{recoveryJob.TaskId}]，作业 ID: {recoveryJob.JobId}。",
            recoveryJob.TaskId,
            JobId: recoveryJob.JobId,
            JobType: recoveryJob.JobType,
            WorkerType: workerType,
            ExistingJobStatus: BackgroundJobStatus.Queued,
            StateKind: "queued");
    }

    private static bool IsPlanManagedWorkerSource(string? source)
        => string.Equals(source, "start_next_task", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "approve_plan_auto_start", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "auto_continuation", StringComparison.OrdinalIgnoreCase);

    private static string BuildAutomaticRecoveryPrompt(
        JObject payload,
        JObject? resultPayload,
        WorkerType workerType)
    {
        var originalPrompt = payload["prompt"]?.Value<string>()
            ?? payload["message"]?.Value<string>()
            ?? payload["description"]?.Value<string>();
        var summary = resultPayload?["summary"]?.Value<string>()
            ?? resultPayload?["message"]?.Value<string>();
        var terminationReason = resultPayload?["terminationReason"]?.Value<string>();
        var detailCode = resultPayload?["detailCode"]?.Value<string>();
        var recoveryReason = resultPayload?["recoveryReason"]?.Value<string>();
        var commitHash = resultPayload?["commitHash"]?.Value<string>();
        var changedFiles = resultPayload?["changedFiles"]?
            .Values<string?>()
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"你正在自动续跑之前进入 recovery-needed 的 {workerType.ToString().ToLowerInvariant()} worker。");
        builder.AppendLine("不要重新开始整个任务；优先复用上次已落盘/已提交的改动，只补齐缺失的验证、收尾或最小修复。");

        if (!string.IsNullOrWhiteSpace(originalPrompt))
        {
            builder.AppendLine();
            builder.AppendLine("原始任务：");
            builder.AppendLine(originalPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.AppendLine();
            builder.AppendLine("上次摘要：");
            builder.AppendLine(summary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(recoveryReason))
        {
            builder.AppendLine();
            builder.AppendLine($"恢复原因：{recoveryReason.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(terminationReason))
        {
            builder.AppendLine($"上次终止原因：{terminationReason.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(detailCode))
        {
            builder.AppendLine($"上次 detail code：{detailCode.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(commitHash))
        {
            builder.AppendLine();
            builder.AppendLine($"上次提交：{commitHash.Trim()}");
        }

        if (changedFiles.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("上次已落盘文件：");
            foreach (var changedFile in changedFiles)
            {
                builder.Append("- ");
                builder.AppendLine(changedFile);
            }
        }

        builder.AppendLine();
        builder.AppendLine("本次续跑要求：");
        builder.AppendLine("1. 先确认上次改动仍然存在，不要重复做同样修改。");
        builder.AppendLine("2. 如果原任务要求 build/test，请立即运行相应验证命令。");
        builder.AppendLine("3. 验证通过后给出非空最终摘要并结束；验证失败时只做当前任务范围内的最小修复后再次验证。");
        builder.AppendLine("4. 不要扩散到当前任务范围外的文件。");

        return builder.ToString().Trim();
    }

    private static string ResolveContinuationWorkspacePath(JObject? payload)
    {
        var mainWorkspacePath = payload?["mainWorkspacePath"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(mainWorkspacePath))
        {
            return mainWorkspacePath;
        }

        return payload?["workspacePath"]?.Value<string>() ?? string.Empty;
    }

    private static bool IsShadowWorkspacePath(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains($"{Path.DirectorySeparatorChar}shadows{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string BuildWorkerExecutionPrompt(CodexTask target, WorkerType workerType)
        => workerType switch
        {
            WorkerType.Explore => BuildExploreExecutionPrompt(target),
            _ => BuildForgeExecutionPrompt(target)
        };

    private static string BuildForgeExecutionPrompt(CodexTask target)
    {
        var description = string.IsNullOrWhiteSpace(target.Description)
            ? "未提供额外描述。"
            : target.Description.Trim();

        return
            $"请在隔离的 shadow worktree 中完成当前计划任务。\n\n" +
            $"任务 ID: {target.Id}\n" +
            $"任务标题: {target.Title}\n" +
            $"任务描述:\n{description}\n\n" +
            "执行要求：\n" +
            "1. 仅处理当前任务，不要自行切换到其他计划任务。\n" +
            "2. 优先做最小必要修改，并保持工作区可验证。\n" +
            "3. 第一轮必须直接开始行动，优先调用工具，不要只输出说明性文字。\n" +
            "4. 如果不确定文件位置，先搜索再读取；不要空响应。\n" +
            "5. 如果已经读取到目标文件快照，本轮必须调用写入工具；只输出“现在修改/开始修复/准备落地”会被视为失败。\n" +
            "6. 修改既有文件优先使用 `hs_write` 或 `ivilson_smart_patch`；不要在已有快照后再次读取相同文件。\n" +
            "7. 修改完成后立即给出一条非空收尾消息；不要继续空转、重复扫描或反复自我解释。\n" +
            "8. 不要顺手处理当前任务未要求的依赖升级、漏洞治理、项目文件或配置文件修改；这些应留给对应计划任务。\n" +
            "9. 完成后汇总关键修改、验证结果和剩余风险；如果没有验证工具，要明确说明未验证原因后结束。\n";
    }

    private static string BuildExploreExecutionPrompt(CodexTask target)
    {
        var description = string.IsNullOrWhiteSpace(target.Description)
            ? "未提供额外描述。"
            : target.Description.Trim();

        return
            $"请在当前项目中完成当前分析任务。\n\n" +
            $"任务 ID: {target.Id}\n" +
            $"任务标题: {target.Title}\n" +
            $"任务描述:\n{description}\n\n" +
            "执行要求：\n" +
            "1. 仅处理当前任务，不要自行切换到其他计划任务。\n" +
            "2. 保持只读分析，不要修改代码或生成补丁。\n" +
            "3. 第一轮必须直接开始行动，优先调用工具，不要只输出说明性文字。\n" +
            "4. 如果不确定文件位置，先搜索再读取；不要空响应。\n" +
            "5. 完成后输出结构化结论、根因判断和下一步建议。\n";
    }

    private async Task<CodexTask?> ResolveNextActionableTaskAsync(string sessionId, CodexSession latestSession, CancellationToken ct)
    {
        var snapshot = await TryGetTaskListSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot?.Items.Count > 0)
        {
            var nextSnapshotTask = snapshot.Items
                .Where(item => IsActionableTaskListStatus(item.Status))
                .OrderBy(item => item.Index)
                .FirstOrDefault();

            if (nextSnapshotTask == null)
            {
                logger.LogInformation(
                    "[PlanTaskDispatch] 使用 tasklist 快照判定已无待执行任务。SessionId={SessionId}, SnapshotCount={Count}",
                    sessionId,
                    snapshot.Items.Count);
                return null;
            }

            var planTask = latestSession.Plan.FirstOrDefault(t =>
                string.Equals(t.Id, nextSnapshotTask.Id, StringComparison.OrdinalIgnoreCase));

            if (planTask != null)
            {
                return planTask;
            }

            logger.LogWarning(
                "[PlanTaskDispatch] tasklist 快照中的任务未在 session plan 中命中，使用快照任务作为回退。SessionId={SessionId}, TaskId={TaskId}, Title={Title}, Status={Status}",
                sessionId,
                nextSnapshotTask.Id,
                nextSnapshotTask.Title,
                nextSnapshotTask.Status);

            return new CodexTask
            {
                Id = nextSnapshotTask.Id,
                Title = nextSnapshotTask.Title,
                Description = nextSnapshotTask.Description,
                Status = MapSnapshotTaskStatus(nextSnapshotTask.Status),
                TaskType = CodexTaskClassifier.CodeTaskType
            };
        }

        return latestSession.Plan.FirstOrDefault(t => t.Status is CodexTaskStatus.Pending or CodexTaskStatus.Failed);
    }

    private async Task<TaskListSnapshot?> TryGetTaskListSnapshotAsync(string sessionId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var taskListService = scope.ServiceProvider.GetService<ITaskListService>();
        if (taskListService == null)
        {
            return null;
        }

        var snapshotJson = await taskListService.GetSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                snapshotJson, CodexFlow.Orchestration.Persistence.OrchestrationSnapshotJsonContext.CaseInsensitive.PlanTaskList);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(
                ex,
                "[PlanTaskDispatch] 反序列化 tasklist 快照失败，将回退为 session plan。SessionId={SessionId}",
                sessionId);
            return null;
        }
    }

    private static bool IsActionableTaskListStatus(string? status)
        => !string.IsNullOrWhiteSpace(status) &&
           status.Trim().ToLowerInvariant() switch
           {
               "pending" => true,
               "failed" => true,
               _ => false
           };

    private static CodexTaskStatus MapSnapshotTaskStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? CodexTaskStatus.Pending
            : status.Trim().ToLowerInvariant() switch
            {
                "completed" => CodexTaskStatus.Success,
                "completedwithwarnings" => CodexTaskStatus.CompletedWithWarnings,
                "running" => CodexTaskStatus.Executing,
                "failed" => CodexTaskStatus.Failed,
                "skipped" => CodexTaskStatus.Skipped,
                _ => CodexTaskStatus.Pending
            };

    private static WorkerType ResolveWorkerTypeForTask(CodexTask target)
        => CodexTaskClassifier.IsAnalysisTask(target) ? WorkerType.Explore : WorkerType.Forge;

    private static string ResolveBackgroundJobStateKind(ActivePlanJob job)
        => WorkerProjectionHelpers.ResolveStateKind(
            job.Status.ToString(),
            job.ResultPayloadJson,
            job.Attempt,
            job.LatestCheckpointStep);

    private async Task ApplyCompletedTaskStatusToSessionAsync(
        CodexSession session,
        string? taskId,
        string? resultNotes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var task = session.Plan.FirstOrDefault(t =>
            string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return;
        }

        if (session.CurrentStage < 3)
        {
            session.CurrentStage = 3;
        }

        if (string.Equals(session.ActiveTaskId, taskId, StringComparison.OrdinalIgnoreCase))
        {
            session.ActiveTaskId = null;
        }

        task.Status = CodexTaskStatus.Success;
        task.ResultNotes = resultNotes;
        task.FinishedAt = DateTime.UtcNow;

        await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        await TryUpdateTaskListStatusAsync(session, taskId, CodexTaskStatus.Success, null, ct).ConfigureAwait(false);
    }

    private async Task TryUpdateTaskListStatusAsync(
        CodexSession session,
        string taskId,
        CodexTaskStatus status,
        string? errorMessage,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var taskListService = scope.ServiceProvider.GetService<ITaskListService>();
        if (taskListService == null)
        {
            return;
        }

        await taskListService
            .UpdateTaskStatusAsync(session, taskId, status, errorMessage, ct)
            .ConfigureAwait(false);
    }

    private async Task<string?> ValidatePlanArtifactExecutionGateAsync(CodexSession session, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetService<IOptions<PlanningOptions>>()?
            .Value ?? new PlanningOptions();

        if (options.PlanArtifactMode == PlanningMode.Off && !options.PlanApprovalRequired)
        {
            return null;
        }

        var store = scope.ServiceProvider.GetService<IPlanArtifactStore>();
        if (store == null)
        {
            return null;
        }

        var artifact = !string.IsNullOrWhiteSpace(session.CurrentPlanArtifactId)
            ? await store.GetAsync(session.CurrentPlanArtifactId, ct).ConfigureAwait(false)
            : await store.GetCurrentAsync(session.Id, ct).ConfigureAwait(false);

        if (artifact == null)
        {
            return options.PlanApprovalRequired
                ? "⛔ 当前会话没有已批准并投影的 PlanArtifact，不能启动任务。请先生成并批准计划。"
                : null;
        }

        if (artifact.Status is not PlanArtifactStatus.ReadyToExecute and not PlanArtifactStatus.Executing)
        {
            return artifact.Status == PlanArtifactStatus.Approved
                ? "⛔ 当前计划已批准但尚未完成任务投影与校验。请先调用 project_plan_to_tasks。"
                : $"⛔ 当前计划状态为 {artifact.Status}，不能启动执行。";
        }

        var projection = artifact.Projection;
        var approvedHash = artifact.Approval?.ApprovedMarkdownHash;
        var projectedHash = projection?.ProjectedMarkdownHash;
        if (projection == null ||
            string.IsNullOrWhiteSpace(approvedHash) ||
            string.IsNullOrWhiteSpace(projectedHash) ||
            !string.Equals(approvedHash, projectedHash, StringComparison.Ordinal))
        {
            return "⛔ 当前计划审批版本与任务投影版本不一致，不能启动执行。请重新投影任务。";
        }

        return null;
    }

    private static JObject? TryParseJobPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JObject.Parse(payloadJson);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractResultNotes(string? resultPayloadJson)
    {
        var payload = TryParseJobPayload(resultPayloadJson);
        return payload?["summary"]?.Value<string>()
               ?? payload?["message"]?.Value<string>()
               ?? payload?["result"]?.Value<string>();
    }

    private static string? ExtractCommitHash(JObject? payload)
        => payload?["commitHash"]?.Value<string>()
           ?? TryParseJobPayload(payload?["updatedPayloadJson"]?.Value<string>())?["lastCommitHash"]?.Value<string>();

    private static IReadOnlyList<string> ExtractChangedFiles(JObject? payload)
    {
        var direct = payload?["changedFiles"]?
            .Values<string?>()
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (direct is { Length: > 0 })
        {
            return direct;
        }

        var updatedPayload = TryParseJobPayload(payload?["updatedPayloadJson"]?.Value<string>());
        return updatedPayload?["lastChangedFiles"]?
            .Values<string?>()
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];
    }

    private static RetainedWorktreeContext ExtractRetainedWorktreeContext(JObject? resultPayload, JObject? jobPayload)
    {
        var updatedPayload = TryParseJobPayload(resultPayload?["updatedPayloadJson"]?.Value<string>());
        var retained = resultPayload?["worktreeRetained"]?.Value<bool?>()
                       ?? updatedPayload?["worktreeRetained"]?.Value<bool?>()
                       ?? jobPayload?["worktreeRetained"]?.Value<bool?>()
                       ?? false;
        var shadowPath = resultPayload?["worktreePath"]?.Value<string>()
                         ?? updatedPayload?["shadowWorktreePath"]?.Value<string>()
                         ?? jobPayload?["shadowWorktreePath"]?.Value<string>();
        var worktreeKey = updatedPayload?["worktreeKey"]?.Value<string>()
                          ?? jobPayload?["worktreeKey"]?.Value<string>();

        return new RetainedWorktreeContext(retained, shadowPath, worktreeKey);
    }

    private sealed record RetainedWorktreeContext(
        bool Retained,
        string? ShadowWorktreePath,
        string? WorktreeKey);

    internal sealed class TaskListSnapshot
    {
        public string SessionId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public List<TaskListItem> Items { get; init; } = [];
        public DateTime UpdatedAt { get; init; }
    }

    internal sealed class TaskListItem
    {
        public string Id { get; init; } = string.Empty;
        public int Index { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }
}
