// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfOutboxStore(IJobDbContext db) : IOutboxStore
{
    public async Task AppendAsync(OutboxEvent entry, CancellationToken ct = default)
    {
        await db.OutboxEvents.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(DateTime cutoffUtc, int limit, CancellationToken ct)
        => await db.OutboxEvents.AsNoTracking().Where(entry => entry.ProcessedAt == null && entry.OccurredAtUtc < cutoffUtc)
            .OrderBy(entry => entry.Seq).Take(limit).ToListAsync(ct).ConfigureAwait(false);

    public async Task RecordProjectionAsync(IReadOnlyList<OutboxProjectionResult> results, CancellationToken ct)
    {
        var ids = results.Select(result => result.EventId).ToArray();
        var entries = await db.OutboxEvents.Where(e => e.ProcessedAt == null && ids.Contains(e.EventId))
            .ToDictionaryAsync(entry => entry.EventId, ct).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (!entries.TryGetValue(result.EventId, out var entry)) continue;
            if (result.ProcessedAtUtc is { } processedAt)
            {
                entry.ProcessedAt = processedAt;
            }
            else
            {
                entry.RetryCount++;
                entry.LastError = result.Error;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AdvanceLastEventSeqAsync(string jobId, long sequence, DateTime now, CancellationToken ct)
        => await db.BackgroundJobs.Where(job => job.JobId == jobId && job.LastEventSeq < sequence)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LastEventSeq, sequence)
                .SetProperty(job => job.UpdatedAtUtc, now), ct).ConfigureAwait(false);

    public Task<OutboxJobSnapshot?> FindJobAsync(string jobId, CancellationToken ct)
        => db.BackgroundJobs.AsNoTracking().Where(job => job.JobId == jobId)
            .Select(job => new OutboxJobSnapshot(
                job.JobId, job.SessionId, job.TaskId, job.JobType, job.RunnerId, job.Attempt,
                job.Status, job.PayloadJson, job.ResultPayloadJson, job.ShortSummary, job.WaitingReason, job.ErrorMessage,
                job.Checkpoints.OrderByDescending(checkpoint => checkpoint.SequenceNumber)
                    .Select(checkpoint => checkpoint.CurrentStep).FirstOrDefault()))
            .FirstOrDefaultAsync(ct);
}
