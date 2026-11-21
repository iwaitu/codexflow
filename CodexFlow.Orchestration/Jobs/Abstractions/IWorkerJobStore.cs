// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

/// <summary>Persists worker continuation, worktree metadata, and artifact verification state.</summary>
public interface IWorkerJobStore
{
    Task<WorkerContinuationState?> FindContinuationAsync(string jobId, CancellationToken ct);
    Task<IReadOnlyList<BackgroundJob>> ReadSessionJobsAsync(string sessionId, CancellationToken ct);

    /// <summary>Updates worktree metadata if the previously read job and its payload still match.</summary>
    Task<bool> TryUpdateWorktreePayloadAsync(BackgroundJob expected, string payloadJson, DateTime updatedAtUtc, CancellationToken ct);

    /// <summary>Updates the completed attempt if its result payload has not changed.</summary>
    Task<bool> TryUpdateVerificationPayloadAsync(
        BackgroundJob expected, string payloadJson, string? errorMessage, DateTime updatedAtUtc, CancellationToken ct);
}

public sealed record WorkerContinuationState(BackgroundJob Job, string? ResumeToken);
