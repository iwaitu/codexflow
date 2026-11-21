// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

/// <summary>Validates acquired claims and persists recovery of expired leases.</summary>
public interface IJobLeaseRecoveryStore
{
    /// <summary>Confirms an eligible claim, or requeues it if the job is still subject to a retry delay.</summary>
    Task<bool> VerifyOrDeferClaimAsync(string jobId, string claimToken, DateTime now, CancellationToken ct);

    Task<IReadOnlyList<ExpiredJobLease>> ReadExpiredAsync(DateTime now, CancellationToken ct);

    /// <summary>Commits matching lease transitions and their outbox events as one transaction.</summary>
    Task<bool> RecoverExpiredAsync(IReadOnlyList<ExpiredJobLeaseRecovery> recoveries, DateTime now, CancellationToken ct);
}

public sealed record ExpiredJobLease(
    string JobId, string JobType, string? SessionId, string? TaskId, string? ClaimToken,
    DateTime ExpiresAtUtc, DateTime? HeartbeatAtUtc);

/// <summary>An outbox event accompanies worker recovery. Other expired jobs return to the queue.</summary>
public sealed record ExpiredJobLeaseRecovery(ExpiredJobLease Lease, string? ErrorMessage, OutboxEvent? Event);
