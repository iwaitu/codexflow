// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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

        var dbContext = scope.ServiceProvider.GetRequiredService<IJobDbContext>();
        var job = await dbContext.BackgroundJobs.FindAsync([jobId], ct).ConfigureAwait(false);
        if (job is null || job.Status != BackgroundJobStatus.WaitingUser)
        {
            return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
        }

        var latestCheckpoint = await dbContext.JobCheckpoints
            .Where(c => c.JobId == jobId)
            .OrderByDescending(c => c.SequenceNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latestCheckpoint is null || !string.Equals(latestCheckpoint.ResumeToken, resumeToken, StringComparison.Ordinal))
        {
            return SuspendedJobResolutionStatus.AlreadyLeftWaitingUser;
        }

        var reason = string.IsNullOrWhiteSpace(submission.Note)
            ? "Execution denied by user during approval."
            : $"Execution denied by user: {submission.Note}";

        var payload = JsonConvert.SerializeObject(new
        {
            success = false,
            reasonCode = ExecutionReasonCodes.PermissionDenied,
            approvalId = item.ApprovalId,
            requestDigest = item.RequestDigest,
            message = reason,
            error = reason
        });

        job.Status = BackgroundJobStatus.Failed;
        job.WaitingReason = null;
        job.ErrorMessage = reason;
        job.ResultPayloadJson = payload;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.UpdatedAtUtc = DateTime.UtcNow;

        dbContext.JobCheckpoints.Add(new JobCheckpoint
        {
            JobId = job.JobId,
            SessionId = job.SessionId,
            TaskId = job.TaskId,
            SequenceNumber = latestCheckpoint.SequenceNumber + 1,
            CurrentStep = "ApprovalDenied",
            Attempt = job.Attempt,
            LastEventSeq = job.LastEventSeq,
            CheckpointData = payload
        });

        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            EventType = "JobFailed",
            JobId = job.JobId,
            SessionId = job.SessionId,
            TaskId = job.TaskId,
            Payload = payload
        });

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        outboxSignal.Pulse();
        logger.LogInformation("Terminated suspended background job {JobId} after user denied approval {ApprovalId}.", jobId, item.ApprovalId);
        return SuspendedJobResolutionStatus.Applied;
    }
}
