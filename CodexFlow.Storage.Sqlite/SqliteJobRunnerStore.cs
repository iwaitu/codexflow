// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteJobRunnerStore(SqliteConnectionFactory connections) : IJobRunnerStore
{
    public async Task<bool> TryCommitAsync(string jobId, JobRunnerFields before, JobRunnerFields after,
        JobCheckpoint? checkpoint, OutboxEvent? entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var changes = new List<string>();

        AddChange("Status", (int)before.Status, (int)after.Status);
        AddChange("PayloadJson", before.PayloadJson, after.PayloadJson);
        AddChange("ResultPayloadJson", before.ResultPayloadJson, after.ResultPayloadJson);
        AddChange("ErrorMessage", before.ErrorMessage, after.ErrorMessage);
        AddChange("RunnerId", before.RunnerId, after.RunnerId);
        AddChange("ClaimToken", before.ClaimToken, after.ClaimToken);
        AddChange("LeaseExpiresAtUtc", before.LeaseExpiresAtUtc, after.LeaseExpiresAtUtc);
        AddChange("DequeuedAtUtc", before.DequeuedAtUtc, after.DequeuedAtUtc);
        AddChange("Attempt", before.Attempt, after.Attempt);
        AddChange("WaitingReason", before.WaitingReason, after.WaitingReason);
        AddChange("CreatedAtUtc", before.CreatedAtUtc, after.CreatedAtUtc);
        AddChange("StartedAtUtc", before.StartedAtUtc, after.StartedAtUtc);
        AddChange("FinishedAtUtc", before.FinishedAtUtc, after.FinishedAtUtc);
        AddChange("UpdatedAtUtc", before.UpdatedAtUtc, after.UpdatedAtUtc);
        AddChange("NotificationStatus", before.NotificationStatus?.ToString(), after.NotificationStatus?.ToString());

        if (changes.Count == 0)
        {
            // Take the same guarded write path when only a checkpoint or event needs persisting.
            changes.Add("JobId = JobId");
        }

        command.CommandText = $"""
            UPDATE BackgroundJobs SET {string.Join(", ", changes)}
            WHERE JobId = $job AND Status = $status AND ClaimToken IS $token AND Attempt = $attempt
                AND ($sequence IS NULL OR
                    COALESCE((SELECT MAX(SequenceNumber) FROM JobCheckpoints WHERE JobId = $job), -1) + 1 = $sequence)
            """;
        Add(command, "$job", jobId);
        Add(command, "$status", (int)before.Status);
        Add(command, "$token", before.ClaimToken);
        Add(command, "$attempt", before.Attempt);
        Add(command, "$sequence", checkpoint?.SequenceNumber);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            return false;
        }

        if (checkpoint is not null)
        {
            await SqliteJobStore.InsertJobCheckpointAsync(connection, transaction, checkpoint, ct).ConfigureAwait(false);
        }
        var sequence = entry is not null
            ? await SqliteOutboxStore.InsertAsync(connection, transaction, entry, ct).ConfigureAwait(false)
            : (long?)null;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        if (entry is not null && sequence.HasValue)
        {
            entry.Seq = sequence.Value;
        }
        return true;

        void AddChange<T>(string column, T oldValue, T newValue)
        {
            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                // Column names come exclusively from the fixed list above.
                var parameter = $"$new{column}";
                changes.Add($"{column} = {parameter}");
                Add(command, parameter, newValue);
            }
        }
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
