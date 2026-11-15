// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Application.Jobs
{
    /// <summary>
    /// The atomic, storage-specific part of a background-job lease: claiming a runnable job and
    /// keeping or giving up that claim. Everything that is not storage specific (verifying a claim,
    /// recovering expired leases, signalling) stays in <see cref="IBackgroundJobLeaseService"/>.
    /// </summary>
    /// <remarks>
    /// Each method must be atomic with respect to other runners using the same store: two callers
    /// must never both obtain the same job from <see cref="TryClaimAsync"/>.
    /// </remarks>
    public interface IJobLeaseStore
    {
        /// <summary>
        /// Claims the oldest runnable job: a queued job, or a running job whose lease has expired,
        /// whose <c>retryNotBeforeUtc</c> payload field (if any) is not in the future.
        /// The claimed job becomes <c>Running</c> and carries the runner id, claim token and lease expiry.
        /// </summary>
        /// <returns>The claimed job id, or <see langword="null"/> when nothing is runnable.</returns>
        Task<string?> TryClaimAsync(
            string runnerId,
            string claimToken,
            DateTime now,
            DateTime leaseExpiresAtUtc,
            CancellationToken ct);

        /// <summary>
        /// Extends the lease and records a heartbeat when the claim token still matches a running job.
        /// </summary>
        /// <returns><see langword="true"/> when a job matched and was updated.</returns>
        Task<bool> RenewAsync(
            string jobId,
            string claimToken,
            DateTime now,
            DateTime leaseExpiresAtUtc,
            CancellationToken ct);

        /// <summary>
        /// Clears the runner id, claim token and lease expiry of a running job when the claim token still
        /// matches. The job status is not changed.
        /// </summary>
        Task ReleaseAsync(string jobId, string claimToken, DateTime now);
    }
}
