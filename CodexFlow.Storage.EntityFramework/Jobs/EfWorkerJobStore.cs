// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfWorkerJobStore(IJobDbContext db) : IWorkerJobStore
{
    public async Task<WorkerContinuationState?> FindContinuationAsync(string jobId, CancellationToken ct)
    {
        var job = await db.BackgroundJobs.AsNoTracking().Include(j => j.Checkpoints)
            .SingleOrDefaultAsync(j => j.JobId == jobId, ct).ConfigureAwait(false);
        return job is null
            ? null
            : new WorkerContinuationState(job, job.Checkpoints.OrderByDescending(c => c.SequenceNumber)
                .Select(c => c.ResumeToken).FirstOrDefault(token => !string.IsNullOrWhiteSpace(token)));
    }

    public async Task<IReadOnlyList<BackgroundJob>> ReadSessionJobsAsync(string sessionId, CancellationToken ct)
        => await db.BackgroundJobs.AsNoTracking().Where(j => j.SessionId == sessionId)
            .OrderBy(j => j.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> TryUpdateWorktreePayloadAsync(
        BackgroundJob expected, string payloadJson, DateTime updatedAtUtc, CancellationToken ct)
    {
        var matches = db.BackgroundJobs.Where(j => j.JobId == expected.JobId && j.Status == expected.Status &&
            j.ClaimToken == expected.ClaimToken && j.Attempt == expected.Attempt && j.PayloadJson == expected.PayloadJson);
        if (db.Database.IsRelational())
        {
            return await matches.ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.PayloadJson, payloadJson).SetProperty(j => j.UpdatedAtUtc, updatedAtUtc), ct)
                .ConfigureAwait(false) == 1;
        }

        var job = await matches.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        job.PayloadJson = payloadJson;
        job.UpdatedAtUtc = updatedAtUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryUpdateVerificationPayloadAsync(
        BackgroundJob expected, string payloadJson, string? errorMessage, DateTime updatedAtUtc, CancellationToken ct)
        => await db.BackgroundJobs.Where(j => j.JobId == expected.JobId && j.Attempt == expected.Attempt &&
                j.Status == BackgroundJobStatus.Completed && j.ResultPayloadJson == expected.ResultPayloadJson)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.ResultPayloadJson, payloadJson)
                .SetProperty(j => j.ErrorMessage, errorMessage)
                .SetProperty(j => j.UpdatedAtUtc, updatedAtUtc), ct).ConfigureAwait(false) == 1;
}
