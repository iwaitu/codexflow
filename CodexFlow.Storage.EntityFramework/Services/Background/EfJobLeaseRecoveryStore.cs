// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Services.Background;

public sealed class EfJobLeaseRecoveryStore(IJobDbContext db) : IJobLeaseRecoveryStore
{
    public async Task<bool> VerifyOrDeferClaimAsync(
        string jobId, string claimToken, DateTime now, CancellationToken ct)
    {
        var job = await db.BackgroundJobs.FirstOrDefaultAsync(
            x => x.JobId == jobId && x.ClaimToken == claimToken && x.Status == BackgroundJobStatus.Running, ct)
            .ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        var earliest = JobLeaseEligibility.GetEarliestRunAt(job.CreatedAtUtc, job.PayloadJson);
        if (earliest <= now)
        {
            return true;
        }

        job.CreatedAtUtc = earliest;
        job.Status = BackgroundJobStatus.Queued;
        job.RunnerId = null;
        job.ClaimToken = null;
        job.LeaseExpiresAtUtc = null;
        job.DequeuedAtUtc = null;
        job.StartedAtUtc = null;
        job.FinishedAtUtc = null;
        job.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return false;
    }

    public async Task<IReadOnlyList<ExpiredJobLease>> ReadExpiredAsync(DateTime now, CancellationToken ct)
        => await db.BackgroundJobs.AsNoTracking()
            .Where(job => job.Status == BackgroundJobStatus.Running && job.LeaseExpiresAtUtc < now)
            .Select(job => new ExpiredJobLease(job.JobId, job.JobType, job.SessionId, job.TaskId, job.ClaimToken,
                job.LeaseExpiresAtUtc!.Value, job.HeartbeatAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> RecoverExpiredAsync(
        IReadOnlyList<ExpiredJobLeaseRecovery> recoveries, DateTime now, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return await RecoverTrackedAsync(recoveries, now, ct).ConfigureAwait(false);
        }

        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var transaction = owned ?? db.Database.CurrentTransaction!;
        var savepoint = $"lease_recovery_{Guid.NewGuid():N}";
        if (owned is null)
        {
            await transaction.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        var addedEvents = new List<(OutboxEvent Entry, long OriginalSequence)>();
        try
        {
            var changed = false;
            foreach (var update in recoveries)
            {
                var needsRecovery = update.Event is not null;
                var status = needsRecovery ? BackgroundJobStatus.FailedRecoveryNeeded : BackgroundJobStatus.Queued;
                var error = update.ErrorMessage;
                var count = await MatchLease(update.Lease, now).ExecuteUpdateAsync(builder => builder
                    .SetProperty(job => job.Status, status)
                    .SetProperty(job => job.RunnerId, (string?)null)
                    .SetProperty(job => job.ClaimToken, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(job => job.UpdatedAtUtc, now)
                    .SetProperty(job => job.ErrorMessage, error)
                    .SetProperty(job => job.FinishedAtUtc, job => needsRecovery ? job.FinishedAtUtc ?? now : null)
                    .SetProperty(job => job.NotificationStatus, job => needsRecovery ? NotificationStatus.Pending : job.NotificationStatus), ct)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    continue;
                }

                changed = true;
                if (update.Event is { } entry)
                {
                    var originalSequence = entry.Seq;
                    db.OutboxEvents.Add(entry);
                    addedEvents.Add((entry, originalSequence));
                }
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (owned is null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            }
            else
            {
                await owned.CommitAsync(ct).ConfigureAwait(false);
            }
            return changed;
        }
        catch
        {
            if (owned is null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            }
            foreach (var (entry, originalSequence) in addedEvents)
            {
                db.Entry(entry).State = EntityState.Detached;
                entry.Seq = originalSequence;
            }
            throw;
        }
    }

    private async Task<bool> RecoverTrackedAsync(
        IReadOnlyList<ExpiredJobLeaseRecovery> recoveries, DateTime now, CancellationToken ct)
    {
        var changed = false;
        foreach (var update in recoveries)
        {
            var job = await MatchLease(update.Lease, now).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (job is null)
            {
                continue;
            }

            job.RunnerId = null;
            job.ClaimToken = null;
            job.LeaseExpiresAtUtc = null;
            job.UpdatedAtUtc = now;
            job.ErrorMessage = update.ErrorMessage;
            if (update.Event is { } entry)
            {
                job.Status = BackgroundJobStatus.FailedRecoveryNeeded;
                job.FinishedAtUtc ??= now;
                job.NotificationStatus = NotificationStatus.Pending;
                db.OutboxEvents.Add(entry);
            }
            else
            {
                job.Status = BackgroundJobStatus.Queued;
                job.FinishedAtUtc = null;
            }

            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return changed;
    }

    private IQueryable<BackgroundJob> MatchLease(ExpiredJobLease lease, DateTime now)
        => db.BackgroundJobs.Where(job => job.JobId == lease.JobId && job.Status == BackgroundJobStatus.Running &&
            job.ClaimToken == lease.ClaimToken && job.LeaseExpiresAtUtc == lease.ExpiresAtUtc &&
            job.LeaseExpiresAtUtc < now && job.HeartbeatAtUtc == lease.HeartbeatAtUtc &&
            job.JobType == lease.JobType && job.SessionId == lease.SessionId && job.TaskId == lease.TaskId);
}
