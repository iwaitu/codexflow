// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

/// <summary>Commits an owned runner change and its checkpoint and outbox event in one transaction.</summary>
public interface IJobRunnerStore
{
    /// <returns>False if the job owner, attempt, status, or expected checkpoint sequence changed.</returns>
    Task<bool> TryCommitAsync(string jobId, JobRunnerFields before, JobRunnerFields after,
        JobCheckpoint? checkpoint, OutboxEvent? entry, CancellationToken ct);
}

/// <summary>Only columns the runner is allowed to write. Other columns may change concurrently.</summary>
public sealed record JobRunnerFields(
    BackgroundJobStatus Status,
    string PayloadJson,
    string? ResultPayloadJson,
    string? ErrorMessage,
    string? RunnerId,
    string? ClaimToken,
    DateTime? LeaseExpiresAtUtc,
    DateTime? DequeuedAtUtc,
    int Attempt,
    string? WaitingReason,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    DateTime UpdatedAtUtc,
    NotificationStatus? NotificationStatus)
{
    public static JobRunnerFields Capture(BackgroundJob job) => new(
        job.Status, job.PayloadJson, job.ResultPayloadJson, job.ErrorMessage, job.RunnerId, job.ClaimToken,
        job.LeaseExpiresAtUtc, job.DequeuedAtUtc, job.Attempt, job.WaitingReason, job.CreatedAtUtc,
        job.StartedAtUtc, job.FinishedAtUtc, job.UpdatedAtUtc, job.NotificationStatus);

    internal void ApplyTo(BackgroundJob job)
    {
        job.Status = Status;
        job.PayloadJson = PayloadJson;
        job.ResultPayloadJson = ResultPayloadJson;
        job.ErrorMessage = ErrorMessage;
        job.RunnerId = RunnerId;
        job.ClaimToken = ClaimToken;
        job.LeaseExpiresAtUtc = LeaseExpiresAtUtc;
        job.DequeuedAtUtc = DequeuedAtUtc;
        job.Attempt = Attempt;
        job.WaitingReason = WaitingReason;
        job.CreatedAtUtc = CreatedAtUtc;
        job.StartedAtUtc = StartedAtUtc;
        job.FinishedAtUtc = FinishedAtUtc;
        job.UpdatedAtUtc = UpdatedAtUtc;
        job.NotificationStatus = NotificationStatus;
    }
}
