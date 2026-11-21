// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfJobRunnerStore(IJobDbContext db) : IJobRunnerStore
{
    public async Task<bool> TryCommitAsync(string jobId, JobRunnerFields before, JobRunnerFields after,
        JobCheckpoint? checkpoint, OutboxEvent? entry, CancellationToken ct)
    {
        var matches = db.BackgroundJobs.Where(job => job.JobId == jobId && job.Status == before.Status &&
            job.ClaimToken == before.ClaimToken && job.Attempt == before.Attempt);
        if (checkpoint is not null)
        {
            var sequence = checkpoint.SequenceNumber;
            matches = matches.Where(job => (db.JobCheckpoints
                .Where(c => c.JobId == job.JobId).Select(c => (int?)c.SequenceNumber).Max() ?? -1) + 1 == sequence);
        }

        if (!db.Database.IsRelational())
        {
            var current = await matches.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (current is null)
            {
                return false;
            }

            if (before.Status != after.Status) current.Status = after.Status;
            if (before.PayloadJson != after.PayloadJson) current.PayloadJson = after.PayloadJson;
            if (before.ResultPayloadJson != after.ResultPayloadJson) current.ResultPayloadJson = after.ResultPayloadJson;
            if (before.ErrorMessage != after.ErrorMessage) current.ErrorMessage = after.ErrorMessage;
            if (before.RunnerId != after.RunnerId) current.RunnerId = after.RunnerId;
            if (before.ClaimToken != after.ClaimToken) current.ClaimToken = after.ClaimToken;
            if (before.LeaseExpiresAtUtc != after.LeaseExpiresAtUtc) current.LeaseExpiresAtUtc = after.LeaseExpiresAtUtc;
            if (before.DequeuedAtUtc != after.DequeuedAtUtc) current.DequeuedAtUtc = after.DequeuedAtUtc;
            if (before.Attempt != after.Attempt) current.Attempt = after.Attempt;
            if (before.WaitingReason != after.WaitingReason) current.WaitingReason = after.WaitingReason;
            if (before.CreatedAtUtc != after.CreatedAtUtc) current.CreatedAtUtc = after.CreatedAtUtc;
            if (before.StartedAtUtc != after.StartedAtUtc) current.StartedAtUtc = after.StartedAtUtc;
            if (before.FinishedAtUtc != after.FinishedAtUtc) current.FinishedAtUtc = after.FinishedAtUtc;
            if (before.UpdatedAtUtc != after.UpdatedAtUtc) current.UpdatedAtUtc = after.UpdatedAtUtc;
            if (before.NotificationStatus != after.NotificationStatus) current.NotificationStatus = after.NotificationStatus;

            AddRecords(checkpoint, entry);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return false;
            }
        }

        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var transaction = owned ?? db.Database.CurrentTransaction!;
        var savepoint = $"runner_{Guid.NewGuid():N}";
        if (owned is null)
        {
            await transaction.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        var checkpointAdded = false;
        var entryAdded = false;
        var originalSequence = entry?.Seq ?? 0;
        try
        {
            var affected = await matches.ExecuteUpdateAsync(setters =>
            {
                if (before == after) setters.SetProperty(j => j.JobId, j => j.JobId);
                if (before.Status != after.Status) setters.SetProperty(j => j.Status, after.Status);
                if (before.PayloadJson != after.PayloadJson) setters.SetProperty(j => j.PayloadJson, after.PayloadJson);
                if (before.ResultPayloadJson != after.ResultPayloadJson) setters.SetProperty(j => j.ResultPayloadJson, after.ResultPayloadJson);
                if (before.ErrorMessage != after.ErrorMessage) setters.SetProperty(j => j.ErrorMessage, after.ErrorMessage);
                if (before.RunnerId != after.RunnerId) setters.SetProperty(j => j.RunnerId, after.RunnerId);
                if (before.ClaimToken != after.ClaimToken) setters.SetProperty(j => j.ClaimToken, after.ClaimToken);
                if (before.LeaseExpiresAtUtc != after.LeaseExpiresAtUtc) setters.SetProperty(j => j.LeaseExpiresAtUtc, after.LeaseExpiresAtUtc);
                if (before.DequeuedAtUtc != after.DequeuedAtUtc) setters.SetProperty(j => j.DequeuedAtUtc, after.DequeuedAtUtc);
                if (before.Attempt != after.Attempt) setters.SetProperty(j => j.Attempt, after.Attempt);
                if (before.WaitingReason != after.WaitingReason) setters.SetProperty(j => j.WaitingReason, after.WaitingReason);
                if (before.CreatedAtUtc != after.CreatedAtUtc) setters.SetProperty(j => j.CreatedAtUtc, after.CreatedAtUtc);
                if (before.StartedAtUtc != after.StartedAtUtc) setters.SetProperty(j => j.StartedAtUtc, after.StartedAtUtc);
                if (before.FinishedAtUtc != after.FinishedAtUtc) setters.SetProperty(j => j.FinishedAtUtc, after.FinishedAtUtc);
                if (before.UpdatedAtUtc != after.UpdatedAtUtc) setters.SetProperty(j => j.UpdatedAtUtc, after.UpdatedAtUtc);
                if (before.NotificationStatus != after.NotificationStatus) setters.SetProperty(j => j.NotificationStatus, after.NotificationStatus);
            }, ct).ConfigureAwait(false);
            if (affected == 1)
            {
                if (checkpoint is not null)
                {
                    db.JobCheckpoints.Add(checkpoint);
                    checkpointAdded = true;
                }
                if (entry is not null)
                {
                    db.OutboxEvents.Add(entry);
                    entryAdded = true;
                }
                if (checkpointAdded || entryAdded)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            if (owned is null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            }
            else
            {
                await owned.CommitAsync(ct).ConfigureAwait(false);
            }

            return affected == 1;
        }
        catch
        {
            if (owned is null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            }
            if (checkpointAdded)
            {
                db.Entry(checkpoint!).State = EntityState.Detached;
            }
            if (entryAdded)
            {
                db.Entry(entry!).State = EntityState.Detached;
                entry!.Seq = originalSequence;
            }
            throw;
        }
    }

    private void AddRecords(JobCheckpoint? checkpoint, OutboxEvent? entry)
    {
        if (checkpoint is not null)
        {
            db.JobCheckpoints.Add(checkpoint);
        }

        if (entry is not null)
        {
            db.OutboxEvents.Add(entry);
        }
    }
}
