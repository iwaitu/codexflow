// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

/// <summary>Read projections used by job views, worker dispatch, and artifact lineage.</summary>
public interface IJobReadStore
{
    Task<JobViewSnapshot?> FindViewAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobViewSnapshot>> ReadSessionViewsAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<string?>> ReadCompletedResultPayloadsAsync(
        string sessionId, string excludingJobId, CancellationToken ct);
    Task<ActivePlanJob?> FindActiveTaskJobAsync(string sessionId, string taskId, string jobType, CancellationToken ct);
    Task<ActivePlanJob?> FindActiveSessionWorkerAsync(string sessionId, CancellationToken ct);
}

public sealed record JobViewSnapshot(
    string JobId, string? SessionId, string? TaskId, string JobType, string? RunnerId,
    int Attempt, BackgroundJobStatus StatusCode, DateTime UpdatedAtUtc, string PayloadJson,
    string? ResultPayloadJson, string? ShortSummary, string? ErrorMessage, string? WaitingReason,
    string? ResumeToken, string? LatestCheckpointStep, long LatestSeq)
{
    public string Status => StatusCode.ToString();
    public bool WaitingUser => StatusCode == BackgroundJobStatus.WaitingUser;
}

public sealed record ActivePlanJob(
    string JobId, string? TaskId, string JobType, BackgroundJobStatus Status,
    string? ResultPayloadJson, int Attempt, string? LatestCheckpointStep);
