// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// Manages background job lease acquisition, renewal, release, and expiration recovery.
    /// </summary>
    public class BackgroundJobLeaseService : IBackgroundJobLeaseService
    {
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

        private readonly IJobDbContext _context;
        private readonly IJobLeaseStore _store;
        private readonly IOutboxSignal _outboxSignal;
        private readonly IBackgroundJobSignal _jobSignal;

        /// <summary>
        /// Initializes the service with the provider-neutral lease store. A host with a store of its own for its database
        /// (PostgreSQL has one) registers it and gets the other constructor.
        /// </summary>
        /// <param name="context">Database context used for lease state transitions.</param>
        /// <param name="outboxSignal">Signal used to wake outbox projection after expiration recovery updates.</param>
        /// <param name="jobSignal">Signal used to wake the job supervisor when work is re-queued.</param>
        public BackgroundJobLeaseService(
            IJobDbContext context,
            IOutboxSignal outboxSignal,
            IBackgroundJobSignal jobSignal)
            : this(context, new EfJobLeaseStore(context), outboxSignal, jobSignal)
        {
        }

        /// <summary>
        /// Initializes the service.
        /// </summary>
        /// <param name="context">Database context used for lease state transitions.</param>
        /// <param name="store">Store that performs the atomic claim, renew and release operations.</param>
        /// <param name="outboxSignal">Signal used to wake outbox projection after expiration recovery updates.</param>
        /// <param name="jobSignal">Signal used to wake the job supervisor when work is re-queued.</param>
        public BackgroundJobLeaseService(
            IJobDbContext context,
            IJobLeaseStore store,
            IOutboxSignal outboxSignal,
            IBackgroundJobSignal jobSignal)
        {
            _context = context;
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

            return await EnsureClaimIsRunnableAsync(jobId, claimToken, now, ct).ConfigureAwait(false)
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
            var expiredJobs = await _context.BackgroundJobs
                .Where(job => job.Status == BackgroundJobStatus.Running
                    && job.LeaseExpiresAtUtc != null
                    && job.LeaseExpiresAtUtc < now)
                .ToListAsync();

            if (expiredJobs.Count == 0)
            {
                return;
            }

            foreach (var job in expiredJobs)
            {
                var previousLeaseExpiry = job.LeaseExpiresAtUtc;
                job.ClaimToken = null;
                job.LeaseExpiresAtUtc = null;
                job.RunnerId = null;
                job.UpdatedAtUtc = now;

                if (WorkerJobTypes.IsWorkerJobType(job.JobType))
                {
                    var workerType = ResolveWorkerType(job.JobType);
                    var playbook = WorkerRecoveryPlaybookFactory.Create(
                        recoveryReason: "lease_expired",
                        workerType: workerType);

                    job.Status = BackgroundJobStatus.FailedRecoveryNeeded;
                    job.ErrorMessage = "Worker lease expired before completion.";
                    job.FinishedAtUtc ??= now;
                    job.NotificationStatus = NotificationStatus.Pending;

                    _context.OutboxEvents.Add(new OutboxEvent
                    {
                        EventType = "JobRecoveryNeeded",
                        JobId = job.JobId,
                        SessionId = job.SessionId,
                        TaskId = job.TaskId,
                        Payload = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            recoveryNeeded = true,
                            canResume = true,
                            resumeStrategy = "continue_worker",
                            recoveryReason = "lease_expired",
                            resumeGuidance = WorkerRecoveryPlaybookFactory.BuildGuidanceSummary(playbook),
                            resumePlaybook = playbook,
                            workerType,
                            message = "Worker lease expired before completion.",
                            summary = "Worker lease expired before completion.",
                            leaseExpiredAtUtc = previousLeaseExpiry,
                            heartbeatAtUtc = job.HeartbeatAtUtc
                        })
                    });
                }
                else
                {
                    job.Status = BackgroundJobStatus.Queued;
                    job.ErrorMessage = null;
                    job.FinishedAtUtc = null;
                }
            }

            await _context.SaveChangesAsync();
            _outboxSignal.Pulse();
            _jobSignal.Pulse();
        }

        private static string ResolveWorkerType(string jobType)
        {
            return WorkerJobTypes.TryResolveWorkerType(jobType, out var workerType)
                ? workerType.ToString().ToLowerInvariant()
                : jobType.Trim().ToLowerInvariant();
        }

        private async Task<bool> EnsureClaimIsRunnableAsync(
            string jobId,
            string claimToken,
            DateTime now,
            CancellationToken ct)
        {
            var job = await _context.BackgroundJobs
                .FirstOrDefaultAsync(
                    x => x.JobId == jobId
                        && x.ClaimToken == claimToken
                        && x.Status == BackgroundJobStatus.Running,
                    ct)
                .ConfigureAwait(false);
            if (job == null)
            {
                return false;
            }

            if (EfJobLeaseStore.IsRunnableAt(job, now))
            {
                return true;
            }

            var retryNotBeforeUtc = EfJobLeaseStore.GetRetryNotBeforeUtc(job.PayloadJson);
            if (retryNotBeforeUtc.HasValue && retryNotBeforeUtc.Value.UtcDateTime > job.CreatedAtUtc)
            {
                job.CreatedAtUtc = retryNotBeforeUtc.Value.UtcDateTime;
            }
            job.Status = BackgroundJobStatus.Queued;
            job.RunnerId = null;
            job.ClaimToken = null;
            job.LeaseExpiresAtUtc = null;
            job.DequeuedAtUtc = null;
            job.StartedAtUtc = null;
            job.FinishedAtUtc = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
            return false;
        }
    }
}
