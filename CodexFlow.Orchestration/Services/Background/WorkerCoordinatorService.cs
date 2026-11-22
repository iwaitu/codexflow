// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Remote;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

/// <summary>
/// Worker 协调服务。
/// </summary>
/// <remarks>
/// 负责 spawn / continue / stop / cleanup / list_workers 这类协同操作对应的后端编排。
/// </remarks>
public sealed class WorkerCoordinatorService
{
    private readonly IJobStore _jobs;
    private readonly IWorkerJobStore _workerJobs;
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly IJobViewService _jobViewService;
    private readonly IGitService _gitService;
    private readonly IRemoteJobOriginStore? _remoteJobOriginStore;

    /// <summary>
    /// 初始化 worker 协调服务。
    /// </summary>
    /// <param name="jobs">后台作业存储。</param>
    /// <param name="workerJobs">Worker 检查点和工作树元数据存储。</param>
    /// <param name="backgroundJobService">后台作业生命周期管理服务。</param>
    /// <param name="jobViewService">后台作业只读视图服务。</param>
    /// <param name="gitService">用于 forge worker shadow worktree 管理的 Git 服务。</param>
    /// <param name="remoteJobOriginStore">可选远程来源账本，防止远程通道继续或恢复非远程作业。</param>
    public WorkerCoordinatorService(
        IJobStore jobs,
        IWorkerJobStore workerJobs,
        IBackgroundJobService backgroundJobService,
        IJobViewService jobViewService,
        IGitService gitService,
        IRemoteJobOriginStore? remoteJobOriginStore = null)
    {
        _jobs = jobs;
        _workerJobs = workerJobs;
        _backgroundJobService = backgroundJobService;
        _jobViewService = jobViewService;
        _gitService = gitService;
        _remoteJobOriginStore = remoteJobOriginStore;
    }

    /// <summary>
    /// 创建一个新的 worker 作业。
    /// </summary>
    /// <param name="request">worker 类型、prompt、工作区和会话上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回新建 worker 的作业标识和状态。</returns>
    public async Task<SpawnWorkerResult> SpawnWorkerAsync(SpawnWorkerRequest request, CancellationToken ct = default)
    {
        var worktreeKey = request.WorkerType == WorkerType.Forge
            ? (string.IsNullOrWhiteSpace(request.TaskId)
                ? $"forge-{Guid.NewGuid():N}"
                : request.TaskId)
            : null;

        var payload = new JObject
        {
            ["sessionId"] = request.SessionId,
            ["userId"] = request.UserId,
            ["workspacePath"] = request.WorkspacePath,
            ["prompt"] = request.Prompt,
            ["workerType"] = request.WorkerType.ToString().ToLowerInvariant(),
            ["source"] = "spawn_worker",
            ["spawnedAtUtc"] = DateTime.UtcNow
        };

        if (request.WorkerType == WorkerType.Forge)
        {
            payload["mainWorkspacePath"] = request.WorkspacePath;
            payload["worktreeKey"] = worktreeKey;
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            payload["description"] = request.Description;
        }

        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            payload["taskId"] = request.TaskId;
        }

        if (request.MaxRounds.HasValue)
        {
            payload["maxRounds"] = request.MaxRounds.Value;
        }

        var jobType = WorkerJobTypes.ToJobType(request.WorkerType);
        var jobId = await _backgroundJobService.CreateJobAsync(
            request.SessionId,
            request.TaskId,
            jobType,
            payload.ToString(Formatting.None)).ConfigureAwait(false);

