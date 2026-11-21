// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Orchestration.BusinessSkills;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteBusinessSkillRetentionStore(SqliteConnectionFactory connections) : IBusinessSkillRetentionStore
{
    private static readonly string[] RunStatusParameters = StatusParameterNames(
        "runStatus", BusinessSkillRetentionPolicy.TerminalRunStatuses.Length);

    private static readonly string[] OperationStatusParameters = StatusParameterNames(
        "operationStatus", BusinessSkillRetentionPolicy.TerminalOperationStatuses.Length);

    private static readonly string ExpiredRunPredicate = $"""
        UpdatedAtUtc < $cutoff AND Status IN ({string.Join(", ", RunStatusParameters)})
        """;

    private static readonly string ExpiredOperationPredicate = $"""
        UpdatedAtUtc < $cutoff AND Status IN ({string.Join(", ", OperationStatusParameters)})
        """;

    private const string ProtectedSnapshots = """
        SELECT ActiveSnapshotId AS SnapshotId FROM BusinessPlugins WHERE ActiveSnapshotId IS NOT NULL
        UNION SELECT CatalogSnapshotId FROM SkillDrafts
        UNION SELECT CatalogSnapshotId FROM PublishedSkillVersions
        """;

    private const string OrphanSnapshotPredicate = $"""
        FetchedAtUtc < $cutoff AND Id NOT IN ({ProtectedSnapshots})
        """;

    public async Task<BusinessSkillRetentionCounts> PruneAsync(BusinessSkillRetentionCutoffs cutoffs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cutoffs);
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = "SELECT COUNT(*) FROM SkillRuns WHERE Status = $status";
        command.Parameters.AddWithValue("$status", BusinessSkillRetentionPolicy.OutcomeUnknownStatus);
        var retainedUnknownCount = await CountAsync(command, ct).ConfigureAwait(false);
        command.Parameters.Clear();

        command.Parameters.AddWithValue("$cutoff", cutoffs.RunHistoryUtc);
        BindStatuses(command, RunStatusParameters, BusinessSkillRetentionPolicy.TerminalRunStatuses);
        command.CommandText = $"""
            DELETE FROM SkillRunSteps WHERE RunId IN (SELECT RunId FROM SkillRuns WHERE {ExpiredRunPredicate})
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = $"DELETE FROM SkillRuns WHERE {ExpiredRunPredicate}";
        var deletedRuns = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Deleted runs are gone before trimming. Keep null, empty, and already redacted summaries unchanged.
        command.Parameters["$cutoff"].Value = cutoffs.DiagnosticsUtc;
        command.Parameters.AddWithValue("$marker", BusinessSkillRetentionService.RedactedDiagnosticsMarker);
        command.CommandText = $"""
            UPDATE SkillRunSteps
            SET RequestSummary = CASE WHEN RequestSummary <> '' THEN $marker ELSE RequestSummary END,
                ResponseSummary = CASE WHEN ResponseSummary <> '' THEN $marker ELSE ResponseSummary END
            WHERE RunId IN (SELECT RunId FROM SkillRuns WHERE {ExpiredRunPredicate})
                AND ((RequestSummary <> '' AND RequestSummary <> $marker)
                    OR (ResponseSummary <> '' AND ResponseSummary <> $marker))
            """;
        var trimmedSteps = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.Parameters.Clear();

        command.Parameters.AddWithValue("$cutoff", cutoffs.OperationHistoryUtc);
        BindStatuses(command, OperationStatusParameters, BusinessSkillRetentionPolicy.TerminalOperationStatuses);
        command.CommandText = $"DELETE FROM PluginOperations WHERE {ExpiredOperationPredicate}";
        var deletedOperations = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.Parameters.Clear();

        command.CommandText = $"SELECT COUNT(*) FROM ({ProtectedSnapshots})";
        var retainedProtectedCount = await CountAsync(command, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("$cutoff", cutoffs.RunHistoryUtc);
        command.CommandText = $"""
            DELETE FROM CatalogCapabilities
            WHERE SnapshotId IN (SELECT Id FROM CatalogSnapshots WHERE {OrphanSnapshotPredicate})
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = $"DELETE FROM CatalogSnapshots WHERE {OrphanSnapshotPredicate}";
        var deletedSnapshots = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new BusinessSkillRetentionCounts(
            trimmedSteps, deletedRuns, deletedSnapshots, deletedOperations, retainedUnknownCount, retainedProtectedCount);
    }

    private static async Task<int> CountAsync(SqliteCommand command, CancellationToken ct)
        => Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

    private static string[] StatusParameterNames(string prefix, int count)
        => Enumerable.Range(0, count)
            .Select(index => "$" + prefix + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();

    private static void BindStatuses(SqliteCommand command, string[] parameterNames, string[] statuses)
    {
        for (var index = 0; index < statuses.Length; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], statuses[index]);
        }
    }
}
