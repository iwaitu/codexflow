// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.Data.Sqlite;
using CodexFlow.Orchestration.Jobs;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Claims and updates job leases using SQLite transactions and conditional writes.</summary>
public sealed class SqliteJobLeaseStore(SqliteConnectionFactory connections) : IJobLeaseStore
{
    private const string Claimable = """
        ("Status" = $queued OR ("Status" = $running AND "LeaseExpiresAtUtc" < $now))
        """;

    public async Task<string?> TryClaimAsync(
        string runnerId, string claimToken, DateTime now, DateTime leaseExpiresAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        // Take the write lock before reading candidates so a separate process cannot claim the same row.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var jobId = await FindRunnableJobIdAsync(connection, transaction, now, ct).ConfigureAwait(false);
        if (jobId is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE "BackgroundJobs"
            SET "Status" = $running, "RunnerId" = $runnerId, "ClaimToken" = $claimToken,
                "LeaseExpiresAtUtc" = $leaseExpiresAtUtc, "DequeuedAtUtc" = $now, "UpdatedAtUtc" = $now
            WHERE "JobId" = $jobId AND {Claimable}
            """;
        AddClaimParameters(command, now);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$runnerId", runnerId);
        command.Parameters.AddWithValue("$claimToken", claimToken);
        command.Parameters.AddWithValue("$leaseExpiresAtUtc", leaseExpiresAtUtc);
        var updated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return updated == 1 ? jobId : null;
    }

    public async Task<bool> RenewAsync(
        string jobId, string claimToken, DateTime now, DateTime leaseExpiresAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "BackgroundJobs"
            SET "LeaseExpiresAtUtc" = $leaseExpiresAtUtc, "HeartbeatAtUtc" = $now, "UpdatedAtUtc" = $now
            WHERE "JobId" = $jobId AND "ClaimToken" = $claimToken AND "Status" = $running
            """;
        AddOwnedLeaseParameters(command, jobId, claimToken, now);
        command.Parameters.AddWithValue("$leaseExpiresAtUtc", leaseExpiresAtUtc);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task ReleaseAsync(string jobId, string claimToken, DateTime now)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "BackgroundJobs"
            SET "RunnerId" = NULL, "ClaimToken" = NULL, "LeaseExpiresAtUtc" = NULL, "UpdatedAtUtc" = $now
            WHERE "JobId" = $jobId AND "ClaimToken" = $claimToken AND "Status" = $running
            """;
        AddOwnedLeaseParameters(command, jobId, claimToken, now);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string?> FindRunnableJobIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTime now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT "JobId", "CreatedAtUtc", "PayloadJson"
            FROM "BackgroundJobs"
            WHERE {Claimable}
            ORDER BY "CreatedAtUtc"
            """;
        AddClaimParameters(command, now);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JobLeaseEligibility.IsRunnableAt(reader.GetDateTime(1), reader.GetString(2), now))
            {
                return reader.GetString(0);
            }
        }

        return null;
    }

    private static void AddClaimParameters(SqliteCommand command, DateTime now)
    {
        command.Parameters.AddWithValue("$queued", (int)BackgroundJobStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)BackgroundJobStatus.Running);
        command.Parameters.AddWithValue("$now", now);
    }

    private static void AddOwnedLeaseParameters(SqliteCommand command, string jobId, string claimToken, DateTime now)
    {
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$claimToken", claimToken);
        command.Parameters.AddWithValue("$running", (int)BackgroundJobStatus.Running);
        command.Parameters.AddWithValue("$now", now);
    }
}
