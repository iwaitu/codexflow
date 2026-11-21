// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Models.Jobs;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// <see cref="IJobEventLog"/> in the <c>JobEventLog</c> table. Timestamps use ISO-8601 UTC text.
/// </summary>
public sealed class SqliteJobEventLog(SqliteConnectionFactory connections) : IJobEventLog
{
    /// <inheritdoc />
    public async Task AppendAsync(JobEventLogEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO "JobEventLog"
                ("EventId", "JobId", "SessionId", "TaskId", "Seq", "EventType", "PayloadJson", "OccurredAtUtc", "LoggedAtUtc")
            VALUES
                ($eventId, $jobId, $sessionId, $taskId, $seq, $eventType, $payloadJson, $occurredAtUtc, $loggedAtUtc)
            """;
        command.Parameters.AddWithValue("$eventId", entry.EventId);
        command.Parameters.AddWithValue("$jobId", (object?)entry.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)entry.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)entry.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$seq", entry.Seq);
        command.Parameters.AddWithValue("$eventType", entry.EventType);
        command.Parameters.AddWithValue("$payloadJson", entry.PayloadJson);
        command.Parameters.AddWithValue("$occurredAtUtc", ToText(entry.OccurredAtUtc));
        command.Parameters.AddWithValue("$loggedAtUtc", ToText(entry.LoggedAtUtc));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobEventLogEntry>> GetByJobAsync(string jobId, long? afterSeq = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "EventId", "JobId", "SessionId", "TaskId", "Seq", "EventType", "PayloadJson", "OccurredAtUtc", "LoggedAtUtc"
            FROM "JobEventLog" WHERE "JobId" = $jobId AND "Seq" > $afterSeq ORDER BY "Seq" ASC
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$afterSeq", afterSeq ?? long.MinValue);

        var result = new List<JobEventLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new JobEventLogEntry
            {
                EventId = reader.GetString(0),
                JobId = reader.IsDBNull(1) ? null : reader.GetString(1),
                SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                TaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Seq = reader.GetInt64(4),
                EventType = reader.GetString(5),
                PayloadJson = reader.GetString(6),
                OccurredAtUtc = FromText(reader.GetString(7)),
                LoggedAtUtc = FromText(reader.GetString(8))
            });
        }

        return result;
    }

    /// <inheritdoc />
    public async Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """DELETE FROM "JobEventLog" WHERE "SessionId" = $sessionId""";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string ToText(DateTime value)
        => DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromText(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
