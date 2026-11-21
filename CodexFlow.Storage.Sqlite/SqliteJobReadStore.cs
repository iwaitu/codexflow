// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteJobReadStore(SqliteConnectionFactory connections) : IJobReadStore
{
    private const string ViewSql = """
        SELECT j.JobId, j.SessionId, j.TaskId, j.JobType, j.RunnerId, j.Attempt, j.Status, j.UpdatedAtUtc,
            j.PayloadJson, j.ResultPayloadJson, j.ShortSummary, j.ErrorMessage, j.WaitingReason,
            (SELECT c.ResumeToken FROM JobCheckpoints c WHERE c.JobId = j.JobId ORDER BY c.SequenceNumber DESC LIMIT 1),
            (SELECT c.CurrentStep FROM JobCheckpoints c WHERE c.JobId = j.JobId ORDER BY c.SequenceNumber DESC LIMIT 1),
            j.LastEventSeq
        FROM BackgroundJobs j
        """;

    public async Task<JobViewSnapshot?> FindViewAsync(string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = ViewSql + " WHERE j.JobId = $job";
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadView(reader) : null;
    }

    public async Task<IReadOnlyList<JobViewSnapshot>> ReadSessionViewsAsync(
        string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = ViewSql + " WHERE j.SessionId = $session ORDER BY j.CreatedAtUtc DESC";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<JobViewSnapshot>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadView(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<string?>> ReadCompletedResultPayloadsAsync(
        string sessionId, string excludingJobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ResultPayloadJson FROM BackgroundJobs
            WHERE SessionId = $session AND JobId <> $excluded AND Status = $completed
            ORDER BY CreatedAtUtc
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$excluded", excludingJobId);
        command.Parameters.AddWithValue("$completed", (int)BackgroundJobStatus.Completed);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var payloads = new List<string?>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            payloads.Add(ReadText(reader, 0));
        }

        return payloads;
    }

    public Task<ActivePlanJob?> FindActiveTaskJobAsync(string sessionId, string taskId, string jobType, CancellationToken ct)
        => FindActiveAsync(sessionId, (taskId, jobType), ct);

    public Task<ActivePlanJob?> FindActiveSessionWorkerAsync(string sessionId, CancellationToken ct)
        => FindActiveAsync(sessionId, null, ct);

    private async Task<ActivePlanJob?> FindActiveAsync(
        string sessionId, (string TaskId, string JobType)? task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.JobId, j.TaskId, j.JobType, j.Status, j.ResultPayloadJson, j.Attempt,
                (SELECT c.CurrentStep FROM JobCheckpoints c WHERE c.JobId = j.JobId ORDER BY c.SequenceNumber DESC LIMIT 1)
            FROM BackgroundJobs j
            WHERE j.SessionId = $session AND j.Status IN ($queued, $running, $waiting, $recovery)
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$queued", (int)BackgroundJobStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)BackgroundJobStatus.Running);
        command.Parameters.AddWithValue("$waiting", (int)BackgroundJobStatus.WaitingUser);
        command.Parameters.AddWithValue("$recovery", (int)BackgroundJobStatus.FailedRecoveryNeeded);
        if (task is { } filter)
        {
            command.CommandText += " AND j.TaskId = $task AND j.JobType = $type ORDER BY j.UpdatedAtUtc DESC LIMIT 1";
            command.Parameters.AddWithValue("$task", filter.TaskId);
            command.Parameters.AddWithValue("$type", filter.JobType);
        }
        else
        {
            command.CommandText += " ORDER BY j.UpdatedAtUtc DESC";
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var jobType = reader.GetString(2);
            if (task is not null || WorkerJobTypes.IsWorkerJobType(jobType))
            {
                return new ActivePlanJob(reader.GetString(0), ReadText(reader, 1), jobType,
                    (BackgroundJobStatus)reader.GetInt32(3), ReadText(reader, 4), reader.GetInt32(5),
                    ReadText(reader, 6));
            }
        }

        return null;
    }

    private static JobViewSnapshot ReadView(SqliteDataReader reader) => new(
        reader.GetString(0), ReadText(reader, 1), ReadText(reader, 2), reader.GetString(3), ReadText(reader, 4),
        reader.GetInt32(5), (BackgroundJobStatus)reader.GetInt32(6), reader.GetDateTime(7), reader.GetString(8),
        ReadText(reader, 9), ReadText(reader, 10), ReadText(reader, 11), ReadText(reader, 12), ReadText(reader, 13),
        ReadText(reader, 14), reader.GetInt64(15));

    private static string? ReadText(SqliteDataReader reader, int column)
        => reader.IsDBNull(column) ? null : reader.GetString(column);
}
