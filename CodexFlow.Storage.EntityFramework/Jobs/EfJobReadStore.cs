// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq.Expressions;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfJobReadStore(IJobDbContext db) : IJobReadStore
{
    private static readonly Expression<Func<BackgroundJob, JobViewSnapshot>> JobView = job => new JobViewSnapshot(
        job.JobId, job.SessionId, job.TaskId, job.JobType, job.RunnerId, job.Attempt, job.Status, job.UpdatedAtUtc,
        job.PayloadJson, job.ResultPayloadJson, job.ShortSummary, job.ErrorMessage, job.WaitingReason,
        job.Checkpoints.OrderByDescending(c => c.SequenceNumber).Select(c => c.ResumeToken).FirstOrDefault(),
        job.Checkpoints.OrderByDescending(c => c.SequenceNumber).Select(c => c.CurrentStep).FirstOrDefault(),
        job.LastEventSeq);

    private static readonly Expression<Func<BackgroundJob, ActivePlanJob>> PlanJob = job => new ActivePlanJob(
        job.JobId, job.TaskId, job.JobType, job.Status, job.ResultPayloadJson, job.Attempt,
        job.Checkpoints.OrderByDescending(c => c.SequenceNumber).Select(c => c.CurrentStep).FirstOrDefault());

    public Task<JobViewSnapshot?> FindViewAsync(string jobId, CancellationToken ct = default)
        => db.BackgroundJobs.AsNoTracking().Where(j => j.JobId == jobId).Select(JobView).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<JobViewSnapshot>> ReadSessionViewsAsync(
        string sessionId, CancellationToken ct = default)
        => await db.BackgroundJobs.AsNoTracking().Where(j => j.SessionId == sessionId)
            .OrderByDescending(j => j.CreatedAtUtc).Select(JobView).ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string?>> ReadCompletedResultPayloadsAsync(
        string sessionId, string excludingJobId, CancellationToken ct)
        => await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.SessionId == sessionId && j.JobId != excludingJobId && j.Status == BackgroundJobStatus.Completed)
            .OrderBy(j => j.CreatedAtUtc).Select(j => j.ResultPayloadJson).ToListAsync(ct).ConfigureAwait(false);

    public Task<ActivePlanJob?> FindActiveTaskJobAsync(
        string sessionId, string taskId, string jobType, CancellationToken ct)
        => ReadActiveJobs(sessionId).Where(j => j.TaskId == taskId && j.JobType == jobType)
            .OrderByDescending(j => j.UpdatedAtUtc).Select(PlanJob).FirstOrDefaultAsync(ct);

    public async Task<ActivePlanJob?> FindActiveSessionWorkerAsync(string sessionId, CancellationToken ct)
    {
        var candidates = await ReadActiveJobs(sessionId).OrderByDescending(j => j.UpdatedAtUtc)
            .Select(PlanJob).ToListAsync(ct).ConfigureAwait(false);
        return candidates.FirstOrDefault(j => WorkerJobTypes.IsWorkerJobType(j.JobType));
    }

    private IQueryable<BackgroundJob> ReadActiveJobs(string sessionId) => db.BackgroundJobs.AsNoTracking()
        .Where(j => j.SessionId == sessionId && (j.Status == BackgroundJobStatus.Queued ||
            j.Status == BackgroundJobStatus.Running || j.Status == BackgroundJobStatus.WaitingUser ||
            j.Status == BackgroundJobStatus.FailedRecoveryNeeded));
}
