// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfJobStore(IJobDbContext db) : IJobStore
{
    public async Task CreateAsync(BackgroundJob job, JobCheckpoint checkpoint, OutboxEvent entry, CancellationToken ct = default)
    {
        db.BackgroundJobs.Add(job);
        db.JobCheckpoints.Add(checkpoint);
        db.OutboxEvents.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<BackgroundJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default)
        => db.BackgroundJobs.AsNoTracking().Where(job => job.JobId == jobId)
            .Select(job => (BackgroundJobStatus?)job.Status).FirstOrDefaultAsync(ct);

    public Task<BackgroundJob?> FindAsync(string jobId, CancellationToken ct = default)
        => db.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(job => job.JobId == jobId, ct);

    public Task<JobCheckpoint?> FindLatestCheckpointAsync(string jobId, CancellationToken ct = default)
        => db.JobCheckpoints.AsNoTracking().Where(checkpoint => checkpoint.JobId == jobId)
            .OrderByDescending(checkpoint => checkpoint.SequenceNumber).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<BackgroundJob>> ReadActiveAsync(string sessionId, CancellationToken ct = default)
        => await db.BackgroundJobs.AsNoTracking().Where(job => job.SessionId == sessionId &&
            (job.Status == BackgroundJobStatus.Queued || job.Status == BackgroundJobStatus.Running ||
                job.Status == BackgroundJobStatus.WaitingUser || job.Status == BackgroundJobStatus.FailedRecoveryNeeded))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<int> ApplyTransitionsAsync(IReadOnlyList<JobTransition> transitions, CancellationToken ct = default)
    {
        if (transitions.Count == 0) return 0;

        await using var owned = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var transaction = owned ?? db.Database.CurrentTransaction!;
        var savepoint = $"job_transitions_{Guid.NewGuid():N}";
        if (owned == null) await transaction.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        var count = 0;
        var addedCheckpoints = new List<JobCheckpoint>();
        var addedEvents = new List<OutboxEvent>();
        try
        {
            foreach (var transition in transitions)
            {
                if (await ApplyAsync(transition, ct).ConfigureAwait(false) == 0) continue;
                if (transition.Checkpoint is { } checkpoint)
                {
                    db.JobCheckpoints.Add(checkpoint);
                    addedCheckpoints.Add(checkpoint);
                }
                db.OutboxEvents.Add(transition.Event);
                addedEvents.Add(transition.Event);
                count++;
            }

            if (count > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (owned != null) await owned.CommitAsync(ct).ConfigureAwait(false);
            else await transaction.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            return count;
        }
        catch
        {
            if (owned == null) await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            foreach (var checkpoint in addedCheckpoints) db.Entry(checkpoint).State = EntityState.Detached;
            foreach (var entry in addedEvents)
            {
                db.Entry(entry).State = EntityState.Detached;
                entry.Seq = 0;
            }
            throw;
        }
    }

    private Task<int> ApplyAsync(JobTransition transition, CancellationToken ct)
    {
        var next = transition.UpdatedJob;
        var query = db.BackgroundJobs.Where(job => job.JobId == next.JobId && job.Status == transition.ExpectedStatus &&
            job.ClaimToken == transition.ExpectedClaimToken && job.Attempt == transition.ExpectedAttempt);
        if (transition.Kind is JobTransitionKind.Resume or JobTransitionKind.DenyApproval)
        {
            query = query.Where(job => job.Checkpoints.OrderByDescending(checkpoint => checkpoint.SequenceNumber)
                .Select(checkpoint => checkpoint.CheckpointId).FirstOrDefault() == transition.ExpectedCheckpointId);
        }

        var worker = WorkerJobTypes.IsWorkerJobType(next.JobType);
        return transition.Kind switch
        {
            JobTransitionKind.Retry => query.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, next.Status)
                .SetProperty(job => job.PayloadJson, next.PayloadJson).SetProperty(job => job.Attempt, next.Attempt)
                .SetProperty(job => job.ErrorMessage, next.ErrorMessage).SetProperty(job => job.WaitingReason, next.WaitingReason)
                .SetProperty(job => job.CancelledAtUtc, next.CancelledAtUtc).SetProperty(job => job.FinishedAtUtc, next.FinishedAtUtc)
                .SetProperty(job => job.UpdatedAtUtc, next.UpdatedAtUtc), ct),
            JobTransitionKind.Cancel => query.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, next.Status)
                .SetProperty(job => job.CancelledAtUtc, next.CancelledAtUtc).SetProperty(job => job.FinishedAtUtc, next.FinishedAtUtc)
                .SetProperty(job => job.NotificationStatus, job => worker ? next.NotificationStatus : job.NotificationStatus)
                .SetProperty(job => job.ErrorMessage, job => worker ? next.ErrorMessage : job.ErrorMessage)
                .SetProperty(job => job.UpdatedAtUtc, next.UpdatedAtUtc), ct),
            JobTransitionKind.Resume => query.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, next.Status)
                .SetProperty(job => job.PayloadJson, next.PayloadJson).SetProperty(job => job.WaitingReason, next.WaitingReason)
                .SetProperty(job => job.ErrorMessage, next.ErrorMessage).SetProperty(job => job.RunnerId, next.RunnerId)
                .SetProperty(job => job.ClaimToken, next.ClaimToken).SetProperty(job => job.LeaseExpiresAtUtc, next.LeaseExpiresAtUtc)
                .SetProperty(job => job.DequeuedAtUtc, next.DequeuedAtUtc).SetProperty(job => job.StartedAtUtc, next.StartedAtUtc)
                .SetProperty(job => job.FinishedAtUtc, next.FinishedAtUtc).SetProperty(job => job.NotificationStatus, next.NotificationStatus)
                .SetProperty(job => job.UpdatedAtUtc, next.UpdatedAtUtc), ct),
            JobTransitionKind.DenyApproval => query.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, next.Status)
                .SetProperty(job => job.WaitingReason, next.WaitingReason).SetProperty(job => job.ErrorMessage, next.ErrorMessage)
                .SetProperty(job => job.ResultPayloadJson, next.ResultPayloadJson).SetProperty(job => job.FinishedAtUtc, next.FinishedAtUtc)
                .SetProperty(job => job.UpdatedAtUtc, next.UpdatedAtUtc), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(transition))
        };
    }

    public async Task<JobDeletionResult> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        var checkpoints = await db.JobCheckpoints.Where(checkpoint => checkpoint.SessionId == sessionId).ToListAsync(ct)
            .ConfigureAwait(false);
        var events = await db.OutboxEvents.Where(entry => entry.SessionId == sessionId).ToListAsync(ct).ConfigureAwait(false);
        var jobs = await db.BackgroundJobs.Where(job => job.SessionId == sessionId).ToListAsync(ct).ConfigureAwait(false);
        var changed = checkpoints.Count > 0 || events.Count > 0 || jobs.Count > 0;
        if (changed)
        {
            db.JobCheckpoints.RemoveRange(checkpoints);
            db.OutboxEvents.RemoveRange(events);
            db.BackgroundJobs.RemoveRange(jobs);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new JobDeletionResult(jobs.Select(job => job.JobId).ToArray(), changed);
    }

    public Task<BackgroundJob?> FindOwnedJobAsync(string jobId, string userId, CancellationToken ct)
        => db.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(job => job.JobId == jobId &&
            db.Sessions.Any(session => session.Id == job.SessionId && session.UserId == userId), ct);

    public Task<SessionRecord?> FindOwnedSessionAsync(string sessionId, string userId, CancellationToken ct)
        => db.Sessions.AsNoTracking().Where(session => session.Id == sessionId && session.UserId == userId)
            .Select(session => new SessionRecord(session.Id, session.UserId, session.WorkspacePath)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<string>> ListSessionIdsAsync(string userId, CancellationToken ct)
        => await db.Sessions.AsNoTracking().Where(session => session.UserId == userId).Select(session => session.Id)
            .ToListAsync(ct).ConfigureAwait(false);
}
