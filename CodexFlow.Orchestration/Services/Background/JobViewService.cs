// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Core.Models.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// 后台作业只读视图服务。
    /// </summary>
    /// <remarks>
    /// 该服务优先从 Redis 热视图读取当前状态，并在必要时回退到数据库或 Mongo 历史表。
    /// </remarks>
    public class JobViewService : IJobViewService
    {
        private readonly IJobReadStore _jobs;
        private readonly IJobHotView _hotView;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// 初始化作业视图服务，热视图由 <paramref name="hotView"/> 提供（可以为空缓存）。
        /// </summary>
        public JobViewService(IJobReadStore jobs, IJobHotView hotView, IServiceProvider serviceProvider)
        {
            _jobs = jobs;
            _hotView = hotView;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 获取单个后台作业的当前视图。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <returns>返回包含状态、恢复信息和验证摘要的视图；不存在时返回 <see langword="null"/>。</returns>
        public async Task<BackgroundJobDto?> GetViewAsync(string jobId)
        {
            // Try Redis first
            var redisData = await _hotView.GetAsync(jobId);
            var dbSnapshotRaw = await _jobs.FindViewAsync(jobId);

            var dbSnapshot = dbSnapshotRaw == null
                ? null
                : new
                {
                    dbSnapshotRaw.JobId,
                    dbSnapshotRaw.SessionId,
                    dbSnapshotRaw.TaskId,
                    dbSnapshotRaw.JobType,
                    dbSnapshotRaw.RunnerId,
                    dbSnapshotRaw.Status,
                    dbSnapshotRaw.UpdatedAtUtc,
                    dbSnapshotRaw.WaitingUser,
                    dbSnapshotRaw.WaitingReason,
                    dbSnapshotRaw.ResumeToken,
                    dbSnapshotRaw.LatestCheckpointStep,
                    dbSnapshotRaw.LatestSeq,
                    WorkerType = WorkerProjectionHelpers.ResolveWorkerType(dbSnapshotRaw.JobType),
                    StateKind = WorkerProjectionHelpers.ResolveStateKind(
                        dbSnapshotRaw.Status,
                        dbSnapshotRaw.ResultPayloadJson,
                        dbSnapshotRaw.Attempt,
                        dbSnapshotRaw.LatestCheckpointStep),
                    RecoveryNeeded = WorkerProjectionHelpers.ResolveRecoveryNeeded(dbSnapshotRaw.Status, dbSnapshotRaw.ResultPayloadJson),
                    RecoveryReason = WorkerProjectionHelpers.ResolveRecoveryReason(dbSnapshotRaw.ResultPayloadJson),
                    ResumeStrategy = WorkerProjectionHelpers.ResolveResumeStrategy(dbSnapshotRaw.ResultPayloadJson),
                    ResumeGuidance = WorkerProjectionHelpers.ResolveResumeGuidance(dbSnapshotRaw.ResultPayloadJson),
                    Verification = WorkerProjectionHelpers.ResolveVerification(dbSnapshotRaw.ResultPayloadJson),
                    Failure = WorkerProjectionHelpers.ResolveFailure(dbSnapshotRaw.Status, dbSnapshotRaw.ErrorMessage, dbSnapshotRaw.ResultPayloadJson),
                    Summary = WorkerProjectionHelpers.BuildSummary(
                        dbSnapshotRaw.Status,
                        dbSnapshotRaw.PayloadJson,
                        dbSnapshotRaw.ResultPayloadJson,
                        dbSnapshotRaw.ShortSummary,
                        dbSnapshotRaw.WaitingReason,
                        dbSnapshotRaw.ErrorMessage)
                };
            
            if (redisData.Count > 0)
            {
                var dict = redisData;
                var redisStatus = dict.GetValueOrDefault("Status", dbSnapshot?.Status ?? "Queued");
                var preferDbTerminalStatus = IsTerminalStatus(dbSnapshot?.Status) && !IsTerminalStatus(redisStatus);
                var effectiveStatus = preferDbTerminalStatus ? dbSnapshot!.Status : redisStatus;
                var effectiveStateKind = IsTerminalStatus(effectiveStatus)
                    ? WorkerProjectionHelpers.ResolveStateKind(effectiveStatus, dbSnapshot?.RecoveryNeeded == true ? dbSnapshotRaw?.ResultPayloadJson : null)
                    : dict.GetValueOrDefault("StateKind") ?? dbSnapshot?.StateKind;
                var effectiveWaitingUser = !IsTerminalStatus(effectiveStatus)
                    && (bool.TryParse(dict.GetValueOrDefault("WaitingUser"), out var waitingUser)
                        ? waitingUser
                        : string.Equals(effectiveStatus, "WaitingUser", StringComparison.Ordinal)
                          || dbSnapshot?.WaitingUser == true);
                var effectiveRecoveryNeeded = string.Equals(effectiveStatus, "FailedRecoveryNeeded", StringComparison.Ordinal)
                    || (!IsTerminalStatus(effectiveStatus)
                        && (bool.TryParse(dict.GetValueOrDefault("RecoveryNeeded"), out var recoveryNeeded)
                            ? recoveryNeeded
                            : dbSnapshot?.RecoveryNeeded == true));
                var redisLatestSeq = long.TryParse(dict.GetValueOrDefault("LatestSeq"), out var seq)
                    ? seq
                    : 0;

                return new BackgroundJobDto
                {
                    JobId = jobId,
                    SessionId = dict.GetValueOrDefault("SessionId") ?? dbSnapshot?.SessionId,
                    TaskId = dict.GetValueOrDefault("TaskId") ?? dbSnapshot?.TaskId,
                    RunnerId = dict.GetValueOrDefault("RunnerId") ?? dbSnapshot?.RunnerId,
                    WorkerType = dict.GetValueOrDefault("WorkerType") ?? dbSnapshot?.WorkerType,
                    StateKind = effectiveStateKind,
                    Summary = dict.GetValueOrDefault("Summary") ?? dbSnapshot?.Summary,
                    WaitingUser = effectiveWaitingUser,
                    WaitingReason = effectiveWaitingUser ? dict.GetValueOrDefault("WaitingReason") ?? dbSnapshot?.WaitingReason : null,
                    RecoveryNeeded = effectiveRecoveryNeeded,
                    RecoveryReason = effectiveRecoveryNeeded ? dict.GetValueOrDefault("RecoveryReason") ?? dbSnapshot?.RecoveryReason : null,
                    ResumeStrategy = effectiveRecoveryNeeded ? dict.GetValueOrDefault("ResumeStrategy") ?? dbSnapshot?.ResumeStrategy : null,
                    ResumeGuidance = effectiveRecoveryNeeded ? dict.GetValueOrDefault("ResumeGuidance") ?? dbSnapshot?.ResumeGuidance : null,
                    Verification = dbSnapshot?.Verification,
                    Failure = IsTerminalStatus(effectiveStatus) ? dbSnapshot?.Failure : null,
                    Status = effectiveStatus,
                    LatestSeq = Math.Max(redisLatestSeq, dbSnapshot?.LatestSeq ?? 0),
                    LatestMessage = dict.GetValueOrDefault("LatestMessage") ?? dict.GetValueOrDefault("Summary") ?? dbSnapshot?.Summary,
                    ResumeToken = effectiveWaitingUser || effectiveRecoveryNeeded
                        ? dict.GetValueOrDefault("ResumeToken") ?? dbSnapshot?.ResumeToken
                        : null,
                    JobType = dict.GetValueOrDefault("JobType") ?? dbSnapshot?.JobType ?? string.Empty,
                    UpdatedAtUtc = DateTime.TryParse(dict.GetValueOrDefault("UpdatedAtUtc"), out var dt)
                        ? dt
                        : dbSnapshot?.UpdatedAtUtc ?? DateTime.UtcNow
                };
            }

            // Fallback to DB
            var job = dbSnapshot == null
                ? null
                : new BackgroundJobDto
                {
                    JobId = dbSnapshot.JobId,
                    SessionId = dbSnapshot.SessionId,
                    TaskId = dbSnapshot.TaskId,
                    RunnerId = dbSnapshot.RunnerId,
                    JobType = dbSnapshot.JobType,
                    WorkerType = dbSnapshot.WorkerType,
                    StateKind = dbSnapshot.StateKind,
                    Summary = dbSnapshot.Summary,
                    Status = dbSnapshot.Status,
                    UpdatedAtUtc = dbSnapshot.UpdatedAtUtc,
                    WaitingUser = dbSnapshot.WaitingUser,
                    WaitingReason = dbSnapshot.WaitingReason,
                    RecoveryNeeded = dbSnapshot.RecoveryNeeded,
                    RecoveryReason = dbSnapshot.RecoveryReason,
                    ResumeStrategy = dbSnapshot.ResumeStrategy,
                    ResumeGuidance = dbSnapshot.ResumeGuidance,
                    Verification = dbSnapshot.Verification,
                    Failure = dbSnapshot.Failure,
                    ResumeToken = dbSnapshot.ResumeToken,
                    LatestSeq = dbSnapshot.LatestSeq,
                    LatestMessage = dbSnapshot.Summary
                };

            return job;
        }

        private static bool IsTerminalStatus(string? status)
            => status is "Completed" or "Failed" or "Cancelled" or "FailedRecoveryNeeded";

        /// <summary>
        /// 获取指定会话下的全部后台作业视图。
        /// </summary>
        /// <param name="sessionId">会话标识。</param>
        /// <returns>返回按创建时间倒序排列的后台作业列表。</returns>
        public async Task<IEnumerable<BackgroundJobDto>> GetSessionJobsAsync(string sessionId)
        {
            // For listing, we usually hit DB as Redis doesn't maintain a full secondary index for sessions in MVP
            // unless we specifically build one.
            var jobs = await _jobs.ReadSessionViewsAsync(sessionId);

            return jobs.Select(job => new BackgroundJobDto
            {
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                RunnerId = job.RunnerId,
                JobType = job.JobType,
                WorkerType = WorkerProjectionHelpers.ResolveWorkerType(job.JobType),
                StateKind = WorkerProjectionHelpers.ResolveStateKind(
                    job.Status,
                    job.ResultPayloadJson,
                    job.Attempt,
                    job.LatestCheckpointStep),
                Summary = WorkerProjectionHelpers.BuildSummary(
                    job.Status,
                    job.PayloadJson,
                    job.ResultPayloadJson,
                    job.ShortSummary,
                    job.WaitingReason,
                    job.ErrorMessage),
                Status = job.Status,
                UpdatedAtUtc = job.UpdatedAtUtc,
                WaitingUser = job.WaitingUser,
                WaitingReason = job.WaitingReason,
                RecoveryNeeded = WorkerProjectionHelpers.ResolveRecoveryNeeded(job.Status, job.ResultPayloadJson),
                RecoveryReason = WorkerProjectionHelpers.ResolveRecoveryReason(job.ResultPayloadJson),
                ResumeStrategy = WorkerProjectionHelpers.ResolveResumeStrategy(job.ResultPayloadJson),
                ResumeGuidance = WorkerProjectionHelpers.ResolveResumeGuidance(job.ResultPayloadJson),
                Verification = WorkerProjectionHelpers.ResolveVerification(job.ResultPayloadJson),
                Failure = WorkerProjectionHelpers.ResolveFailure(job.Status, job.ErrorMessage, job.ResultPayloadJson),
                ResumeToken = job.ResumeToken,
                LatestSeq = job.LatestSeq,
                LatestMessage = WorkerProjectionHelpers.BuildSummary(
                    job.Status,
                    job.PayloadJson,
                    job.ResultPayloadJson,
                    job.ShortSummary,
                    job.WaitingReason,
                    job.ErrorMessage)
            });
        }

        /// <summary>
        /// 获取指定后台作业的事件历史。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <param name="afterSeq">可选的序号下界，仅返回更大的事件。</param>
        /// <returns>返回事件历史流。</returns>
        public async Task<IEnumerable<JobEventLogEntry>> GetHistoryAsync(string jobId, long? afterSeq = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var eventLog = scope.ServiceProvider.GetRequiredService<IJobEventLog>();
            return await eventLog.GetByJobAsync(jobId, afterSeq);
        }
    }
}
