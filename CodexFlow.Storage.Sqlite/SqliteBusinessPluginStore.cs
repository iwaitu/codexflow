// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteBusinessPluginStore(SqliteConnectionFactory connections) : IBusinessPluginStore
{
    private const string PluginColumns = "Id, Name, SourceType, ConnectionId, Enabled, ActiveSnapshotId, ArchivedAtUtc, LastDiscoveredAtUtc, CreatedAtUtc, UpdatedAtUtc";
    private const string ConnectionColumns = """
        Id, PluginId, SourceUrl, Environment, EndpointPolicyJson, DocumentCredentialRef, ExecutionCredentialRef,
        Revision, CredentialRevision, IsEncryptedTransport, CreatedAtUtc, UpdatedAtUtc
        """;
    private const string CredentialColumns = "CredentialRef, AuthKind, MaskedHint, Revision, CipherFileName, CreatedAtUtc, UpdatedAtUtc";
    private const string CapabilityColumns = """
        SnapshotId, Id, NativeIdentity, ModelAlias, Title, Description, SourceDescriptionMissing, GroupTag, Risk,
        SuggestedSourceRiskHint, Executability, DiagnosticsJson, InputSchemaJson, OutputSchemaJson, InvocationJson
        """;
    private const string SnapshotColumns = """
        Id, PluginId, ConnectionRevision, CredentialRevision, Environment, ProtocolVersion, SourceHash, SchemaHash,
        Complete, TotalCapabilities, ExecutableCount, NeedsConfigurationCount, UnsupportedCount, DiagnosticsJson, FetchedAtUtc
        """;
    private const string OperationColumns = """
        OperationId, Kind, TargetId, ClientRequestId, Status, Sequence, Version, SnapshotId, ResultMessage, ErrorJson, CreatedAtUtc, UpdatedAtUtc
        """;
    private const string CapabilityFilter = """
        WHERE SnapshotId = $snapshot AND ($groupTag IS NULL OR GroupTag = $groupTag)
          AND (NOT $executableOnly OR Executability = $executable)
          AND ($search IS NULL OR instr(Title, $search) > 0 OR instr(Description, $search) > 0 OR instr(NativeIdentity, $search) > 0)
        """;

    public async Task<BusinessPluginDirectory> LoadDirectoryAsync(CancellationToken ct)
    {
        await using var db = connections.Open();
        var plugins = await ReadAsync(db, null, $"SELECT {PluginColumns} FROM BusinessPlugins ORDER BY CreatedAtUtc;", ReadPlugin, ct);
        var conns = await ReadAsync(db, null, $"SELECT {ConnectionColumns} FROM PluginConnections;", ReadConnection, ct);
        var credentials = await ReadAsync(db, null, $"SELECT {CredentialColumns} FROM BusinessCredentialMetas;", ReadCredential, ct);
        var counts = await ReadAsync(db, null, """
            SELECT SnapshotId, count(*) FROM CatalogCapabilities c
            WHERE EXISTS (SELECT 1 FROM BusinessPlugins p WHERE p.ActiveSnapshotId = c.SnapshotId)
            GROUP BY SnapshotId;
            """, row => (Id: row.GetString(0), Count: row.GetInt32(1)), ct);
        return new BusinessPluginDirectory(plugins, conns.ToDictionary(x => x.Id, StringComparer.Ordinal),
            credentials.ToDictionary(x => x.CredentialRef, StringComparer.Ordinal),
            counts.ToDictionary(x => x.Id, x => x.Count, StringComparer.Ordinal));
    }

    public Task<BusinessPluginEntity?> FindPluginAsync(string pluginId, CancellationToken ct)
        => FindAsync($"SELECT {PluginColumns} FROM BusinessPlugins WHERE Id = $id;", ReadPlugin, ct, ("$id", pluginId));

    public Task<PluginConnectionEntity?> FindConnectionAsync(string connectionId, CancellationToken ct)
        => FindAsync($"SELECT {ConnectionColumns} FROM PluginConnections WHERE Id = $id;", ReadConnection, ct, ("$id", connectionId));

    public Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct)
        => FindAsync($"SELECT {CredentialColumns} FROM BusinessCredentialMetas WHERE CredentialRef = $id;", ReadCredential, ct, ("$id", credentialRef));

    public async Task<List<string>> FindOtherCredentialUsersAsync(string pluginId, string credentialRef, CancellationToken ct)
    {
        await using var db = connections.Open();
        return await ReadAsync(db, null, """
            SELECT DISTINCT PluginId FROM PluginConnections
            WHERE PluginId <> $plugin AND (DocumentCredentialRef = $ref OR ExecutionCredentialRef = $ref);
            """, row => row.GetString(0), ct, ("$plugin", pluginId), ("$ref", credentialRef));
    }

    public async Task<int> CountCapabilitiesAsync(string snapshotId, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var count = Command(db, null, "SELECT count(*) FROM CatalogCapabilities WHERE SnapshotId = $snapshot;", ("$snapshot", snapshotId));
        return Convert.ToInt32(await count.ExecuteScalarAsync(ct));
    }

    public async Task<List<CatalogCapabilityEntity>> ReadCapabilitiesAsync(string snapshotId, CancellationToken ct)
    {
        await using var db = connections.Open();
        return await ReadAsync(db, null, $"SELECT {CapabilityColumns} FROM CatalogCapabilities WHERE SnapshotId = $snapshot;",
            ReadCapability, ct, ("$snapshot", snapshotId));
    }

    public Task<CatalogSnapshotEntity?> FindSnapshotAsync(string snapshotId, string? pluginId, CancellationToken ct)
        => FindAsync($"SELECT {SnapshotColumns} FROM CatalogSnapshots WHERE Id = $id AND ($plugin IS NULL OR PluginId = $plugin);",
            ReadSnapshot, ct, ("$id", snapshotId), ("$plugin", pluginId));

    public Task<CatalogCapabilityEntity?> FindCapabilityAsync(string snapshotId, string capabilityId, CancellationToken ct)
        => FindAsync($"SELECT {CapabilityColumns} FROM CatalogCapabilities WHERE SnapshotId = $snapshot AND Id = $id;",
            ReadCapability, ct, ("$snapshot", snapshotId), ("$id", capabilityId));

    public async Task<CatalogCapabilityPage> QueryCapabilitiesAsync(
        string snapshotId, string? search, string? groupTag, bool executableOnly, int page, int pageSize, CancellationToken ct)
    {
        await using var db = connections.Open();
        (string, object?)[] filters =
        [
            ("$snapshot", snapshotId), ("$search", search), ("$groupTag", groupTag),
            ("$executableOnly", executableOnly), ("$executable", nameof(CapabilityExecutabilityStatus.Executable))
        ];
        await using var count = Command(db, null, $"SELECT count(*) FROM CatalogCapabilities {CapabilityFilter};", filters);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var rows = await ReadAsync(db, null, $"""
            SELECT {CapabilityColumns} FROM CatalogCapabilities {CapabilityFilter}
            ORDER BY GroupTag, Title, Id LIMIT $limit OFFSET $offset;
            """, ReadCapability, ct, [.. filters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
        return new CatalogCapabilityPage(rows, total);
    }

    public Task<PluginOperationEntity?> FindOperationAsync(string operationId, CancellationToken ct)
        => FindAsync($"SELECT {OperationColumns} FROM PluginOperations WHERE OperationId = $id;", ReadOperation, ct, ("$id", operationId));

    public async Task<PluginOperationEntity?> FindRequestAsync(string kind, string pluginId, string clientRequestId, CancellationToken ct)
    {
        await using var db = connections.Open();
        return await FindRequestAsync(db, null, kind, pluginId, clientRequestId, ct);
    }

    public async Task CreatePluginAsync(BusinessPluginEntity plugin, PluginConnectionEntity connection, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var transaction = db.BeginTransaction(deferred: false);
        await ExecuteAsync(db, transaction, $"""
            INSERT INTO BusinessPlugins ({PluginColumns})
            VALUES ($id, $name, $type, $connection, $enabled, $snapshot, $archived, $discovered, $created, $updated);
            """, ct, ("$id", plugin.Id), ("$name", plugin.Name), ("$type", plugin.SourceType), ("$connection", plugin.ConnectionId),
            ("$enabled", plugin.Enabled), ("$snapshot", plugin.ActiveSnapshotId), ("$archived", plugin.ArchivedAtUtc),
            ("$discovered", plugin.LastDiscoveredAtUtc), ("$created", plugin.CreatedAtUtc), ("$updated", plugin.UpdatedAtUtc));
        await ExecuteAsync(db, transaction, $"""
            INSERT INTO PluginConnections ({ConnectionColumns})
            VALUES ($id, $plugin, $source, $environment, $policy, $document, $execution, $revision, $credRevision, $encrypted, $created, $updated);
            """, ct, ConnectionParameters(connection));
        await transaction.CommitAsync(ct);
    }

    public async Task<PluginConnectionSaveResult> TrySaveConnectionAsync(
        BusinessPluginEntity plugin, PluginConnectionEntity connection, int expectedRevision,
        BusinessCredentialMetaEntity? newCredential, string? removeCredentialRef, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var transaction = db.BeginTransaction(deferred: false);
        var updated = await ExecuteAsync(db, transaction, """
            UPDATE PluginConnections SET SourceUrl = $source, Environment = $environment, EndpointPolicyJson = $policy,
                DocumentCredentialRef = $document, ExecutionCredentialRef = $execution, Revision = $revision,
                CredentialRevision = $credRevision, IsEncryptedTransport = $encrypted, UpdatedAtUtc = $updated
            WHERE Id = $id AND PluginId = $plugin AND Revision = $expected;
            """, ct, [.. ConnectionParameters(connection), ("$expected", expectedRevision)]);
        if (updated == 0)
        {
            return new PluginConnectionSaveResult(false);
        }

        await ExecuteAsync(db, transaction, "UPDATE BusinessPlugins SET UpdatedAtUtc = $now WHERE Id = $plugin;", ct,
            ("$now", plugin.UpdatedAtUtc), ("$plugin", plugin.Id));
        if (newCredential is not null)
        {
            await ExecuteAsync(db, transaction, $"""
                INSERT INTO BusinessCredentialMetas ({CredentialColumns})
                VALUES ($ref, $auth, $hint, $revision, $file, $created, $updated)
                ON CONFLICT(CredentialRef) DO UPDATE SET AuthKind = excluded.AuthKind, MaskedHint = excluded.MaskedHint,
                    Revision = excluded.Revision, CipherFileName = excluded.CipherFileName, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """, ct, ("$ref", newCredential.CredentialRef), ("$auth", newCredential.AuthKind), ("$hint", newCredential.MaskedHint),
                ("$revision", newCredential.Revision), ("$file", newCredential.CipherFileName),
                ("$created", newCredential.CreatedAtUtc), ("$updated", newCredential.UpdatedAtUtc));
        }

        var removed = removeCredentialRef is not null && await ExecuteAsync(db, transaction, """
            DELETE FROM BusinessCredentialMetas WHERE CredentialRef = $ref
              AND NOT EXISTS (SELECT 1 FROM PluginConnections WHERE DocumentCredentialRef = $ref OR ExecutionCredentialRef = $ref);
            """, ct, ("$ref", removeCredentialRef)) != 0;
        await MarkDefinitionsAsync(db, transaction, plugin.Id, ct);
        await transaction.CommitAsync(ct);
        return new PluginConnectionSaveResult(true, removed);
    }

    public async Task<bool> DisablePluginAsync(string pluginId, DateTime now, CancellationToken ct)
    {
        await using var db = connections.Open();
        return await ExecuteAsync(db, null, """
            UPDATE BusinessPlugins SET Enabled = 0, ArchivedAtUtc = coalesce(ArchivedAtUtc, $now), UpdatedAtUtc = $now WHERE Id = $id;
            """, ct, ("$id", pluginId), ("$now", now)) != 0;
    }

    public async Task<PluginOperationEntity?> TryRegisterOperationAsync(PluginOperationEntity op, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var transaction = db.BeginTransaction(deferred: false);
        var inserted = await ExecuteAsync(db, transaction, $"""
            INSERT INTO PluginOperations ({OperationColumns})
            VALUES ($id, $kind, $target, $request, $status, $sequence, $version, $snapshot, $message, $error, $created, $updated)
            ON CONFLICT(Kind, TargetId, ClientRequestId) DO NOTHING;
            """, ct, ("$id", op.OperationId), ("$kind", op.Kind), ("$target", op.TargetId), ("$request", op.ClientRequestId),
            ("$status", op.Status), ("$sequence", op.Sequence), ("$version", op.Version), ("$snapshot", op.SnapshotId),
            ("$message", op.ResultMessage), ("$error", op.ErrorJson), ("$created", op.CreatedAtUtc), ("$updated", op.UpdatedAtUtc));
        var existing = inserted == 0
            ? await FindRequestAsync(db, transaction, op.Kind, op.TargetId, op.ClientRequestId, ct)
            : null;
        await transaction.CommitAsync(ct);
        return existing;
    }

    public async Task SaveOperationResultAsync(PluginOperationEntity operation, CancellationToken ct)
    {
        await using var db = connections.Open();
        await SaveOperationResultAsync(db, null, operation, ct);
    }

    public async Task<bool> TryActivateCatalogAsync(
        BusinessPluginEntity plugin, string? previousSnapshotId, CatalogSnapshotEntity snapshot,
        IReadOnlyList<CatalogCapabilityEntity> capabilities, PluginOperationEntity operation,
        bool needsRevalidation, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var transaction = db.BeginTransaction(deferred: false);
        var updated = await ExecuteAsync(db, transaction, """
            UPDATE BusinessPlugins SET ActiveSnapshotId = $snapshot, LastDiscoveredAtUtc = $discovered, UpdatedAtUtc = $updated
            WHERE Id = $id AND Enabled = 1 AND ActiveSnapshotId IS $previous
              AND EXISTS (SELECT 1 FROM PluginConnections c WHERE c.Id = BusinessPlugins.ConnectionId
                          AND c.Revision = $revision AND c.CredentialRevision = $credRevision);
            """, ct, ("$snapshot", snapshot.Id), ("$discovered", plugin.LastDiscoveredAtUtc), ("$updated", plugin.UpdatedAtUtc),
            ("$id", plugin.Id), ("$previous", previousSnapshotId), ("$revision", snapshot.ConnectionRevision), ("$credRevision", snapshot.CredentialRevision));
        if (updated == 0)
        {
            return false;
        }

        await ExecuteAsync(db, transaction, $"""
            INSERT INTO CatalogSnapshots ({SnapshotColumns})
            VALUES ($id, $plugin, $revision, $credential, $environment, $protocol, $source, $schema, $complete,
                $total, $executable, $configure, $unsupported, $diagnostics, $fetched);
            """, ct, ("$id", snapshot.Id), ("$plugin", snapshot.PluginId), ("$revision", snapshot.ConnectionRevision),
            ("$credential", snapshot.CredentialRevision), ("$environment", snapshot.Environment), ("$protocol", snapshot.ProtocolVersion),
            ("$source", snapshot.SourceHash), ("$schema", snapshot.SchemaHash), ("$complete", snapshot.Complete),
            ("$total", snapshot.TotalCapabilities), ("$executable", snapshot.ExecutableCount), ("$configure", snapshot.NeedsConfigurationCount),
            ("$unsupported", snapshot.UnsupportedCount), ("$diagnostics", snapshot.DiagnosticsJson), ("$fetched", snapshot.FetchedAtUtc));
        foreach (var cap in capabilities)
        {
            await ExecuteAsync(db, transaction, $"""
                INSERT INTO CatalogCapabilities ({CapabilityColumns})
                VALUES ($snapshot, $id, $identity, $alias, $title, $description, $descriptionMissing, $groupTag, $risk,
                    $riskHint, $executable, $diagnostics, $input, $output, $invocation);
                """, ct, ("$snapshot", cap.SnapshotId), ("$id", cap.Id), ("$identity", cap.NativeIdentity), ("$alias", cap.ModelAlias),
                ("$title", cap.Title), ("$description", cap.Description), ("$descriptionMissing", cap.SourceDescriptionMissing),
                ("$groupTag", cap.GroupTag), ("$risk", cap.Risk), ("$riskHint", cap.SuggestedSourceRiskHint),
                ("$executable", cap.Executability), ("$diagnostics", cap.DiagnosticsJson), ("$input", cap.InputSchemaJson),
                ("$output", cap.OutputSchemaJson), ("$invocation", cap.InvocationJson));
        }

        await SaveOperationResultAsync(db, transaction, operation, ct);
        if (needsRevalidation)
        {
            await MarkDefinitionsAsync(db, transaction, plugin.Id, ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryUpdateCapabilityRiskAsync(
        string pluginId, string snapshotId, string capabilityId, string risk, CancellationToken ct)
    {
        await using var db = connections.Open();
        await using var transaction = db.BeginTransaction(deferred: false);
        var updated = await ExecuteAsync(db, transaction, """
            UPDATE CatalogCapabilities SET Risk = $risk WHERE SnapshotId = $snapshot AND Id = $id
              AND EXISTS (SELECT 1 FROM BusinessPlugins WHERE Id = $plugin AND ActiveSnapshotId = $snapshot);
            """, ct, ("$risk", risk), ("$snapshot", snapshotId), ("$id", capabilityId), ("$plugin", pluginId));
        if (updated == 0)
        {
            return false;
        }

        await MarkDefinitionsAsync(db, transaction, pluginId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static Task<int> SaveOperationResultAsync(SqliteConnection db, SqliteTransaction? transaction, PluginOperationEntity operation, CancellationToken ct)
        => ExecuteAsync(db, transaction, """
            UPDATE PluginOperations SET Status = $status, Sequence = $sequence, SnapshotId = $snapshot, ResultMessage = $message,
                ErrorJson = $error, UpdatedAtUtc = $updated WHERE OperationId = $id;
            """, ct, ("$status", operation.Status), ("$sequence", operation.Sequence), ("$snapshot", operation.SnapshotId),
            ("$message", operation.ResultMessage), ("$error", operation.ErrorJson), ("$updated", operation.UpdatedAtUtc), ("$id", operation.OperationId));

    private static Task<int> MarkDefinitionsAsync(SqliteConnection db, SqliteTransaction transaction, string pluginId, CancellationToken ct)
        => ExecuteAsync(db, transaction, """
            UPDATE SkillDrafts SET NeedsRevalidation = 1 WHERE PluginId = $plugin AND NeedsRevalidation = 0;
            UPDATE PublishedSkillVersions SET NeedsRevalidation = 1 WHERE NeedsRevalidation = 0
              AND EXISTS (SELECT 1 FROM PublishedSkills WHERE Id = PublishedSkillVersions.SkillId AND PluginId = $plugin);
            """, ct, ("$plugin", pluginId));

    private static async Task<PluginOperationEntity?> FindRequestAsync(
        SqliteConnection db, SqliteTransaction? transaction, string kind, string pluginId, string requestId, CancellationToken ct)
    {
        var rows = await ReadAsync(db, transaction, $"""
            SELECT {OperationColumns} FROM PluginOperations WHERE Kind = $kind AND TargetId = $target AND ClientRequestId = $request;
            """, ReadOperation, ct, ("$kind", kind), ("$target", pluginId), ("$request", requestId));
        return rows.FirstOrDefault();
    }

    private async Task<T?> FindAsync<T>(string sql, Func<SqliteDataReader, T> map, CancellationToken ct,
        params (string, object?)[] args) where T : class
    {
        await using var db = connections.Open();
        await using var command = Command(db, null, sql, args);
        await using var row = await command.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? map(row) : null;
    }

    private static async Task<List<T>> ReadAsync<T>(
        SqliteConnection db, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map,
        CancellationToken ct, params (string, object?)[] args)
    {
        await using var command = Command(db, transaction, sql, args);
        await using var row = await command.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await row.ReadAsync(ct))
        {
            result.Add(map(row));
        }

        return result;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection db, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string, object?)[] args)
    {
        await using var command = Command(db, transaction, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string, object?)[] args)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in args)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        return command;
    }

    private static (string, object?)[] ConnectionParameters(PluginConnectionEntity c)
        => [("$id", c.Id), ("$plugin", c.PluginId), ("$source", c.SourceUrl), ("$environment", c.Environment),
            ("$policy", c.EndpointPolicyJson), ("$document", c.DocumentCredentialRef), ("$execution", c.ExecutionCredentialRef),
            ("$revision", c.Revision), ("$credRevision", c.CredentialRevision), ("$encrypted", c.IsEncryptedTransport),
            ("$created", c.CreatedAtUtc), ("$updated", c.UpdatedAtUtc)];

    private static BusinessPluginEntity ReadPlugin(SqliteDataReader row) => new()
    {
        Id = row.GetString(0), Name = row.GetString(1), SourceType = row.GetString(2), ConnectionId = row.GetString(3),
        Enabled = row.GetBoolean(4), ActiveSnapshotId = Text(row, 5), ArchivedAtUtc = NullableDate(row, 6),
        LastDiscoveredAtUtc = NullableDate(row, 7), CreatedAtUtc = Date(row, 8), UpdatedAtUtc = Date(row, 9),
    };

    private static PluginConnectionEntity ReadConnection(SqliteDataReader row) => new()
    {
        Id = row.GetString(0), PluginId = row.GetString(1), SourceUrl = row.GetString(2), Environment = row.GetString(3),
        EndpointPolicyJson = row.GetString(4), DocumentCredentialRef = Text(row, 5), ExecutionCredentialRef = Text(row, 6),
        Revision = row.GetInt32(7), CredentialRevision = row.GetString(8), IsEncryptedTransport = row.GetBoolean(9),
        CreatedAtUtc = Date(row, 10), UpdatedAtUtc = Date(row, 11),
    };

    private static BusinessCredentialMetaEntity ReadCredential(SqliteDataReader row) => new()
    {
        CredentialRef = row.GetString(0), AuthKind = row.GetString(1), MaskedHint = row.GetString(2), Revision = row.GetString(3),
        CipherFileName = row.GetString(4), CreatedAtUtc = Date(row, 5), UpdatedAtUtc = Date(row, 6),
    };

    private static CatalogCapabilityEntity ReadCapability(SqliteDataReader row) => new()
    {
        SnapshotId = row.GetString(0), Id = row.GetString(1), NativeIdentity = row.GetString(2), ModelAlias = row.GetString(3),
        Title = row.GetString(4), Description = row.GetString(5), SourceDescriptionMissing = row.GetBoolean(6),
        GroupTag = row.GetString(7), Risk = row.GetString(8), SuggestedSourceRiskHint = Text(row, 9),
        Executability = row.GetString(10), DiagnosticsJson = row.GetString(11), InputSchemaJson = Text(row, 12),
        OutputSchemaJson = Text(row, 13), InvocationJson = row.GetString(14),
    };

    private static CatalogSnapshotEntity ReadSnapshot(SqliteDataReader row) => new()
    {
        Id = row.GetString(0), PluginId = row.GetString(1), ConnectionRevision = row.GetInt32(2), CredentialRevision = row.GetString(3),
        Environment = row.GetString(4), ProtocolVersion = row.GetString(5), SourceHash = row.GetString(6), SchemaHash = row.GetString(7),
        Complete = row.GetBoolean(8), TotalCapabilities = row.GetInt32(9), ExecutableCount = row.GetInt32(10),
        NeedsConfigurationCount = row.GetInt32(11), UnsupportedCount = row.GetInt32(12), DiagnosticsJson = row.GetString(13),
        FetchedAtUtc = Date(row, 14),
    };

    private static PluginOperationEntity ReadOperation(SqliteDataReader row) => new()
    {
        OperationId = row.GetString(0), Kind = row.GetString(1), TargetId = row.GetString(2), ClientRequestId = row.GetString(3),
        Status = row.GetString(4), Sequence = row.GetInt64(5), Version = row.GetInt32(6), SnapshotId = Text(row, 7),
        ResultMessage = Text(row, 8), ErrorJson = Text(row, 9), CreatedAtUtc = Date(row, 10), UpdatedAtUtc = Date(row, 11),
    };

    private static string? Text(SqliteDataReader row, int offset) => row.IsDBNull(offset) ? null : row.GetString(offset);
    private static DateTime Date(SqliteDataReader row, int offset) => DateTime.SpecifyKind(row.GetDateTime(offset), DateTimeKind.Utc);
    private static DateTime? NullableDate(SqliteDataReader row, int offset) => row.IsDBNull(offset) ? null : Date(row, offset);
}
