// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed partial class SqliteBusinessSkillExecutionStore(SqliteConnectionFactory connections) : IBusinessSkillExecutionStore
{
    private readonly SqliteConnectionFactory _connections = connections;

    private const string BusinessPluginColumns = """
        Id, Name, SourceType, ConnectionId, Enabled, ActiveSnapshotId, ArchivedAtUtc,
        LastDiscoveredAtUtc, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string PluginConnectionColumns = """
        Id, PluginId, SourceUrl, Environment, EndpointPolicyJson, DocumentCredentialRef, ExecutionCredentialRef,
        Revision, CredentialRevision, IsEncryptedTransport, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string CatalogSnapshotColumns = """
        Id, PluginId, ConnectionRevision, CredentialRevision, Environment, ProtocolVersion, SourceHash,
        SchemaHash, Complete, TotalCapabilities, ExecutableCount, NeedsConfigurationCount, UnsupportedCount, DiagnosticsJson,
        FetchedAtUtc
        """;

    private const string SkillDraftColumns = """
        Id, PluginId, CatalogSnapshotId, Revision, Name, BusinessGoal, SelectedCapabilityIdsJson,
        CandidateDefinitionJson, ClarificationQuestionsJson, ValidationErrorsJson, NeedsRevalidation, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string BusinessCredentialMetaColumns = """
        CredentialRef, AuthKind, MaskedHint, Revision, CipherFileName, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string PublishedSkillColumns = """
        Id, Name, Description, PluginId, Environment, ActiveVersion, CreatedAtUtc,
        UpdatedAtUtc
        """;

    private const string PublishedSkillVersionColumns = """
        SkillId, Version, ContentHash, CatalogSnapshotId, DefinitionJson, NeedsRevalidation, WriteStepVerified,
        PublishedAtUtc
        """;

    private const string CatalogCapabilityColumns = """
        SnapshotId, Id, NativeIdentity, ModelAlias, Title, Description, SourceDescriptionMissing,
        GroupTag, Risk, SuggestedSourceRiskHint, Executability, DiagnosticsJson, InputSchemaJson, OutputSchemaJson,
        InvocationJson
        """;

    private const string SkillRunColumns = """
        RunId, SkillId, SkillVersion, ClientRequestId, Status, StopRequested, ExternalRequestCount,
        ReadOnlyRetryCount, InputsJson, OutputPayloadJson, PendingApprovalJson, ErrorJson, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string SkillRunStepColumns = """
        RunId, Sequence, StepId, StepType, State, RequestSummary, ResponseSummary,
        Evidence, ErrorCode, StartedAtUtc, CompletedAtUtc
        """;

    public Task<BusinessPluginEntity?> FindPluginAsync(string id, CancellationToken ct)
        => FindAsync($"SELECT {BusinessPluginColumns} FROM BusinessPlugins WHERE Id = $id", ReadBusinessPlugin, ct, ("$id", id));

    public Task<PluginConnectionEntity?> FindConnectionAsync(string id, string? pluginId, CancellationToken ct)
        => FindAsync($"SELECT {PluginConnectionColumns} FROM PluginConnections WHERE Id = $id AND ($pluginId IS NULL OR PluginId = $pluginId)",
            ReadPluginConnection, ct, ("$id", id), ("$pluginId", pluginId));

    public Task<CatalogSnapshotEntity?> FindSnapshotAsync(string id, string? pluginId, CancellationToken ct)
        => FindAsync($"SELECT {CatalogSnapshotColumns} FROM CatalogSnapshots WHERE Id = $id AND ($pluginId IS NULL OR PluginId = $pluginId)",
            ReadCatalogSnapshot, ct, ("$id", id), ("$pluginId", pluginId));

    public Task<SkillDraftEntity?> FindDraftAsync(string id, CancellationToken ct)
        => FindAsync($"SELECT {SkillDraftColumns} FROM SkillDrafts WHERE Id = $id", ReadSkillDraft, ct, ("$id", id));

    public Task<BusinessCredentialMetaEntity?> FindCredentialAsync(string credentialRef, CancellationToken ct)
        => FindAsync($"SELECT {BusinessCredentialMetaColumns} FROM BusinessCredentialMetas WHERE CredentialRef = $id",
            ReadBusinessCredentialMeta, ct, ("$id", credentialRef));

    public Task<PublishedSkillEntity?> FindPublishedSkillAsync(string id, CancellationToken ct)
        => FindAsync($"SELECT {PublishedSkillColumns} FROM PublishedSkills WHERE Id = $id", ReadPublishedSkill, ct, ("$id", id));

    public Task<PublishedSkillVersionEntity?> FindPublishedVersionAsync(string skillId, int version, CancellationToken ct)
        => FindAsync($"SELECT {PublishedSkillVersionColumns} FROM PublishedSkillVersions WHERE SkillId = $id AND Version = $version",
            ReadPublishedSkillVersion, ct, ("$id", skillId), ("$version", version));

    public async Task<IReadOnlyList<PublishedSkillEntity>> ListEnabledPublishedSkillsAsync(CancellationToken ct)
        => await ReadAsync($"""
            SELECT {PublishedSkillColumns} FROM PublishedSkills
            WHERE EXISTS (SELECT 1 FROM BusinessPlugins WHERE BusinessPlugins.Id = PublishedSkills.PluginId AND Enabled = 1)
            ORDER BY Id
            """, ReadPublishedSkill, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PublishedSkillVersionEntity>> ListPublishedVersionsAsync(string[] skillIds, CancellationToken ct)
        => await ReadAsync($"SELECT {PublishedSkillVersionColumns} FROM PublishedSkillVersions WHERE SkillId IN (SELECT value FROM json_each($ids))",
            ReadPublishedSkillVersion, ct, ("$ids", WriteIds(skillIds))).ConfigureAwait(false);

    public async Task<IReadOnlyList<CatalogCapabilityEntity>> ListCapabilitiesAsync(string snapshotId, CancellationToken ct)
        => await ReadAsync($"SELECT {CatalogCapabilityColumns} FROM CatalogCapabilities WHERE SnapshotId = $id",
            ReadCatalogCapability, ct, ("$id", snapshotId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<BusinessCapabilityRisk>> ListCapabilityRisksAsync(
        string snapshotId, string[] capabilityIds, CancellationToken ct)
        => await ReadAsync("SELECT Id, Risk FROM CatalogCapabilities WHERE SnapshotId = $id AND Id IN (SELECT value FROM json_each($ids))",
            static reader => new BusinessCapabilityRisk(reader.GetString(0), reader.GetString(1)), ct,
            ("$id", snapshotId), ("$ids", WriteIds(capabilityIds))).ConfigureAwait(false);

    public Task<SkillRunEntity?> FindRunAsync(string id, CancellationToken ct)
        => FindAsync($"SELECT {SkillRunColumns} FROM SkillRuns WHERE RunId = $id", ReadSkillRun, ct, ("$id", id));

    public Task<SkillRunEntity?> FindIdempotentRunAsync(string skillId, string clientRequestId, CancellationToken ct)
        => FindAsync($"SELECT {SkillRunColumns} FROM SkillRuns WHERE SkillId = $skill AND ClientRequestId = $client",
            ReadSkillRun, ct, ("$skill", skillId), ("$client", clientRequestId));

    public async Task<IReadOnlyList<SkillRunEntity>> ListRecentRunsAsync(string skillId, CancellationToken ct)
        => await ReadAsync($"SELECT {SkillRunColumns} FROM SkillRuns WHERE SkillId = $id ORDER BY CreatedAtUtc DESC LIMIT 50",
            ReadSkillRun, ct, ("$id", skillId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<SkillRunEntity>> ListActiveRunsAsync(CancellationToken ct)
        => await ReadAsync($"SELECT {SkillRunColumns} FROM SkillRuns WHERE Status IN ($queued, $running, $waiting)", ReadSkillRun, ct,
            ("$queued", nameof(SkillRunStatus.Queued)), ("$running", nameof(SkillRunStatus.Running)),
            ("$waiting", nameof(SkillRunStatus.WaitingForApproval))).ConfigureAwait(false);

    public async Task<IReadOnlyList<SkillRunEntity>> ListWaitingRunsAsync(CancellationToken ct)
        => await ReadAsync($"SELECT {SkillRunColumns} FROM SkillRuns WHERE Status = $status AND PendingApprovalJson IS NOT NULL",
            ReadSkillRun, ct, ("$status", nameof(SkillRunStatus.WaitingForApproval))).ConfigureAwait(false);

    public async Task<SkillRunStepEntity[]> ListRunStepsAsync(string[] runIds, CancellationToken ct)
        => (await ReadAsync($"SELECT {SkillRunStepColumns} FROM SkillRunSteps WHERE RunId IN (SELECT value FROM json_each($ids)) ORDER BY Sequence",
            ReadSkillRunStep, ct, ("$ids", WriteIds(runIds))).ConfigureAwait(false)).ToArray();

    private static string WriteIds(string[] ids) => JsonSerializer.Serialize(ids, BusinessSkillJsonContext.Run.StringArray);

    private async Task<T?> FindAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken ct,
        params (string Name, object? Value)[] parameters) where T : class
        => (await ReadAsync(sql, read, ct, parameters).ConfigureAwait(false)).FirstOrDefault();

    private async Task<List<T>> ReadAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var command = Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static BusinessPluginEntity ReadBusinessPlugin(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        SourceType = reader.GetString(2),
        ConnectionId = reader.GetString(3),
        Enabled = reader.GetBoolean(4),
        ActiveSnapshotId = reader.IsDBNull(5) ? null : reader.GetString(5),
        ArchivedAtUtc = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
        LastDiscoveredAtUtc = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
        CreatedAtUtc = reader.GetDateTime(8),
        UpdatedAtUtc = reader.GetDateTime(9)
    };

    private static PluginConnectionEntity ReadPluginConnection(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        PluginId = reader.GetString(1),
        SourceUrl = reader.GetString(2),
        Environment = reader.GetString(3),
        EndpointPolicyJson = reader.GetString(4),
        DocumentCredentialRef = reader.IsDBNull(5) ? null : reader.GetString(5),
        ExecutionCredentialRef = reader.IsDBNull(6) ? null : reader.GetString(6),
        Revision = reader.GetInt32(7),
        CredentialRevision = reader.GetString(8),
        IsEncryptedTransport = reader.GetBoolean(9),
        CreatedAtUtc = reader.GetDateTime(10),
        UpdatedAtUtc = reader.GetDateTime(11)
    };

    private static CatalogSnapshotEntity ReadCatalogSnapshot(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        PluginId = reader.GetString(1),
        ConnectionRevision = reader.GetInt32(2),
        CredentialRevision = reader.GetString(3),
        Environment = reader.GetString(4),
        ProtocolVersion = reader.GetString(5),
        SourceHash = reader.GetString(6),
        SchemaHash = reader.GetString(7),
        Complete = reader.GetBoolean(8),
        TotalCapabilities = reader.GetInt32(9),
        ExecutableCount = reader.GetInt32(10),
        NeedsConfigurationCount = reader.GetInt32(11),
        UnsupportedCount = reader.GetInt32(12),
        DiagnosticsJson = reader.GetString(13),
        FetchedAtUtc = reader.GetDateTime(14)
    };

    private static SkillDraftEntity ReadSkillDraft(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        PluginId = reader.GetString(1),
        CatalogSnapshotId = reader.GetString(2),
        Revision = reader.GetInt32(3),
        Name = reader.GetString(4),
        BusinessGoal = reader.GetString(5),
        SelectedCapabilityIdsJson = reader.GetString(6),
        CandidateDefinitionJson = reader.IsDBNull(7) ? null : reader.GetString(7),
        ClarificationQuestionsJson = reader.GetString(8),
        ValidationErrorsJson = reader.GetString(9),
        NeedsRevalidation = reader.GetBoolean(10),
        CreatedAtUtc = reader.GetDateTime(11),
        UpdatedAtUtc = reader.GetDateTime(12)
    };

    private static BusinessCredentialMetaEntity ReadBusinessCredentialMeta(SqliteDataReader reader) => new()
    {
        CredentialRef = reader.GetString(0),
        AuthKind = reader.GetString(1),
        MaskedHint = reader.GetString(2),
        Revision = reader.GetString(3),
        CipherFileName = reader.GetString(4),
        CreatedAtUtc = reader.GetDateTime(5),
        UpdatedAtUtc = reader.GetDateTime(6)
    };

    private static PublishedSkillEntity ReadPublishedSkill(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        PluginId = reader.GetString(3),
        Environment = reader.GetString(4),
        ActiveVersion = reader.GetInt32(5),
        CreatedAtUtc = reader.GetDateTime(6),
        UpdatedAtUtc = reader.GetDateTime(7)
    };

    private static PublishedSkillVersionEntity ReadPublishedSkillVersion(SqliteDataReader reader) => new()
    {
        SkillId = reader.GetString(0),
        Version = reader.GetInt32(1),
        ContentHash = reader.GetString(2),
        CatalogSnapshotId = reader.GetString(3),
        DefinitionJson = reader.GetString(4),
        NeedsRevalidation = reader.GetBoolean(5),
        WriteStepVerified = reader.GetBoolean(6),
        PublishedAtUtc = reader.GetDateTime(7)
    };

    private static CatalogCapabilityEntity ReadCatalogCapability(SqliteDataReader reader) => new()
    {
        SnapshotId = reader.GetString(0),
        Id = reader.GetString(1),
        NativeIdentity = reader.GetString(2),
        ModelAlias = reader.GetString(3),
        Title = reader.GetString(4),
        Description = reader.GetString(5),
        SourceDescriptionMissing = reader.GetBoolean(6),
        GroupTag = reader.GetString(7),
        Risk = reader.GetString(8),
        SuggestedSourceRiskHint = reader.IsDBNull(9) ? null : reader.GetString(9),
        Executability = reader.GetString(10),
        DiagnosticsJson = reader.GetString(11),
        InputSchemaJson = reader.IsDBNull(12) ? null : reader.GetString(12),
        OutputSchemaJson = reader.IsDBNull(13) ? null : reader.GetString(13),
        InvocationJson = reader.GetString(14)
    };

    private static SkillRunEntity ReadSkillRun(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        SkillId = reader.GetString(1),
        SkillVersion = reader.GetInt32(2),
        ClientRequestId = reader.GetString(3),
        Status = reader.GetString(4),
        StopRequested = reader.GetBoolean(5),
        ExternalRequestCount = reader.GetInt32(6),
        ReadOnlyRetryCount = reader.GetInt32(7),
        InputsJson = reader.GetString(8),
        OutputPayloadJson = reader.IsDBNull(9) ? null : reader.GetString(9),
        PendingApprovalJson = reader.IsDBNull(10) ? null : reader.GetString(10),
        ErrorJson = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAtUtc = reader.GetDateTime(12),
        UpdatedAtUtc = reader.GetDateTime(13)
    };

    private static SkillRunStepEntity ReadSkillRunStep(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        Sequence = reader.GetInt32(1),
        StepId = reader.GetString(2),
        StepType = reader.GetString(3),
        State = reader.GetString(4),
        RequestSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
        ResponseSummary = reader.IsDBNull(6) ? null : reader.GetString(6),
        Evidence = reader.IsDBNull(7) ? null : reader.GetString(7),
        ErrorCode = reader.IsDBNull(8) ? null : reader.GetString(8),
        StartedAtUtc = reader.GetDateTime(9),
        CompletedAtUtc = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
    };
}
