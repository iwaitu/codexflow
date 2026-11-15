// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Data.Common;
using System.Globalization;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Models.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// <see cref="IJobEventLog"/> in the <c>JobEventLog</c> table of the project's SQLite file (created by the
/// <c>SupportTables</c> migration). Timestamps are stored as ISO-8601 UTC text.
/// </summary>
public sealed class SqliteJobEventLog : IJobEventLog
{
    private readonly CodexFlow.Orchestration.Jobs.IJobDbContext _context;

    public SqliteJobEventLog(CodexFlow.Orchestration.Jobs.IJobDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task AppendAsync(JobEventLogEntry entry, CancellationToken ct = default)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($@"
INSERT OR REPLACE INTO ""JobEventLog""
    (""EventId"", ""JobId"", ""SessionId"", ""TaskId"", ""Seq"", ""EventType"", ""PayloadJson"", ""OccurredAtUtc"", ""LoggedAtUtc"")
VALUES
    ({entry.EventId}, {entry.JobId}, {entry.SessionId}, {entry.TaskId}, {entry.Seq}, {entry.EventType}, {entry.PayloadJson},
     {ToText(entry.OccurredAtUtc)}, {ToText(entry.LoggedAtUtc)})", ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobEventLogEntry>> GetByJobAsync(string jobId, long? afterSeq = null, CancellationToken ct = default)
    {
        var connection = _context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT ""EventId"", ""JobId"", ""SessionId"", ""TaskId"", ""Seq"", ""EventType"", ""PayloadJson"", ""OccurredAtUtc"", ""LoggedAtUtc""
FROM ""JobEventLog"" WHERE ""JobId"" = $jobId AND ""Seq"" > $afterSeq ORDER BY ""Seq"" ASC";
            AddParameter(command, "$jobId", jobId);
            AddParameter(command, "$afterSeq", afterSeq ?? long.MinValue);
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            var result = new List<JobEventLogEntry>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
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
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""JobEventLog"" WHERE ""SessionId"" = {sessionId}", ct);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string ToText(DateTime value)
        => DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromText(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
