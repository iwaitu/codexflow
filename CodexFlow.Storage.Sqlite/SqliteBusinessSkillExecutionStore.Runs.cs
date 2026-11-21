// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed partial class SqliteBusinessSkillExecutionStore
{
    public async Task<SkillRunEntity> CreateRunAsync(SkillRunEntity proposed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, $"""
            INSERT INTO SkillRuns ({SkillRunColumns})
            VALUES ($id, $skill, $version, $client, $status, $stop, $requests, $retries, $inputs, $output,
                $pending, $error, $created, $updated)
            ON CONFLICT (SkillId, ClientRequestId) DO NOTHING
            """, ("$id", proposed.RunId), ("$skill", proposed.SkillId), ("$version", proposed.SkillVersion),
            ("$client", proposed.ClientRequestId), ("$status", proposed.Status), ("$stop", proposed.StopRequested),
            ("$requests", proposed.ExternalRequestCount), ("$retries", proposed.ReadOnlyRetryCount), ("$inputs", proposed.InputsJson),
            ("$output", proposed.OutputPayloadJson), ("$pending", proposed.PendingApprovalJson), ("$error", proposed.ErrorJson),
            ("$created", proposed.CreatedAtUtc), ("$updated", proposed.UpdatedAtUtc));

        var winner = proposed;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            await using var select = Command(connection, transaction,
                $"SELECT {SkillRunColumns} FROM SkillRuns WHERE SkillId = $skill AND ClientRequestId = $client",
                ("$skill", proposed.SkillId), ("$client", proposed.ClientRequestId));
            winner = await ReadOneRunAsync(select, ct).ConfigureAwait(false) ??
                throw new InvalidOperationException("The existing business skill run was not found.");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return winner;
    }

    public async Task<SkillRunEntity?> RequestStopAsync(string runId, DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var command = Command(connection, null, $"""
            UPDATE SkillRuns SET StopRequested = 1, UpdatedAtUtc = $now WHERE RunId = $id RETURNING {SkillRunColumns}
            """, ("$id", runId), ("$now", now));
        return await ReadOneRunAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<bool> StartQueuedRunAsync(string runId, DateTime now, CancellationToken ct)
        => await ExecuteAsync("""
            UPDATE SkillRuns SET Status = $running, UpdatedAtUtc = $now WHERE RunId = $id AND Status = $queued
            """, ct, ("$running", nameof(SkillRunStatus.Running)), ("$queued", nameof(SkillRunStatus.Queued)),
            ("$now", now), ("$id", runId)).ConfigureAwait(false) != 0;

    public Task SetPendingApprovalAsync(string runId, SkillRunStatus status, string json, DateTime now, CancellationToken ct)
        => ExecuteAsync("""
            UPDATE SkillRuns SET Status = $status, PendingApprovalJson = $pending, UpdatedAtUtc = $now
            WHERE RunId = $id AND Status IN ($queued, $running, $waiting)
            """, ct, ("$status", status.ToString()), ("$pending", json), ("$now", now), ("$id", runId),
            ("$queued", nameof(SkillRunStatus.Queued)), ("$running", nameof(SkillRunStatus.Running)),
            ("$waiting", nameof(SkillRunStatus.WaitingForApproval)));

    public Task ClearPendingApprovalAsync(string runId, DateTime now, CancellationToken ct)
        => ExecuteAsync("""
            UPDATE SkillRuns SET PendingApprovalJson = NULL, UpdatedAtUtc = $now,
                Status = CASE WHEN Status = $waiting THEN $running ELSE Status END WHERE RunId = $id
            """, ct, ("$waiting", nameof(SkillRunStatus.WaitingForApproval)), ("$running", nameof(SkillRunStatus.Running)),
            ("$now", now), ("$id", runId));

    public async Task<SkillRunEntity?> FinishRunAsync(
        string runId, SkillRunFinalState state, SkillDraftRunEvidence? evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var update = Command(connection, transaction, $"""
            {FinalStateUpdate}
            WHERE RunId = $id AND Status IN ($queued, $running, $waiting)
            RETURNING {SkillRunColumns}
            """, ("$id", runId), ("$queued", nameof(SkillRunStatus.Queued)), ("$running", nameof(SkillRunStatus.Running)),
            ("$waiting", nameof(SkillRunStatus.WaitingForApproval)));
        BindFinalState(update, state);
        var finished = await ReadOneRunAsync(update, ct).ConfigureAwait(false);
        if (finished is not null && evidence is not null)
        {
            await using var draft = Command(connection, transaction, """
                UPDATE SkillDrafts SET CandidateDefinitionJson = $json, UpdatedAtUtc = $now
                WHERE Id = $id AND Revision = $revision AND CandidateDefinitionJson = $expected
                """, ("$json", evidence.NewDefinitionJson), ("$now", evidence.Now), ("$id", evidence.DraftId),
                ("$revision", evidence.Revision), ("$expected", evidence.ExpectedDefinitionJson));
            await draft.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return finished;
    }

    public async Task RecoverRunsAsync(IReadOnlyList<SkillRunRecovery> recoveries, CancellationToken ct)
    {
        if (recoveries.Count == 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var recovery in recoveries)
        {
            await using var update = Command(connection, transaction,
                $"{FinalStateUpdate} WHERE RunId = $id AND Status = $expected",
                ("$id", recovery.RunId), ("$expected", recovery.ExpectedStatus));
            BindFinalState(update, recovery.FinalState);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                continue;
            }

            foreach (var step in recovery.ChangedSteps)
            {
                await using var updateStep = Command(connection, transaction, """
                    UPDATE SkillRunSteps SET State = $state, ErrorCode = $error, ResponseSummary = $response, CompletedAtUtc = $now
                    WHERE RunId = $id AND Sequence = $sequence AND State = $dispatching
                    """, ("$state", step.State), ("$error", step.ErrorCode), ("$response", step.ResponseSummary),
                    ("$now", step.CompletedAtUtc), ("$id", recovery.RunId), ("$sequence", step.Sequence),
                    ("$dispatching", nameof(SkillRunStepState.Dispatching)));
                await updateStep.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task PersistCheckpointAsync(SkillRunStepEntity checkpoint, bool insert, CancellationToken ct)
    {
        if (insert)
        {
            return ExecuteAsync($"""
                INSERT INTO SkillRunSteps ({SkillRunStepColumns})
                VALUES ($id, $sequence, $stepId, $stepType, $state, $request, $response, $evidence, $error, $started, $completed)
                """, ct, ("$id", checkpoint.RunId), ("$sequence", checkpoint.Sequence), ("$stepId", checkpoint.StepId),
                ("$stepType", checkpoint.StepType), ("$state", checkpoint.State), ("$request", checkpoint.RequestSummary),
                ("$response", checkpoint.ResponseSummary), ("$evidence", checkpoint.Evidence), ("$error", checkpoint.ErrorCode),
                ("$started", checkpoint.StartedAtUtc), ("$completed", checkpoint.CompletedAtUtc));
        }

        return ExecuteAsync("""
            UPDATE SkillRunSteps SET State = $state, RequestSummary = COALESCE($request, RequestSummary),
                ResponseSummary = COALESCE($response, ResponseSummary), Evidence = COALESCE($evidence, Evidence),
                ErrorCode = COALESCE($error, ErrorCode), CompletedAtUtc = COALESCE($completed, CompletedAtUtc)
            WHERE RunId = $id AND Sequence = $sequence
            """, ct, ("$state", checkpoint.State), ("$request", checkpoint.RequestSummary), ("$response", checkpoint.ResponseSummary),
            ("$evidence", checkpoint.Evidence), ("$error", checkpoint.ErrorCode), ("$completed", checkpoint.CompletedAtUtc),
            ("$id", checkpoint.RunId), ("$sequence", checkpoint.Sequence));
    }

    public Task UpdateProgressAsync(string runId, int externalRequests, int readOnlyRetries, DateTime now, CancellationToken ct)
        => ExecuteAsync("""
            UPDATE SkillRuns SET ExternalRequestCount = $requests, ReadOnlyRetryCount = $retries, UpdatedAtUtc = $now WHERE RunId = $id
            """, ct, ("$requests", externalRequests), ("$retries", readOnlyRetries), ("$now", now), ("$id", runId));

    private const string FinalStateUpdate = """
        UPDATE SkillRuns SET Status = $status, PendingApprovalJson = NULL,
            ExternalRequestCount = COALESCE($requests, ExternalRequestCount),
            ReadOnlyRetryCount = COALESCE($retries, ReadOnlyRetryCount),
            OutputPayloadJson = CASE WHEN $updateOutput = 1 THEN $output ELSE OutputPayloadJson END,
            ErrorJson = $error, UpdatedAtUtc = $now
        """;

    private static void BindFinalState(SqliteCommand command, SkillRunFinalState state)
    {
        command.Parameters.AddWithValue("$status", state.Status.ToString());
        command.Parameters.AddWithValue("$requests", (object?)state.ExternalRequests ?? DBNull.Value);
        command.Parameters.AddWithValue("$retries", (object?)state.ReadOnlyRetries ?? DBNull.Value);
        command.Parameters.AddWithValue("$updateOutput", state.UpdateOutput);
        command.Parameters.AddWithValue("$output", (object?)state.OutputJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)state.ErrorJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", state.Now);
    }

    private static async Task<SkillRunEntity?> ReadOneRunAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSkillRun(reader) : null;
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var command = Command(connection, null, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