        return new SpawnWorkerResult
        {
            Success = true,
            JobId = jobId,
            JobType = jobType,
            Status = "queued"
        };
    }

    /// <summary>
    /// 继续一个已有 worker。
    /// </summary>
    /// <param name="request">目标作业、追问内容以及恢复所需的上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回恢复原作业或创建 follow-up 作业的结果。</returns>
    public async Task<ContinueWorkerResult> ContinueWorkerAsync(ContinueWorkerRequest request, CancellationToken ct = default)
    {
        var continuation = await _workerJobs.FindContinuationAsync(request.JobId, ct).ConfigureAwait(false);

        if (continuation is null)
        {
            return new ContinueWorkerResult
            {
                Success = false,
                Mode = ContinueWorkerMode.SpawnedFollowUp,
                JobId = request.JobId,
                Message = "worker 不存在。"
            };
        }

        var job = continuation.Job;
        if (RemoteExecutionAmbientContext.IsRemote)
        {
            var origin = _remoteJobOriginStore is null
                ? null
                : await _remoteJobOriginStore.FindJobOriginAsync(job.JobId, ct).ConfigureAwait(false);
            if (origin is null || origin.Channel != ControlChannel.Remote)
            {
                return new ContinueWorkerResult
                {
                    Success = false,
                    Mode = ContinueWorkerMode.SpawnedFollowUp,
                    JobId = request.JobId,
                    Message = "远程通道禁止唤醒、继续或派生非远程来源的历史 worker 作业。"
                };
            }
        }

        if (!WorkerJobTypes.TryResolveWorkerType(job.JobType, out var workerType))
        {
            return new ContinueWorkerResult
            {
                Success = false,
                Mode = ContinueWorkerMode.SpawnedFollowUp,
                JobId = request.JobId,
                Message = "该 job 不是受支持的 worker 作业。"
            };
        }

        if (job.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Running)
        {
            return new ContinueWorkerResult
            {
                Success = false,
                Mode = ContinueWorkerMode.SpawnedFollowUp,
                JobId = request.JobId,
                Message = $"worker 当前仍处于 {job.Status} 状态，请等待完成或先停止。"
            };
        }

        var payload = ParsePayload(job.PayloadJson) ?? new JObject();

        if (job.Status is BackgroundJobStatus.WaitingUser or BackgroundJobStatus.FailedRecoveryNeeded)
        {
            payload["prompt"] = BuildContinuationPrompt(payload, job.ResultPayloadJson, request.Prompt, workerType, job.Status == BackgroundJobStatus.FailedRecoveryNeeded ? job.ErrorMessage ?? string.Empty : null);
            payload["continuedFromJobId"] = job.JobId;
            payload["continuedAtUtc"] = DateTime.UtcNow;
            payload["requiresUserConfirmation"] = false;

            var resumeToken = request.ResumeToken;
            if (string.IsNullOrWhiteSpace(resumeToken))
            {
                resumeToken = continuation.ResumeToken;
            }

            var resumeResult = await _backgroundJobService.ResumeJobAsync(
                job.JobId,
                resumeToken,
                payload.ToString(Formatting.None)).ConfigureAwait(false);

            return resumeResult == BackgroundJobCommandResult.Success
                ? new ContinueWorkerResult
                {
                    Success = true,
                    Mode = ContinueWorkerMode.Resumed,
                    JobId = job.JobId
                }
                : new ContinueWorkerResult
                {
                    Success = false,
                    Mode = ContinueWorkerMode.Resumed,
                    JobId = job.JobId,
                    Message = "worker 无法恢复，可能缺少 resume token 或当前状态不允许恢复。"
                };
        }

        if (job.Status is not (BackgroundJobStatus.Completed or BackgroundJobStatus.Failed or BackgroundJobStatus.Cancelled))
        {
            return new ContinueWorkerResult
            {
                Success = false,
                Mode = ContinueWorkerMode.SpawnedFollowUp,
                JobId = job.JobId,
                Message = $"当前状态 {job.Status} 不支持 follow-up。"
            };
        }

        payload["prompt"] = BuildContinuationPrompt(payload, job.ResultPayloadJson, request.Prompt, workerType, job.Status == BackgroundJobStatus.Failed ? job.ErrorMessage ?? string.Empty : null);
        payload["continuedFromJobId"] = job.JobId;
        payload["continuedAtUtc"] = DateTime.UtcNow;
        payload["requiresUserConfirmation"] = false;
        payload["source"] = "continue_worker";
        payload["workerType"] = workerType.ToString().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(payload["sessionId"]?.Value<string>()) && !string.IsNullOrWhiteSpace(request.SessionIdFallback))
        {
            payload["sessionId"] = request.SessionIdFallback;
        }

        if (string.IsNullOrWhiteSpace(payload["userId"]?.Value<string>()) && !string.IsNullOrWhiteSpace(request.UserIdFallback))
        {
            payload["userId"] = request.UserIdFallback;
        }

        if (string.IsNullOrWhiteSpace(payload["workspacePath"]?.Value<string>()) && !string.IsNullOrWhiteSpace(request.WorkspacePathFallback))
        {
            payload["workspacePath"] = request.WorkspacePathFallback;
        }

        var sessionId = payload["sessionId"]?.Value<string>() ?? job.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new ContinueWorkerResult
            {
                Success = false,
                Mode = ContinueWorkerMode.SpawnedFollowUp,
                JobId = job.JobId,
                Message = "无法解析 worker 所属 session。"
            };
        }

        var newJobId = await _backgroundJobService.CreateJobAsync(
            sessionId,
            job.TaskId,
            job.JobType,
            payload.ToString(Formatting.None)).ConfigureAwait(false);

        return new ContinueWorkerResult
        {
            Success = true,
            Mode = ContinueWorkerMode.SpawnedFollowUp,
            JobId = job.JobId,
            NewJobId = newJobId
        };
    }

    /// <summary>
    /// 停止一个仍可取消的 worker 作业。
    /// </summary>
    /// <param name="jobId">作业标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回停止结果。</returns>
    public async Task<StopWorkerResult> StopWorkerAsync(string jobId, CancellationToken ct = default)
    {
        var job = await _jobs.FindAsync(jobId, ct).ConfigureAwait(false);

        if (job == null)
        {
            return new StopWorkerResult
            {
                Success = false,
                JobId = jobId,
                Message = "worker 不存在。"
            };
        }

        if (!WorkerJobTypes.IsWorkerJobType(job.JobType))
        {
            return new StopWorkerResult
            {
                Success = false,
                JobId = jobId,
                Message = "该 job 不是 worker 作业。"
            };
        }

        var result = await _backgroundJobService.CancelJobAsync(jobId).ConfigureAwait(false);
        return result switch
        {
            BackgroundJobCommandResult.Success or BackgroundJobCommandResult.NoOp => new StopWorkerResult
            {
                Success = true,
                JobId = jobId
            },
            BackgroundJobCommandResult.NotFound => new StopWorkerResult
            {
                Success = false,
                JobId = jobId,
                Message = "worker 不存在。"
            },
            _ => new StopWorkerResult
            {
                Success = false,
                JobId = jobId,
                Message = "当前 worker 状态不允许停止。"
            }
        };
    }

    /// <summary>
    /// 列出指定会话下的全部 worker 摘要。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回按更新时间倒序排列的 worker 摘要列表。</returns>
    public async Task<IReadOnlyList<WorkerJobSummary>> ListWorkersAsync(string sessionId, CancellationToken ct = default)
    {
        var jobs = await _jobViewService.GetSessionJobsAsync(sessionId).ConfigureAwait(false);
        return jobs
            .Where(job => WorkerJobTypes.IsWorkerJobType(job.JobType))
            .OrderByDescending(job => job.UpdatedAtUtc)
            .Select(MapSummary)
            .ToArray();
    }

    /// <summary>
    /// 读取 worker 当前视图和增量事件输出。
    /// </summary>
    /// <param name="request">目标 worker job 与事件序号过滤条件。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回 worker 输出快照。</returns>
    public async Task<WorkerOutputResult> GetWorkerOutputAsync(WorkerOutputRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return new WorkerOutputResult
            {
                Success = false,
                JobId = string.Empty,
                Message = "缺少 job_id。"
            };
        }

        var view = request.IncludeCurrentView
            ? await _jobViewService.GetViewAsync(request.JobId).ConfigureAwait(false)
            : null;

        if (request.IncludeCurrentView && view == null)
        {
            return new WorkerOutputResult
            {
                Success = false,
                JobId = request.JobId,
                Message = "worker 不存在。"
            };
        }

        if (view != null && !WorkerJobTypes.IsWorkerJobType(view.JobType))
        {
            return new WorkerOutputResult
            {
                Success = false,
                JobId = request.JobId,
                Message = "该 job 不是 worker 作业。"
            };
        }

        var maxEvents = Math.Clamp(request.MaxEvents, 1, 200);
        var history = await _jobViewService.GetHistoryAsync(request.JobId, request.AfterSeq).ConfigureAwait(false);
        var events = history
            .OrderBy(entry => entry.Seq)
            .Take(maxEvents)
            .Select(entry => new WorkerOutputEvent
            {
                Seq = entry.Seq,
                EventType = entry.EventType,
                PayloadJson = entry.PayloadJson,
                OccurredAtUtc = entry.OccurredAtUtc
            })
            .ToArray();

        return new WorkerOutputResult
        {
            Success = true,
            JobId = request.JobId,
            View = view == null ? null : MapOutputSnapshot(view),
            Events = events
        };
    }

    /// <summary>
    /// 清理已结束 forge worker 对应的 shadow worktree。
    /// </summary>
    /// <param name="jobId">forge worker 作业标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回清理结果及 worktree 路径。</returns>
    public async Task<CleanupWorkerWorktreeResult> CleanupWorkerWorktreeAsync(string jobId, CancellationToken ct = default)
    {
        var job = await _jobs.FindAsync(jobId, ct).ConfigureAwait(false);

        if (job == null)
        {
            return new CleanupWorkerWorktreeResult
            {
                Success = false,
                JobId = jobId,
                Message = "worker 不存在。"
            };
        }

        if (!WorkerJobTypes.TryResolveWorkerType(job.JobType, out var workerType) || workerType != WorkerType.Forge)
        {
            return new CleanupWorkerWorktreeResult
            {
                Success = false,
                JobId = jobId,
                Message = "仅 forge worker 支持清理 shadow worktree。"
            };
        }

        if (job.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Running or BackgroundJobStatus.WaitingUser or BackgroundJobStatus.FailedRecoveryNeeded)
        {
            return new CleanupWorkerWorktreeResult
            {
                Success = false,
                JobId = jobId,
                Message = $"当前状态 {job.Status} 不允许清理 shadow worktree。"
            };
        }

        var payload = ParsePayload(job.PayloadJson) ?? new JObject();
        var mainWorkspacePath = payload["mainWorkspacePath"]?.Value<string>()
            ?? payload["workspacePath"]?.Value<string>();
        var shadowWorktreePath = payload["shadowWorktreePath"]?.Value<string>();
        var worktreeKey = payload["worktreeKey"]?.Value<string>()
            ?? job.TaskId
            ?? job.JobId;

        if (string.IsNullOrWhiteSpace(mainWorkspacePath) || string.IsNullOrWhiteSpace(worktreeKey))
        {
            return new CleanupWorkerWorktreeResult
            {
                Success = false,
                JobId = jobId,
                WorktreePath = shadowWorktreePath,
                Message = "缺少清理 shadow worktree 所需的路径元数据。"
            };
        }

        var pathExists = !string.IsNullOrWhiteSpace(shadowWorktreePath) && Directory.Exists(shadowWorktreePath);
        if (pathExists && !await _gitService.RemoveShadowWorktreeAsync(mainWorkspacePath, worktreeKey).ConfigureAwait(false))
        {
            return new CleanupWorkerWorktreeResult
            {
                Success = false,
                JobId = jobId,
                WorktreePath = shadowWorktreePath,
                Message = "shadow worktree 清理失败，请检查 git worktree 状态。"
            };
        }

        if (pathExists)
        {
            payload["lastShadowWorktreePath"] = shadowWorktreePath;
        }
        payload.Remove("shadowWorktreePath");
        payload["worktreeRetained"] = false;
        var updatedAtUtc = DateTime.UtcNow;
        payload["worktreeCleanedAtUtc"] = updatedAtUtc;
        var saved = await _workerJobs.TryUpdateWorktreePayloadAsync(
            job, payload.ToString(Formatting.None), updatedAtUtc, ct).ConfigureAwait(false);

        return new CleanupWorkerWorktreeResult
        {
            Success = saved,
            JobId = jobId,
            WorktreePath = shadowWorktreePath,
            Cleaned = pathExists,
            Message = !saved
                ? "作业已更新，请重新检查 shadow worktree 状态。"
                : pathExists ? null : "shadow worktree 已不存在，无需重复清理。"
        };
    }

    private static WorkerJobSummary MapSummary(BackgroundJobDto job)
    {
        _ = WorkerJobTypes.TryResolveWorkerType(job.JobType, out var workerType);
        return new WorkerJobSummary
        {
            JobId = job.JobId,
            WorkerType = workerType.ToString().ToLowerInvariant(),
            Status = job.Status,
            StateKind = job.StateKind,
            TaskId = job.TaskId,
            Summary = job.Summary,
            VerificationVerdict = job.Verification?.Verdict,
            VerificationSummary = job.Verification?.Summary,
            VerificationFocusItems = job.Verification?.Checklist?.FocusItems,
            WaitingReason = job.WaitingReason,
            ResumeToken = job.ResumeToken,
            UpdatedAtUtc = job.UpdatedAtUtc
        };
    }

    private static WorkerOutputSnapshot MapOutputSnapshot(BackgroundJobDto job)
        => new()
        {
            JobId = job.JobId,
            SessionId = job.SessionId,
            TaskId = job.TaskId,
            WorkerType = job.WorkerType,
            StateKind = job.StateKind,
            Status = job.Status,
            Summary = job.Summary,
            LatestMessage = job.LatestMessage,
            LatestSeq = job.LatestSeq,
            WaitingUser = job.WaitingUser,
            WaitingReason = job.WaitingReason,
            ResumeToken = job.ResumeToken,
            RecoveryNeeded = job.RecoveryNeeded,
            RecoveryReason = job.RecoveryReason,
            ResumeStrategy = job.ResumeStrategy,
            ResumeGuidance = job.ResumeGuidance,
            UpdatedAtUtc = job.UpdatedAtUtc
        };

    private static JObject? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JObject.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="failureMessage">
    /// The failure message the host recorded on the job (its ErrorMessage, which is not part of the result), or null when the job did not
    /// fail. A follow-up to a failed job is told that message, the failures of the earlier retries, and that the new job runs with the
    /// same write scope as the one that failed.
    /// </param>
    private static string BuildContinuationPrompt(
        JObject originalPayload,
        string? resultPayloadJson,
        string followUpPrompt,
        WorkerType workerType,
        string? failureMessage = null)
    {
        var originalPrompt = originalPayload["prompt"]?.Value<string>()
            ?? originalPayload["message"]?.Value<string>()
            ?? originalPayload["description"]?.Value<string>();
        var resultPayload = ParsePayload(resultPayloadJson);
        var summary = resultPayload?["summary"]?.Value<string>()
            ?? resultPayload?["message"]?.Value<string>();
        var result = resultPayload?["result"]?.Value<string>();
        var terminationReason = resultPayload?["terminationReason"]?.Value<string>();
        var detailCode = resultPayload?["detailCode"]?.Value<string>();
        var commitHash = resultPayload?["commitHash"]?.Value<string>();
        var changedFiles = resultPayload?["changedFiles"]?
            .Values<string?>()
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"你正在继续之前的 {workerType.ToString().ToLowerInvariant()} worker 工作。");

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

        if (!string.IsNullOrWhiteSpace(terminationReason))
        {
            builder.AppendLine();
            builder.AppendLine($"上次终止原因：{terminationReason.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(detailCode))
        {
            builder.AppendLine();
            builder.AppendLine($"上次 detail code：{detailCode.Trim()}");
        }

        if (failureMessage != null)
        {
            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                builder.AppendLine();
                builder.AppendLine($"上次失败信息：{(failureMessage.Length > 600 ? failureMessage[..600] + "…" : failureMessage.Trim())}");
            }

            // What the failure was, not only what it said: the files the guard refused, the packages that came out wrong, what would change it.
            var failure = CodexFlow.Core.Protocols.JobFailureClassifier.Classify(failureMessage, resultPayloadJson);
            if (failure.RefusedPaths.Count > 0 || failure.Evidence.Count > 0 || !failure.Retryable)
            {
                builder.AppendLine();
                builder.AppendLine("失败的结构化记录：");
                builder.AppendLine(CodexFlow.Core.Protocols.UntrustedText.Wrap("job-failure", failure.ToPromptText()));
            }

            if (originalPayload["failureHistory"] is JArray earlier && earlier.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("更早几次尝试的失败记录：");
                foreach (var entry in earlier.OfType<JObject>())
                {
                    var code = entry["detailCode"]?.Value<string>();
                    builder.AppendLine($"- 第 {entry["attempt"]?.Value<int?>() ?? 0} 次{(string.IsNullOrWhiteSpace(code) ? string.Empty : $"（{code}）")}：{entry["message"]?.Value<string>()}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("注意：新作业的写入范围与上次相同。如果上次失败是因为写入被拒绝或范围不足，换做法也不会改变这一点，请如实报告，不要硬做。");
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

        var appendedRecoveryPlaybook = TryAppendRecoveryPlaybookContext(builder, resultPayload);

        var appendedStructuredVerification = TryAppendStructuredVerificationContext(builder, resultPayload);

        if (!appendedStructuredVerification && !appendedRecoveryPlaybook && !string.IsNullOrWhiteSpace(result))
        {
            builder.AppendLine();
            builder.AppendLine("上次详细结果：");
            builder.AppendLine(result.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("新的追问：");
        builder.AppendLine(followUpPrompt.Trim());
        builder.AppendLine();
        builder.AppendLine("请在延续已有上下文的前提下继续完成该任务。");

        return builder.ToString().Trim();
    }

    private static bool TryAppendRecoveryPlaybookContext(System.Text.StringBuilder builder, JObject? resultPayload)
    {
        if (resultPayload?["recoveryNeeded"]?.Value<bool>() != true)
        {
            return false;
        }

        var recoveryReason = resultPayload["recoveryReason"]?.Value<string>();
        var resumeStrategy = resultPayload["resumeStrategy"]?.Value<string>();
        var guidance = resultPayload["resumeGuidance"]?.Value<string>()
            ?? resultPayload["resumePlaybook"]?["guidance"]?.Value<string>()
            ?? resultPayload["resumePlaybook"]?["nextAction"]?.Value<string>();
        var suggestedPrompt = resultPayload["resumePlaybook"]?["suggestedPrompt"]?.Value<string>();
        var steps = resultPayload["resumePlaybook"]?["steps"]?
            .Values<string?>()
            .Where(static step => !string.IsNullOrWhiteSpace(step))
            .Select(static step => step!.Trim())
            .ToArray()
            ?? [];
        var runtimeFlags = resultPayload["runtimeFlags"]?
            .Values<string?>()
            .Where(static flag => !string.IsNullOrWhiteSpace(flag))
            .Select(static flag => flag!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        if (string.IsNullOrWhiteSpace(recoveryReason)
            && string.IsNullOrWhiteSpace(guidance)
            && steps.Length == 0
            && runtimeFlags.Length == 0
            && string.IsNullOrWhiteSpace(suggestedPrompt))
        {
            return false;
        }

        builder.AppendLine();
        builder.AppendLine("上次恢复剧本：");

        if (!string.IsNullOrWhiteSpace(recoveryReason))
        {
            builder.AppendLine($"恢复原因：{recoveryReason.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(resumeStrategy))
        {
            builder.AppendLine($"恢复方式：{resumeStrategy.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(guidance))
        {
            builder.AppendLine("恢复指引：");
            builder.AppendLine(guidance.Trim());
        }

        if (steps.Length > 0)
        {
            builder.AppendLine("建议步骤：");
            foreach (var step in steps)
            {
                builder.Append("- ");
                builder.AppendLine(step);
            }
        }

        if (runtimeFlags.Length > 0)
        {
            builder.AppendLine("运行时标记：");
            foreach (var flag in runtimeFlags)
            {
                builder.Append("- ");
                builder.AppendLine(flag);
            }
        }

        if (!string.IsNullOrWhiteSpace(suggestedPrompt))
        {
            builder.AppendLine("建议续跑提示：");
            builder.AppendLine(suggestedPrompt.Trim());
        }

        return true;
    }

    private static bool TryAppendStructuredVerificationContext(System.Text.StringBuilder builder, JObject? resultPayload)
    {
        var checklist = resultPayload?["verificationChecklist"] as JObject;
        if (checklist == null)
        {
            return false;
        }

        var completedItems = ExtractVerificationChecklistItems(checklist["completedItems"] as JArray);
        var failedItems = ExtractVerificationChecklistItems(checklist["failedItems"] as JArray);
        var pendingItems = ExtractVerificationChecklistItems(checklist["pendingItems"] as JArray);
        var focusItems = checklist["focusItems"]?
            .Values<string?>()
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
        var issues = resultPayload?["verificationIssues"]?
            .Values<string?>()
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        if (completedItems.Count == 0 && failedItems.Count == 0 && pendingItems.Count == 0 && focusItems.Length == 0 && issues.Length == 0)
        {
            return false;
        }

        builder.AppendLine();
        builder.AppendLine("上次验证收口：");

        if (completedItems.Count > 0)
        {
            builder.AppendLine("以下检查已经有证据支撑，通常不需要重做：");
            AppendVerificationChecklistLines(builder, completedItems);
            builder.AppendLine();
        }

        if (failedItems.Count > 0)
        {
            builder.AppendLine("以下检查当前验证失败，本轮必须优先修复：");
            AppendVerificationChecklistLines(builder, failedItems);
            builder.AppendLine();
        }

        if (pendingItems.Count > 0)
        {
            builder.AppendLine("以下检查仍待补齐证据，本轮必须继续推进：");
            AppendVerificationChecklistLines(builder, pendingItems);
            builder.AppendLine();
        }

        if (focusItems.Length > 0)
        {
            builder.AppendLine("本轮重点目标：");
            foreach (var item in focusItems)
            {
                builder.Append("- ").AppendLine(item);
            }
            builder.AppendLine();
        }

        if (issues.Length > 0)
        {
            builder.AppendLine("验证 issues：");
            foreach (var issue in issues)
            {
                builder.Append("- ").AppendLine(issue);
            }
        }

        return true;
    }

    private static List<(string Check, IReadOnlyList<string> Evidence)> ExtractVerificationChecklistItems(JArray? array)
    {
        if (array == null || array.Count == 0)
        {
            return [];
        }

        return array
            .OfType<JObject>()
            .Select(item => (
                Check: item["check"]?.Value<string>()?.Trim() ?? "unspecified-check",
                Evidence: (IReadOnlyList<string>)(item["evidence"]?
                    .Values<string?>()
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .Select(static line => line!.Trim())
                    .ToArray()
                    ?? [])))
            .ToList();
    }

    private static void AppendVerificationChecklistLines(
        System.Text.StringBuilder builder,
        IReadOnlyList<(string Check, IReadOnlyList<string> Evidence)> items)
    {
        foreach (var item in items)
        {
            builder.Append("- ").Append(item.Check);
            if (item.Evidence.Count > 0)
            {
                builder.Append(" (证据: ").Append(string.Join("; ", item.Evidence)).Append(')');
            }

            builder.AppendLine();
        }
    }
}
