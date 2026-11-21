// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed partial class EfBusinessSkillExecutionStore
{
    public async Task<SkillRunEntity> CreateRunAsync(SkillRunEntity proposed, CancellationToken ct)
    {
        _db.SkillRuns.Add(proposed);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return proposed;
        }
        catch (DbUpdateException)
        {
            _db.Entry(proposed).State = EntityState.Detached;
            var existing = await FindIdempotentRunAsync(proposed.SkillId, proposed.ClientRequestId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    public async Task<SkillRunEntity?> RequestStopAsync(string runId, DateTime now, CancellationToken ct)
    {
        var affected = await _db.SkillRuns.Where(row => row.RunId == runId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.StopRequested, true)
                .SetProperty(row => row.UpdatedAtUtc, now), ct).ConfigureAwait(false);
        return affected == 0 ? null : await FindRunAsync(runId, ct).ConfigureAwait(false);
    }

    public async Task<bool> StartQueuedRunAsync(string runId, DateTime now, CancellationToken ct)
        => await _db.SkillRuns.Where(row => row.RunId == runId && row.Status == nameof(SkillRunStatus.Queued))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, nameof(SkillRunStatus.Running))
                .SetProperty(row => row.UpdatedAtUtc, now), ct).ConfigureAwait(false) != 0;

    public Task SetPendingApprovalAsync(string runId, SkillRunStatus status, string json, DateTime now, CancellationToken ct)
    {
        var statusText = status.ToString();
        return _db.SkillRuns.Where(row => row.RunId == runId &&
                (row.Status == nameof(SkillRunStatus.Queued) || row.Status == nameof(SkillRunStatus.Running) ||
                 row.Status == nameof(SkillRunStatus.WaitingForApproval))).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Status, statusText)
            .SetProperty(row => row.PendingApprovalJson, json)
            .SetProperty(row => row.UpdatedAtUtc, now), ct);
    }

    public Task ClearPendingApprovalAsync(string runId, DateTime now, CancellationToken ct)
        => _db.SkillRuns.Where(row => row.RunId == runId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.PendingApprovalJson, (string?)null)
            .SetProperty(row => row.Status, row => row.Status == nameof(SkillRunStatus.WaitingForApproval)
                ? nameof(SkillRunStatus.Running) : row.Status)
            .SetProperty(row => row.UpdatedAtUtc, now), ct);

    public async Task<SkillRunEntity?> FinishRunAsync(
        string runId, SkillRunFinalState state, SkillDraftRunEvidence? evidence, CancellationToken ct)
    {
        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var candidates = _db.SkillRuns.Where(row => row.RunId == runId &&
            (row.Status == nameof(SkillRunStatus.Running) || row.Status == nameof(SkillRunStatus.WaitingForApproval) ||
             row.Status == nameof(SkillRunStatus.Queued)));
        if (await ApplyFinalStateAsync(candidates, state, ct).ConfigureAwait(false) == 0)
        {
            return null;
        }

        if (evidence is not null)
        {
            await _db.SkillDrafts.Where(row => row.Id == evidence.DraftId && row.Revision == evidence.Revision &&
                    row.CandidateDefinitionJson == evidence.ExpectedDefinitionJson)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CandidateDefinitionJson, evidence.NewDefinitionJson)
                    .SetProperty(row => row.UpdatedAtUtc, evidence.Now), ct).ConfigureAwait(false);
        }

        var finished = await FindRunAsync(runId, ct).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        return finished;
    }

    public async Task RecoverRunsAsync(IReadOnlyList<SkillRunRecovery> recoveries, CancellationToken ct)
    {
        if (recoveries.Count == 0)
        {
            return;
        }

        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        foreach (var recovery in recoveries)
        {
            var candidates = _db.SkillRuns.Where(row => row.RunId == recovery.RunId && row.Status == recovery.ExpectedStatus);
            if (await ApplyFinalStateAsync(candidates, recovery.FinalState, ct).ConfigureAwait(false) == 0)
            {
                continue;
            }

            foreach (var step in recovery.ChangedSteps)
            {
                await _db.SkillRunSteps.Where(row => row.RunId == recovery.RunId && row.Sequence == step.Sequence &&
                        row.State == nameof(SkillRunStepState.Dispatching))
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.State, step.State)
                        .SetProperty(row => row.ErrorCode, step.ErrorCode)
                        .SetProperty(row => row.ResponseSummary, step.ResponseSummary)
                        .SetProperty(row => row.CompletedAtUtc, step.CompletedAtUtc), ct).ConfigureAwait(false);
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task PersistCheckpointAsync(SkillRunStepEntity checkpoint, bool insert, CancellationToken ct)
    {
        if (insert)
        {
            _db.SkillRunSteps.Add(checkpoint);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        await _db.SkillRunSteps.Where(row => row.RunId == checkpoint.RunId && row.Sequence == checkpoint.Sequence)
            .ExecuteUpdateAsync(set => set
                .SetProperty(row => row.State, checkpoint.State)
                .SetProperty(row => row.RequestSummary, row => checkpoint.RequestSummary ?? row.RequestSummary)
                .SetProperty(row => row.ResponseSummary, row => checkpoint.ResponseSummary ?? row.ResponseSummary)
                .SetProperty(row => row.Evidence, row => checkpoint.Evidence ?? row.Evidence)
                .SetProperty(row => row.ErrorCode, row => checkpoint.ErrorCode ?? row.ErrorCode)
                .SetProperty(row => row.CompletedAtUtc, row => checkpoint.CompletedAtUtc ?? row.CompletedAtUtc), ct).ConfigureAwait(false);
    }

    public Task UpdateProgressAsync(string runId, int externalRequests, int readOnlyRetries, DateTime now, CancellationToken ct)
        => _db.SkillRuns.Where(row => row.RunId == runId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ExternalRequestCount, externalRequests)
            .SetProperty(row => row.ReadOnlyRetryCount, readOnlyRetries)
            .SetProperty(row => row.UpdatedAtUtc, now), ct);

    private static Task<int> ApplyFinalStateAsync(IQueryable<SkillRunEntity> rows, SkillRunFinalState state, CancellationToken ct)
    {
        var statusText = state.Status.ToString();
        return rows.ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Status, statusText)
            .SetProperty(row => row.PendingApprovalJson, (string?)null)
            .SetProperty(row => row.ExternalRequestCount, row => state.ExternalRequests ?? row.ExternalRequestCount)
            .SetProperty(row => row.ReadOnlyRetryCount, row => state.ReadOnlyRetries ?? row.ReadOnlyRetryCount)
            .SetProperty(row => row.OutputPayloadJson, row => state.UpdateOutput ? state.OutputJson : row.OutputPayloadJson)
            .SetProperty(row => row.ErrorJson, state.ErrorJson)
            .SetProperty(row => row.UpdatedAtUtc, state.Now), ct);
    }
}
