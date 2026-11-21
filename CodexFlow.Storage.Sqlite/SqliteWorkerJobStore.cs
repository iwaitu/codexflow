// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteWorkerJobStore(SqliteConnectionFactory connections) : IWorkerJobStore
{
    public async Task<WorkerContinuationState?> FindContinuationAsync(string jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SqliteJobStore.BackgroundJobColumns} FROM BackgroundJobs WHERE JobId = $job";
        Add(command, "$job", jobId);
        BackgroundJob job;
        await using (var jobReader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await jobReader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            job = SqliteJobStore.ReadJob(jobReader);
        }

        command.CommandText = """
            SELECT ResumeToken FROM JobCheckpoints WHERE JobId = $job AND ResumeToken IS NOT NULL
            ORDER BY SequenceNumber DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var token = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return new WorkerContinuationState(job, token);
            }
        }

        return new WorkerContinuationState(job, null);
    }

    public async Task<IReadOnlyList<BackgroundJob>> ReadSessionJobsAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteJobStore.BackgroundJobColumns}
            FROM BackgroundJobs WHERE SessionId = $session ORDER BY CreatedAtUtc
            """;
        Add(command, "$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var jobs = new List<BackgroundJob>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            jobs.Add(SqliteJobStore.ReadJob(reader));
        }

        return jobs;
    }

    public async Task<bool> TryUpdateWorktreePayloadAsync(
        BackgroundJob expected, string payloadJson, DateTime updatedAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BackgroundJobs SET PayloadJson = $payload, UpdatedAtUtc = $now
            WHERE JobId = $job AND Status = $status AND ClaimToken IS $token AND Attempt = $attempt
                  AND PayloadJson = $expected
            """;
        Add(command, "$payload", payloadJson);
        Add(command, "$now", updatedAtUtc);
        Add(command, "$job", expected.JobId);
        Add(command, "$status", (int)expected.Status);
        Add(command, "$token", expected.ClaimToken);
        Add(command, "$attempt", expected.Attempt);
        Add(command, "$expected", expected.PayloadJson);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryUpdateVerificationPayloadAsync(
        BackgroundJob expected, string payloadJson, string? errorMessage, DateTime updatedAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BackgroundJobs SET ResultPayloadJson = $payload, ErrorMessage = $error, UpdatedAtUtc = $now
            WHERE JobId = $job AND Attempt = $attempt AND Status = $completed AND ResultPayloadJson IS $expected
            """;
        Add(command, "$payload", payloadJson);
        Add(command, "$error", errorMessage);
        Add(command, "$now", updatedAtUtc);
        Add(command, "$job", expected.JobId);
        Add(command, "$attempt", expected.Attempt);
        Add(command, "$completed", (int)BackgroundJobStatus.Completed);
        Add(command, "$expected", expected.ResultPayloadJson);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
