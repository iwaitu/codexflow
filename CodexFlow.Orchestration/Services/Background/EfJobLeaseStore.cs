// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// <see cref="CodexFlow.Application.Jobs.IJobLeaseStore"/> that uses plain EF queries and updates and no
    /// provider-specific SQL. It is not atomic across contexts: a claim reads the oldest runnable job and then
    /// writes it. That is enough for a single host process (and for the EF in-memory provider the tests use);
    /// it must not be shared by several host instances. Use <see cref="PostgresJobLeaseStore"/> for that.
    /// Claims inside one process are serialized with a lock so two runners cannot take the same job.
    /// </summary>
    public sealed class EfJobLeaseStore : CodexFlow.Application.Jobs.IJobLeaseStore
    {
        private static readonly SemaphoreSlim ClaimLock = new(1, 1);

        private readonly IJobDbContext _context;

        public EfJobLeaseStore(IJobDbContext context)
        {
            _context = context;
        }

        /// <inheritdoc />
        public async Task<string?> TryClaimAsync(
            string runnerId,
            string claimToken,
            DateTime now,
            DateTime leaseExpiresAtUtc,
            CancellationToken ct)
        {
            await ClaimLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var candidates = await _context.BackgroundJobs
                    .Where(job =>
                        job.Status == BackgroundJobStatus.Queued
                        || (job.Status == BackgroundJobStatus.Running
                            && job.LeaseExpiresAtUtc != null
                            && job.LeaseExpiresAtUtc < now))
                    .OrderBy(job => job.CreatedAtUtc)
                    .ToListAsync(ct);
                var job = candidates.FirstOrDefault(job => IsRunnableAt(job, now));

                if (job == null)
                {
                    return null;
                }

                job.Status = BackgroundJobStatus.Running;
                job.RunnerId = runnerId;
                job.ClaimToken = claimToken;
                job.LeaseExpiresAtUtc = leaseExpiresAtUtc;
                job.DequeuedAtUtc = now;
                job.UpdatedAtUtc = now;

                await _context.SaveChangesAsync(ct);
                return job.JobId;
            }
            finally
            {
                ClaimLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> RenewAsync(
            string jobId,
            string claimToken,
            DateTime now,
            DateTime leaseExpiresAtUtc,
            CancellationToken ct)
        {
            var job = await _context.BackgroundJobs
                .FirstOrDefaultAsync(
                    x => x.JobId == jobId
                        && x.ClaimToken == claimToken
                        && x.Status == BackgroundJobStatus.Running,
                    ct);
            if (job == null)
            {
                return false;
            }

            job.LeaseExpiresAtUtc = leaseExpiresAtUtc;
            job.HeartbeatAtUtc = now;
            job.UpdatedAtUtc = now;
            await _context.SaveChangesAsync(ct);
            return true;
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(string jobId, string claimToken, DateTime now)
        {
            var job = await _context.BackgroundJobs
                .FirstOrDefaultAsync(
                    x => x.JobId == jobId
                        && x.ClaimToken == claimToken
                        && x.Status == BackgroundJobStatus.Running);
            if (job == null)
            {
                return;
            }

            job.ClaimToken = null;
            job.LeaseExpiresAtUtc = null;
            job.RunnerId = null;
            job.UpdatedAtUtc = now;
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Whether a job may be claimed at <paramref name="now"/>: it was created already and its optional
        /// <c>retryNotBeforeUtc</c> payload field has passed.
        /// </summary>
        internal static bool IsRunnableAt(BackgroundJob job, DateTime now)
        {
            if (job.CreatedAtUtc > now)
            {
                return false;
            }

            var retryNotBeforeUtc = GetRetryNotBeforeUtc(job.PayloadJson);
            return retryNotBeforeUtc == null ||
                retryNotBeforeUtc <= new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        }

        internal static DateTimeOffset? GetRetryNotBeforeUtc(string? payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson) ||
                !payloadJson.Contains("\"retryNotBeforeUtc\"", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var payload = Newtonsoft.Json.Linq.JObject.Parse(payloadJson);
                var value = payload["retryNotBeforeUtc"]?.ToString();
                return DateTimeOffset.TryParse(value, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
