// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CodexFlow.Orchestration.Persistence;

namespace CodexFlow.Services.Background;

/// <summary>
/// Resumes or terminates a background job suspended in <see cref="BackgroundJobStatus.WaitingUser"/>
/// when an interactive execution approval decision is submitted via the control plane.
/// </summary>
public sealed class BackgroundJobApprovalResumer(
    IServiceProvider serviceProvider,
    IOutboxSignal outboxSignal,
    ILogger<BackgroundJobApprovalResumer> logger) : IBackgroundJobApprovalResumer
{
    public async Task<bool> OnSuspendedApprovalResolvedAsync(
        string jobId,
        string resumeToken,
        PendingApprovalItem item,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct)
        => await TryApplyResolutionAsync(jobId, resumeToken, item, submission, ct).ConfigureAwait(false) == SuspendedJobResolutionStatus.Applied;

    public async Task<SuspendedJobResolutionStatus> TryApplyResolutionAsync(
        string jobId,
        string resumeToken,
        PendingApprovalItem item,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeToken);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(submission);
        ct.ThrowIfCancellationRequested();

        await using var scope = serviceProvider.CreateAsyncScope();
        if (submission.Decision != ExecutionApprovalDecisionKind.Deny)
        {
            if (CodexFlow.Core.Remote.RemoteExecutionAmbientContext.IsRemote)
            {
                var originStore = scope.ServiceProvider.GetService<CodexFlow.Core.Remote.IRemoteJobOriginStore>();
                var origin = originStore is null
                    ? null
                    : await originStore.FindJobOriginAsync(jobId, ct).ConfigureAwait(false);
                if (origin is null || origin.Channel != CodexFlow.Core.Remote.ControlChannel.Remote)
                {
                    logger.LogWarning("Blocked remote approval resumption for non-remote background job {JobId}.", jobId);
                    return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
                }
            }

            var jobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            var outcome = await jobService.ResumeJobAsync(jobId, resumeToken, payloadJson: null).ConfigureAwait(false);
            logger.LogInformation(
                "Resumed background job {JobId} after approval {ApprovalId} ({Decision}): outcome={Outcome}",
                jobId,
                item.ApprovalId,
                submission.Decision,
                outcome);
            return outcome switch
            {
                BackgroundJobCommandResult.Success => SuspendedJobResolutionStatus.Applied,
                BackgroundJobCommandResult.NotFound or BackgroundJobCommandResult.Conflict or BackgroundJobCommandResult.NoOp => SuspendedJobResolutionStatus.AlreadyLeftWaitingUser,
                _ => SuspendedJobResolutionStatus.RetryableFailure
            };
        }

        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var job = await store.FindAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != BackgroundJobStatus.WaitingUser)
        {
            return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
        }

        var latestCheckpoint = await store.FindLatestCheckpointAsync(jobId, ct).ConfigureAwait(false);
        if (latestCheckpoint is null || !string.Equals(latestCheckpoint.ResumeToken, resumeToken, StringComparison.Ordinal))
        {
            return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
        }

        var reason = string.IsNullOrWhiteSpace(submission.Note)
            ? "Execution denied by user during approval."
            : $"Execution denied by user: {submission.Note}";

        var payload = OrchestrationPayloadJson.Write(
            ("success", false),
            ("reasonCode", ExecutionReasonCodes.PermissionDenied),
            ("approvalId", item.ApprovalId),
            ("requestDigest", item.RequestDigest),
            ("message", reason),
            ("error", reason));

        job.Status = BackgroundJobStatus.Failed;
        job.WaitingReason = null;
        job.ErrorMessage = reason;
        job.ResultPayloadJson = payload;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.UpdatedAtUtc = DateTime.UtcNow;

        var checkpoint = new JobCheckpoint
        {
            JobId = job.JobId,
            SessionId = job.SessionId,
            TaskId = job.TaskId,
            SequenceNumber = latestCheckpoint.SequenceNumber + 1,
            CurrentStep = "ApprovalDenied",
            Attempt = job.Attempt,
            LastEventSeq = job.LastEventSeq,
            CheckpointData = payload
        };

        var entry = new OutboxEvent
        {
            EventType = "JobFailed",
            JobId = job.JobId,
            SessionId = job.SessionId,
            TaskId = job.TaskId,
            Payload = payload
        };

        if (await store.ApplyTransitionsAsync([new JobTransition(JobTransitionKind.DenyApproval, job,
                BackgroundJobStatus.WaitingUser, job.ClaimToken, job.Attempt, entry, checkpoint, latestCheckpoint.CheckpointId)], ct)
            .ConfigureAwait(false) == 0)
        {
            return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
        }
        outboxSignal.Pulse();
        logger.LogInformation("Terminated suspended background job {JobId} after user denied approval {ApprovalId}.", jobId, item.ApprovalId);
        return SuspendedJobResolutionStatus.Applied;
    }
}
