// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs
{
    public enum BackgroundJobCommandResult
    {
        Success = 0,
        NotFound = 1,
        Conflict = 2,
        NoOp = 3
    }

    /// <summary>
    /// What a retry came to. A refusal says why in words (<see cref="Reason"/>) and in a code a program can act on
    /// (<see cref="ReasonCode"/>); a retry that was queued says what the worker is being told about the last failure.
    /// </summary>
    public sealed record RetryOutcome(
        BackgroundJobCommandResult Result,
        string? ReasonCode = null,
        string? Reason = null,
        string? FailureSummary = null);

    public interface IBackgroundJobService
    {
        Task<string> CreateJobAsync(string sessionId, string? taskId, string jobType, string payloadJson, bool startRunning = false);
        Task<BackgroundJobStatus?> GetJobStatusAsync(string jobId);
        Task<BackgroundJobCommandResult> RetryJobAsync(string jobId);

        /// <summary>
        /// <see cref="RetryJobAsync"/> that carries the reason the job failed to the worker, says why when it refuses, and takes
        /// the caller's guidance for the worker. An implementation that does not do that falls back to the plain retry.
        /// </summary>
        async Task<RetryOutcome> RetryJobDetailedAsync(string jobId, string? guidance = null)
            => new(await RetryJobAsync(jobId).ConfigureAwait(false));
        Task<BackgroundJobCommandResult> CancelJobAsync(string jobId);
        Task<BackgroundJobCommandResult> ResumeJobAsync(string jobId, string? resumeToken, string? payloadJson);
        Task<int> DeleteSessionJobRecordsAsync(string sessionId);

        /// <summary>
        /// 取消指定会话下所有活跃（非终止态）的后台作业。
        /// 返回实际取消的作业数量。
        /// </summary>
        Task<int> CancelSessionJobsAsync(string sessionId);
    }
}
