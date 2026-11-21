// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Upgrades the Local state database using the historical SQLite migration IDs.</summary>
public sealed class SqliteSchemaMigrator
{
    private const string CreateHistoryTable = """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
            "ProductVersion" TEXT NOT NULL
        );
        """;

    private static readonly string[] MigrationIds =
    [
        "20261113221012_Initial",
        "20261116134656_AddSessionArchiveAndExternalWorkspace",
        "20261117061315_AddBusinessPluginBuilderTables",
        "20261117191659_AddSessionWorkModeAndPlanMode",
    ];

    private readonly SqliteConnectionFactory _connections;

    public SqliteSchemaMigrator(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Reserve the writer before reading history so two processes cannot select the same next migration.
            await using var transaction = connection.BeginTransaction(deferred: false);
            await EnsureNoLegacyMigrationLockAsync(transaction, ct).ConfigureAwait(false);
            var appliedCount = await CountAppliedPrefixAsync(transaction, ct).ConfigureAwait(false);
            if (appliedCount == MigrationIds.Length)
            {
                return;
            }

            if (appliedCount == 0)
            {
                await EnsureEmptySchemaAsync(transaction, ct).ConfigureAwait(false);
                await using var create = CreateCommand(transaction, CreateHistoryTable);
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await ApplyMigrationAsync(transaction, MigrationIds[appliedCount], ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureNoLegacyMigrationLockAsync(SqliteTransaction transaction, CancellationToken ct)
    {
        if (!await HasTableAsync(transaction, "__EFMigrationsLock", ct).ConfigureAwait(false))
        {
            return;
        }

        await using var command = CreateCommand(transaction, """SELECT 1 FROM "__EFMigrationsLock" LIMIT 1;""");
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "The SQLite database has an EF migration lock. Stop the old host and verify that no EF migration is running before clearing a stale lock.");
        }
    }

    private static async Task<int> CountAppliedPrefixAsync(SqliteTransaction transaction, CancellationToken ct)
    {
        if (!await HasTableAsync(transaction, "__EFMigrationsHistory", ct).ConfigureAwait(false))
        {
            return 0;
        }

        await using var command = CreateCommand(transaction, """
            SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE BINARY;
            """);
        await using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var count = 0;
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
        {
            var migration = rows.GetString(0);
            if (count == MigrationIds.Length || !string.Equals(migration, MigrationIds[count], StringComparison.Ordinal))
            {
                var expected = count < MigrationIds.Length ? MigrationIds[count] : "end of migration history";
                throw new InvalidOperationException(
                    $"The SQLite database has unknown or noncontiguous migration history. Expected '{expected}', found '{migration}'.");
            }

            count++;
        }

        return count;
    }

    private static async Task EnsureEmptySchemaAsync(SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = CreateCommand(transaction, """
            SELECT name FROM sqlite_schema
            WHERE lower(substr(name, 1, 7)) <> 'sqlite_'
              AND name COLLATE NOCASE NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
            ORDER BY name
            LIMIT 1;
            """);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string orphan)
        {
            throw new InvalidOperationException(
                $"The SQLite database has no recognized migration history and already contains '{orphan}'.");
        }
    }

    private static async Task ApplyMigrationAsync(SqliteTransaction transaction, string migrationId, CancellationToken ct)
    {
        var assembly = typeof(SqliteSchemaMigrator).Assembly;
        var resourceName = $"CodexFlow.Storage.Sqlite.SchemaMigrations.{migrationId}.sql";
        await using var script = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The SQL resource for migration '{migrationId}' is missing.");
        using var reader = new StreamReader(script, leaveOpen: true);
        var sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        try
        {
            await using var migrate = CreateCommand(transaction, sql);
            await migrate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var record = CreateCommand(transaction, """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, '10.0.15');
                """);
            record.Parameters.AddWithValue("$migrationId", migrationId);
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"Could not apply SQLite migration '{migrationId}'.", ex);
        }
    }

    private static async Task<bool> HasTableAsync(SqliteTransaction transaction, string table, CancellationToken ct)
    {
        await using var command = CreateCommand(transaction, """
            SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $table COLLATE NOCASE LIMIT 1;
            """);
        command.Parameters.AddWithValue("$table", table);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static SqliteCommand CreateCommand(SqliteTransaction transaction, string sql)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
