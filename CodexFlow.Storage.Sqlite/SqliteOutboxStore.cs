// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteOutboxStore(SqliteConnectionFactory connections) : IOutboxStore
{
    public async Task AppendAsync(OutboxEvent entry, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var sequence = await InsertAsync(connection, transaction, entry, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        entry.Seq = sequence;
    }

    internal static async Task<long> InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, OutboxEvent entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sequence = entry.Seq;
        if (sequence == 0)
        {
            command.CommandText = """UPDATE OutboxSeq SET Value = Value + 1 WHERE Id = 1 RETURNING Value""";
            sequence = (long?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OutboxSeq is missing its counter row.");
        }

        command.CommandText = """
            INSERT INTO OutboxEvents (EventId, EventType, JobId, SessionId, TaskId, Seq, Payload,
                                      CorrelationId, DedupeKey, OccurredAtUtc, ProcessedAt, RetryCount, LastError)
            VALUES ($id, $type, $job, $session, $task, $seq, $payload, $correlation, $dedupe,
                    $occurred, $processed, $retry, $error)
            """;
        Add(command, "$id", entry.EventId);
        Add(command, "$type", entry.EventType);
        Add(command, "$job", entry.JobId);
        Add(command, "$session", entry.SessionId);
        Add(command, "$task", entry.TaskId);
        Add(command, "$seq", sequence);
        Add(command, "$payload", entry.Payload);
        Add(command, "$correlation", entry.CorrelationId);
        Add(command, "$dedupe", entry.DedupeKey);
        Add(command, "$occurred", entry.OccurredAtUtc);
        Add(command, "$processed", entry.ProcessedAt);
        Add(command, "$retry", entry.RetryCount);
        Add(command, "$error", entry.LastError);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return sequence;
    }

    public async Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(DateTime cutoffUtc, int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, EventType, JobId, SessionId, TaskId, Seq, Payload,
                   CorrelationId, DedupeKey, OccurredAtUtc, RetryCount, LastError
            FROM OutboxEvents WHERE ProcessedAt IS NULL AND OccurredAtUtc < $cutoff ORDER BY Seq LIMIT $limit
            """;
        Add(command, "$cutoff", cutoffUtc);
        Add(command, "$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var pending = new List<OutboxEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            pending.Add(new OutboxEvent
            {
                EventId = reader.GetString(0),
                EventType = reader.GetString(1),
                JobId = Text(reader, 2),
                SessionId = Text(reader, 3),
                TaskId = Text(reader, 4),
                Seq = reader.GetInt64(5),
                Payload = reader.GetString(6),
                CorrelationId = Text(reader, 7),
                DedupeKey = Text(reader, 8),
                OccurredAtUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
                RetryCount = reader.GetInt32(10),
                LastError = Text(reader, 11)
            });
        }

        return pending;
    }

    public async Task RecordProjectionAsync(IReadOnlyList<OutboxProjectionResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;

        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE OutboxEvents SET ProcessedAt = $processed,
                RetryCount = CASE WHEN $processed IS NULL THEN RetryCount + 1 ELSE RetryCount END,
                LastError = CASE WHEN $processed IS NULL THEN $error ELSE LastError END
            WHERE EventId = $id AND ProcessedAt IS NULL
            """;
        foreach (var result in results)
        {
            command.Parameters.Clear();
            Add(command, "$id", result.EventId);
            Add(command, "$processed", result.ProcessedAtUtc);
            Add(command, "$error", result.Error);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task AdvanceLastEventSeqAsync(string jobId, long sequence, DateTime now, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BackgroundJobs SET LastEventSeq = $seq, UpdatedAtUtc = $now WHERE JobId = $job AND LastEventSeq < $seq
            """;
        Add(command, "$job", jobId);
        Add(command, "$seq", sequence);
        Add(command, "$now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<OutboxJobSnapshot?> FindJobAsync(string jobId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.JobId, j.SessionId, j.TaskId, j.JobType, j.RunnerId, j.Attempt, j.Status,
                   j.PayloadJson, j.ResultPayloadJson, j.ShortSummary, j.WaitingReason, j.ErrorMessage,
                   (SELECT c.CurrentStep FROM JobCheckpoints c WHERE c.JobId = j.JobId ORDER BY c.SequenceNumber DESC LIMIT 1)
            FROM BackgroundJobs j WHERE j.JobId = $job
            """;
        Add(command, "$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new OutboxJobSnapshot(reader.GetString(0), Text(reader, 1), Text(reader, 2), reader.GetString(3),
                Text(reader, 4), reader.GetInt32(5), (BackgroundJobStatus)reader.GetInt32(6), reader.GetString(7),
                Text(reader, 8), Text(reader, 9), Text(reader, 10), Text(reader, 11), Text(reader, 12))
            : null;
    }

    private static string? Text(SqliteDataReader reader, int column)
        => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
