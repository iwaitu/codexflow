// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteJobLeaseRecoveryStore(SqliteConnectionFactory connections) : IJobLeaseRecoveryStore
{
    public async Task<bool> VerifyOrDeferClaimAsync(
        string jobId, string claimToken, DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "CreatedAtUtc", "PayloadJson" FROM "BackgroundJobs"
            WHERE "JobId" = $job AND "ClaimToken" = $token AND "Status" = $running
            """;
        Add(command, "$job", jobId);
        Add(command, "$token", claimToken);
        Add(command, "$running", (int)BackgroundJobStatus.Running);

        DateTime earliestRunAt;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return false;
            }

            earliestRunAt = JobLeaseEligibility.GetEarliestRunAt(reader.GetDateTime(0), reader.GetString(1));
            if (earliestRunAt <= now)
            {
                return true;
            }
        }

        command.CommandText = """
            UPDATE "BackgroundJobs" SET "Status" = $queued, "CreatedAtUtc" = $eligible,
                "RunnerId" = NULL, "ClaimToken" = NULL, "LeaseExpiresAtUtc" = NULL,
                "DequeuedAtUtc" = NULL, "StartedAtUtc" = NULL, "FinishedAtUtc" = NULL, "UpdatedAtUtc" = $updated
            WHERE "JobId" = $job AND "ClaimToken" = $token AND "Status" = $running
            """;
        Add(command, "$queued", (int)BackgroundJobStatus.Queued);
        Add(command, "$eligible", earliestRunAt);
        Add(command, "$updated", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return false;
    }

    public async Task<IReadOnlyList<ExpiredJobLease>> ReadExpiredAsync(DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "JobId", "JobType", "SessionId", "TaskId", "ClaimToken", "LeaseExpiresAtUtc", "HeartbeatAtUtc"
            FROM "BackgroundJobs" WHERE "Status" = $running AND "LeaseExpiresAtUtc" < $now
            """;
        Add(command, "$running", (int)BackgroundJobStatus.Running);
        Add(command, "$now", now);
        var jobs = new List<ExpiredJobLease>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            jobs.Add(new ExpiredJobLease(reader.GetString(0), reader.GetString(1),
                ReadNullableText(reader, 2), ReadNullableText(reader, 3), ReadNullableText(reader, 4),
                reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }

        return jobs;
    }

    public async Task<bool> RecoverExpiredAsync(
        IReadOnlyList<ExpiredJobLeaseRecovery> recoveries, DateTime now, CancellationToken ct)
    {
        if (recoveries.Count == 0)
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "BackgroundJobs" SET "Status" = $newStatus, "RunnerId" = NULL, "ClaimToken" = NULL,
                "LeaseExpiresAtUtc" = NULL, "UpdatedAtUtc" = $now, "ErrorMessage" = $error,
                "FinishedAtUtc" = CASE WHEN $recovery THEN COALESCE("FinishedAtUtc", $now) ELSE NULL END,
                "NotificationStatus" = CASE WHEN $recovery THEN $pending ELSE "NotificationStatus" END
            WHERE "JobId" = $job AND "Status" = $running AND "ClaimToken" IS $token
                AND "LeaseExpiresAtUtc" = $expiry AND "LeaseExpiresAtUtc" < $now AND "HeartbeatAtUtc" IS $heartbeat
                AND "JobType" = $type AND "SessionId" IS $session AND "TaskId" IS $task
            """;
        var changed = false;
        var events = new List<(OutboxEvent Entry, long Sequence)>();
        foreach (var update in recoveries)
        {
            var lease = update.Lease;
            var needsRecovery = update.Event is not null;
            command.Parameters.Clear();
            Add(command, "$newStatus", (int)(needsRecovery ? BackgroundJobStatus.FailedRecoveryNeeded : BackgroundJobStatus.Queued));
            Add(command, "$now", now);
            Add(command, "$recovery", needsRecovery);
            Add(command, "$error", update.ErrorMessage);
            Add(command, "$pending", nameof(NotificationStatus.Pending));
            Add(command, "$job", lease.JobId);
            Add(command, "$running", (int)BackgroundJobStatus.Running);
            Add(command, "$token", lease.ClaimToken);
            Add(command, "$expiry", lease.ExpiresAtUtc);
            Add(command, "$heartbeat", lease.HeartbeatAtUtc);
            Add(command, "$type", lease.JobType);
            Add(command, "$session", lease.SessionId);
            Add(command, "$task", lease.TaskId);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                continue;
            }

            changed = true;
            if (update.Event is { } entry)
            {
                var sequence = await SqliteOutboxStore.InsertAsync(connection, transaction, entry, ct).ConfigureAwait(false);
                events.Add((entry, sequence));
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (var (entry, sequence) in events)
        {
            entry.Seq = sequence;
        }

        return changed;
    }

    private static string? ReadNullableText(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
