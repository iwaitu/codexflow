// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed partial class SqliteBusinessSkillExecutionStore : IBusinessSkillBuilderStore
{
    private const string OperationColumns = """
        OperationId, Kind, TargetId, ClientRequestId, Status, Sequence, Version, SnapshotId,
        ResultMessage, ErrorJson, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string GenerationQuery = $"""
        SELECT {OperationColumns} FROM PluginOperations
        WHERE Kind = 'generate' AND TargetId = $id AND ClientRequestId = $client
        """;

    public Task CreateDraftAsync(SkillDraftEntity draft, CancellationToken ct)
        => ExecuteAsync($"""
            INSERT INTO SkillDrafts ({SkillDraftColumns})
            VALUES ($id, $plugin, $catalog, $revision, $name, $goal, $selection, $definition,
                $questions, $errors, $revalidate, $created, $updated)
            """, ct, [("$id", draft.Id), ("$plugin", draft.PluginId), ("$created", draft.CreatedAtUtc), ..DraftValues(draft)]);

    public async Task<bool> TryUpdateDraftAsync(SkillDraftRevision expected, SkillDraftEntity draft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        return await UpdateDraftAsync(connection, null, expected, draft, ct).ConfigureAwait(false);
    }

    public Task<PluginOperationEntity?> FindGenerationAsync(string draftId, string clientRequestId, CancellationToken ct)
        => FindAsync(GenerationQuery, ReadOperation, ct, ("$id", draftId), ("$client", clientRequestId));

    public async Task<PluginOperationEntity> ReserveGenerationAsync(PluginOperationEntity proposed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var insert = Command(connection, transaction, $"""
            INSERT INTO PluginOperations ({OperationColumns})
            VALUES ($id, $kind, $target, $client, $status, $sequence, $version, $snapshot, $message, $error, $created, $updated)
            ON CONFLICT (Kind, TargetId, ClientRequestId) DO NOTHING
            """, ("$id", proposed.OperationId), ("$kind", proposed.Kind), ("$target", proposed.TargetId),
            ("$client", proposed.ClientRequestId), ("$status", proposed.Status), ("$sequence", proposed.Sequence),
            ("$version", proposed.Version), ("$snapshot", proposed.SnapshotId), ("$message", proposed.ResultMessage),
            ("$error", proposed.ErrorJson), ("$created", proposed.CreatedAtUtc), ("$updated", proposed.UpdatedAtUtc));
        var winner = proposed;
        if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            await using var select = Command(connection, transaction, GenerationQuery,
                ("$id", proposed.TargetId), ("$client", proposed.ClientRequestId));
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            winner = await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadOperation(reader)
                : throw new InvalidOperationException("Idempotent generation operation could not be reloaded.");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return winner;
    }

    public async Task<bool> CompleteGenerationAsync(
        SkillDraftRevision expected, SkillDraftEntity draft, PluginOperationEntity operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await UpdateDraftAsync(connection, transaction, expected, draft, ct).ConfigureAwait(false))
        {
            return false;
        }

        await using var update = Command(connection, transaction, UpdateGenerationSql, GenerationUpdateValues(operation));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public Task FailGenerationAsync(PluginOperationEntity operation, CancellationToken ct)
        => ExecuteAsync(UpdateGenerationSql, ct, GenerationUpdateValues(operation));

    public async Task<bool> PublishAsync(BusinessSkillPublication publication, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var draft = publication.ExpectedDraft;
        var binding = publication.Plugin;
        var skill = publication.Skill;
        var version = publication.Version;
        await using var validate = Command(connection, transaction, $"""
            SELECT
              EXISTS (SELECT 1 FROM SkillDrafts
                WHERE Id = $draft AND Revision = $revision AND UpdatedAtUtc = $updated AND NeedsRevalidation = 0
                  AND CatalogSnapshotId = $catalog AND PluginId = $plugin)
              AND ({BindingQuery})
              AND EXISTS (SELECT 1 FROM CatalogSnapshots WHERE Id = $catalog AND PluginId = $plugin AND SchemaHash = $hash)
              AND EXISTS (SELECT 1 FROM SkillRuns WHERE RunId = $testRun AND Status = $success)
              AND $version = (SELECT COALESCE(MAX(Version), 0) + 1 FROM PublishedSkillVersions WHERE SkillId = $skill)
            """, ("$draft", draft.Id), ("$revision", draft.Revision), ("$updated", draft.UpdatedAtUtc),
            ("$catalog", version.CatalogSnapshotId), ("$hash", publication.CatalogSchemaHash), ("$testRun", publication.SucceededTestRunId),
            ("$success", nameof(SkillRunStatus.Succeeded)), ("$version", version.Version), ("$skill", skill.Id));
        BindPlugin(validate, binding);
        if (await validate.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long allowed || allowed != 1)
        {
            return false;
        }

        await using var updateSkill = Command(connection, transaction, $"""
            INSERT INTO PublishedSkills ({PublishedSkillColumns})
            VALUES ($id, $name, $description, $plugin, $environment, $version, $created, $updated)
            ON CONFLICT (Id) DO UPDATE SET
                Name = excluded.Name, Description = excluded.Description, Environment = excluded.Environment,
                ActiveVersion = excluded.ActiveVersion, UpdatedAtUtc = excluded.UpdatedAtUtc
            WHERE PublishedSkills.PluginId = excluded.PluginId
            """, ("$id", skill.Id), ("$name", skill.Name), ("$description", skill.Description), ("$plugin", binding.PluginId),
            ("$environment", skill.Environment), ("$version", skill.ActiveVersion), ("$created", skill.CreatedAtUtc),
            ("$updated", skill.UpdatedAtUtc));
        if (await updateSkill.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await using var insertVersion = Command(connection, transaction, $"""
            INSERT INTO PublishedSkillVersions ({PublishedSkillVersionColumns})
            VALUES ($id, $version, $hash, $catalog, $definition, $revalidate, $writes, $published)
            """, ("$id", version.SkillId), ("$version", version.Version), ("$hash", version.ContentHash),
            ("$catalog", version.CatalogSnapshotId), ("$definition", version.DefinitionJson),
            ("$revalidate", version.NeedsRevalidation), ("$writes", version.WriteStepVerified), ("$published", version.PublishedAtUtc));
        await insertVersion.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ActivateVersionAsync(
        string skillId, int version, BusinessSkillPluginBinding binding, DateTime now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _connections.Open();
        await using var command = Command(connection, null, $"""
            UPDATE PublishedSkills SET ActiveVersion = $version, UpdatedAtUtc = $now
            WHERE Id = $id AND PluginId = $plugin AND ({BindingQuery})
                AND EXISTS (SELECT 1 FROM PublishedSkillVersions
                  WHERE SkillId = $id AND Version = $version AND NeedsRevalidation = 0)
            """, ("$version", version), ("$now", now), ("$id", skillId));
        BindPlugin(command, binding);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
    }

    public async Task<IReadOnlyList<PublishedSkillEntity>> ListPublishedSkillsAsync(CancellationToken ct)
        => await ReadAsync($"SELECT {PublishedSkillColumns} FROM PublishedSkills ORDER BY CreatedAtUtc",
            ReadPublishedSkill, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<CatalogCapabilityEntity>> ListSelectedCapabilitiesAsync(
        string snapshotId, string[] capabilityIds, CancellationToken ct)
        => await ReadAsync($"SELECT {CatalogCapabilityColumns} FROM CatalogCapabilities WHERE SnapshotId = $id AND Id IN (SELECT value FROM json_each($ids))",
            ReadCatalogCapability, ct, ("$id", snapshotId), ("$ids", WriteIds(capabilityIds))).ConfigureAwait(false);

    private const string BindingQuery = """
        EXISTS (SELECT 1 FROM BusinessPlugins AS p
          JOIN PluginConnections AS c ON c.Id = p.ConnectionId
          WHERE p.Id = $plugin AND p.Enabled = 1 AND c.Id = $connection AND c.Revision = $connectionRevision
            AND c.CredentialRevision = $credentialRevision)
        """;

    private static void BindPlugin(SqliteCommand command, BusinessSkillPluginBinding binding)
    {
        command.Parameters.AddWithValue("$plugin", binding.PluginId);
        command.Parameters.AddWithValue("$connection", binding.ConnectionId);
        command.Parameters.AddWithValue("$connectionRevision", binding.ConnectionRevision);
        command.Parameters.AddWithValue("$credentialRevision", binding.CredentialRevision);
    }

    private static async Task<bool> UpdateDraftAsync(SqliteConnection connection, SqliteTransaction? transaction,
        SkillDraftRevision expected, SkillDraftEntity draft, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            UPDATE SkillDrafts SET CatalogSnapshotId = $catalog, Revision = $revision, Name = $name,
                BusinessGoal = $goal, SelectedCapabilityIdsJson = $selection, CandidateDefinitionJson = $definition,
                ClarificationQuestionsJson = $questions, ValidationErrorsJson = $errors, NeedsRevalidation = $revalidate,
                UpdatedAtUtc = $updated
            WHERE Id = $expectedId AND Revision = $expectedRevision AND UpdatedAtUtc = $expectedUpdated
            """, DraftValues(draft));
        command.Parameters.AddWithValue("$expectedId", expected.Id);
        command.Parameters.AddWithValue("$expectedRevision", expected.Revision);
        command.Parameters.AddWithValue("$expectedUpdated", expected.UpdatedAtUtc);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
    }

    private static (string Name, object? Value)[] DraftValues(SkillDraftEntity draft)
        => [("$catalog", draft.CatalogSnapshotId), ("$revision", draft.Revision),
            ("$name", draft.Name), ("$goal", draft.BusinessGoal), ("$selection", draft.SelectedCapabilityIdsJson),
            ("$definition", draft.CandidateDefinitionJson), ("$questions", draft.ClarificationQuestionsJson),
            ("$errors", draft.ValidationErrorsJson), ("$revalidate", draft.NeedsRevalidation),
            ("$updated", draft.UpdatedAtUtc)];

    private const string UpdateGenerationSql = """
        UPDATE PluginOperations SET Status = $status, Sequence = $sequence, ResultMessage = $message,
            ErrorJson = $error, UpdatedAtUtc = $updated
        WHERE OperationId = $id AND Kind = 'generate' AND Status = $running AND Sequence = 1
        """;

    private static (string Name, object? Value)[] GenerationUpdateValues(PluginOperationEntity operation)
        => [("$id", operation.OperationId), ("$status", operation.Status), ("$sequence", operation.Sequence),
            ("$message", operation.ResultMessage), ("$error", operation.ErrorJson), ("$updated", operation.UpdatedAtUtc),
            ("$running", nameof(PluginOperationStatus.Running))];

    private static PluginOperationEntity ReadOperation(SqliteDataReader reader) => new()
    {
        OperationId = reader.GetString(0),
        Kind = reader.GetString(1),
        TargetId = reader.GetString(2),
        ClientRequestId = reader.GetString(3),
        Status = reader.GetString(4),
        Sequence = reader.GetInt64(5),
        Version = reader.GetInt32(6),
        SnapshotId = reader.IsDBNull(7) ? null : reader.GetString(7),
        ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
        ErrorJson = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAtUtc = reader.GetDateTime(10),
        UpdatedAtUtc = reader.GetDateTime(11)
    };
}
