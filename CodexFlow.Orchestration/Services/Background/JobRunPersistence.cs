// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;

namespace CodexFlow.Services.Background;

/// <summary>Tracks the last committed runner snapshot without tracking unrelated job fields.</summary>
internal sealed class JobRunPersistence(IJobStore jobs, IJobRunnerStore writes)
{
    private JobRunnerFields? _previous;

    public static JobRunPersistence Create(IServiceProvider services)
        => new(services.GetRequiredService<IJobStore>(), services.GetRequiredService<IJobRunnerStore>());

    public async Task<BackgroundJob?> LoadAsync(string jobId, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct).ConfigureAwait(false);
        _previous = job is null ? null : JobRunnerFields.Capture(job);
        return job;
    }

    public Task<BackgroundJob?> FindCurrentAsync(string jobId, CancellationToken ct)
        => jobs.FindAsync(jobId, ct);

    public Task<JobCheckpoint?> FindLatestCheckpointAsync(string jobId, CancellationToken ct)
        => jobs.FindLatestCheckpointAsync(jobId, ct);

    public async Task<bool> TryReloadAsync(BackgroundJob job, CancellationToken ct)
    {
        var fresh = await jobs.FindAsync(job.JobId, ct).ConfigureAwait(false);
        if (_previous is not { } owner || fresh is null || fresh.Status != owner.Status ||
            fresh.ClaimToken != owner.ClaimToken || fresh.Attempt != owner.Attempt)
        {
            return false;
        }

        _previous = JobRunnerFields.Capture(fresh);
        _previous.ApplyTo(job);
        job.LastEventSeq = fresh.LastEventSeq;
        return true;
    }

    public async Task<bool> TryCommitAsync(
        BackgroundJob job, JobCheckpoint? checkpoint, OutboxEvent? entry, CancellationToken ct)
    {
        var before = _previous ?? throw new InvalidOperationException("Load the job before persisting runner state.");
        var after = JobRunnerFields.Capture(job);
        if (!await writes.TryCommitAsync(job.JobId, before, after, checkpoint, entry, ct).ConfigureAwait(false))
        {
            return false;
        }

        _previous = after;
        return true;
    }
}
