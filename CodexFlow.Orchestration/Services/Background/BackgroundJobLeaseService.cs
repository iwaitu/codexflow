// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Persistence;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// Manages background job lease acquisition, renewal, release, and expiration recovery.
    /// </summary>
    public class BackgroundJobLeaseService : IBackgroundJobLeaseService
    {
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

        private readonly IJobLeaseRecoveryStore _recoveryStore;
        private readonly IJobLeaseStore _store;
        private readonly IOutboxSignal _outboxSignal;
        private readonly IBackgroundJobSignal _jobSignal;

        /// <summary>Initializes the service with the host's claim and recovery stores.</summary>
        public BackgroundJobLeaseService(
            IJobLeaseStore store,
            IJobLeaseRecoveryStore recoveryStore,
            IOutboxSignal outboxSignal,
            IBackgroundJobSignal jobSignal)
        {
            _recoveryStore = recoveryStore;
            _store = store;
            _outboxSignal = outboxSignal;
            _jobSignal = jobSignal;
        }

        /// <summary>
        /// Attempts to claim the next runnable background job.
        /// </summary>
        /// <param name="runnerId">Identifier of the runner claiming the job.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The claimed job identifier and claim token, or <see langword="null"/> when no job is available.</returns>
        public async Task<(string JobId, string ClaimToken)?> TryClaimNextAsync(string runnerId, CancellationToken ct)
        {
            var claimToken = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            var jobId = await _store.TryClaimAsync(runnerId, claimToken, now, now.Add(LeaseDuration), ct).ConfigureAwait(false);
            if (jobId == null)
            {
                return null;
            }

            return await _recoveryStore.VerifyOrDeferClaimAsync(jobId, claimToken, now, ct).ConfigureAwait(false)
                ? (jobId, claimToken)
                : null;
        }

        /// <summary>
        /// Renews the lease for a running job when the claim token still matches.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="claimToken">Current claim token issued to the runner.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><see langword="true"/> when the lease is renewed; otherwise <see langword="false"/>.</returns>
        public Task<bool> RenewLeaseAsync(string jobId, string claimToken, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return _store.RenewAsync(jobId, claimToken, now, now.Add(LeaseDuration), ct);
        }

        /// <summary>
        /// Releases a job lease without changing the job's status.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="claimToken">Claim token currently associated with the runner.</param>
        public Task ReleaseAsync(string jobId, string claimToken)
            => _store.ReleaseAsync(jobId, claimToken, DateTime.UtcNow);

        /// <summary>
        /// Detects expired running jobs and either requeues them or marks worker jobs as recovery-needed.
        /// </summary>
        public async Task MarkExpiredJobsAsync()
        {
            var now = DateTime.UtcNow;
            var expired = await _recoveryStore.ReadExpiredAsync(now, CancellationToken.None).ConfigureAwait(false);
            var recoveries = expired.Select(CreateRecovery).ToArray();
            if (recoveries.Length > 0 &&
                await _recoveryStore.RecoverExpiredAsync(recoveries, now, CancellationToken.None).ConfigureAwait(false))
            {
                _outboxSignal.Pulse();
                _jobSignal.Pulse();
            }
        }

        private static ExpiredJobLeaseRecovery CreateRecovery(ExpiredJobLease lease)
        {
            if (!WorkerJobTypes.IsWorkerJobType(lease.JobType))
            {
                return new ExpiredJobLeaseRecovery(lease, null, null);
            }

            var workerType = ResolveWorkerType(lease.JobType);
            var playbook = WorkerRecoveryPlaybookFactory.Create(
                recoveryReason: "lease_expired",
                workerType: workerType);
            const string errorMessage = "Worker lease expired before completion.";
            var entry = new OutboxEvent
            {
                EventType = "JobRecoveryNeeded",
                JobId = lease.JobId,
                SessionId = lease.SessionId,
                TaskId = lease.TaskId,
                Payload = OrchestrationPayloadJson.Write(
                    ("success", false),
                    ("recoveryNeeded", true),
                    ("canResume", true),
                    ("resumeStrategy", "continue_worker"),
                    ("recoveryReason", "lease_expired"),
                    ("resumeGuidance", WorkerRecoveryPlaybookFactory.BuildGuidanceSummary(playbook)),
                    ("resumePlaybook", playbook),
                    ("workerType", workerType),
                    ("message", errorMessage),
                    ("summary", errorMessage),
                    ("leaseExpiredAtUtc", lease.ExpiresAtUtc),
                    ("heartbeatAtUtc", lease.HeartbeatAtUtc))
            };

            return new ExpiredJobLeaseRecovery(lease, errorMessage, entry);
        }

        private static string ResolveWorkerType(string jobType)
        {
            return WorkerJobTypes.TryResolveWorkerType(jobType, out var workerType)
                ? workerType.ToString().ToLowerInvariant()
                : jobType.Trim().ToLowerInvariant();
        }
    }
}
