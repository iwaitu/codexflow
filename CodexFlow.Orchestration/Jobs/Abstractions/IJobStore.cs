// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;

namespace CodexFlow.Application.Jobs;

public interface IJobStore
{
    /// <summary>Commits the job, its initial checkpoint, and its creation event together.</summary>
    Task CreateAsync(BackgroundJob job, JobCheckpoint checkpoint, OutboxEvent entry, CancellationToken ct = default);

    Task<BackgroundJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default);
    Task<BackgroundJob?> FindAsync(string jobId, CancellationToken ct = default);
    Task<JobCheckpoint?> FindLatestCheckpointAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<BackgroundJob>> ReadActiveAsync(string sessionId, CancellationToken ct = default);
    Task<int> ApplyTransitionsAsync(IReadOnlyList<JobTransition> transitions, CancellationToken ct = default);
    Task<JobDeletionResult> DeleteBySessionAsync(string sessionId, CancellationToken ct = default);
    Task<BackgroundJob?> FindOwnedJobAsync(string jobId, string userId, CancellationToken ct);
    Task<SessionRecord?> FindOwnedSessionAsync(string sessionId, string userId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSessionIdsAsync(string userId, CancellationToken ct);
}

public enum JobTransitionKind { Retry, Cancel, Resume, DenyApproval }

/// <summary>A conditional state mutation and the records to commit with it.</summary>
public sealed record JobTransition(
    JobTransitionKind Kind,
    BackgroundJob UpdatedJob,
    BackgroundJobStatus ExpectedStatus,
    string? ExpectedClaimToken,
    int ExpectedAttempt,
    OutboxEvent Event,
    JobCheckpoint? Checkpoint = null,
    string? ExpectedCheckpointId = null);

public sealed record JobDeletionResult(IReadOnlyList<string> JobIds, bool Changed);
