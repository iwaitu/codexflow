// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Planning;
using CodexFlow.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqlitePlanArtifactRepository(SqliteConnectionFactory connections) : IPlanArtifactRepository
{
    private const string Columns = """
        Id, SessionId, Version, Status, Source, Goal, Markdown, PreviewProjectedMarkdownHash, PlanFilePath,
        TasksJson, ReviewsJson, ApprovalJson, ProjectionJson, ParentPlanId, SupersededByPlanId, IsCurrent,
        CreatedAtUtc, UpdatedAtUtc
        """;

    private const string GetByIdSql = $"SELECT {Columns} FROM PlanArtifacts WHERE Id = $value";
    private const string GetCurrentSql = $"""
        SELECT {Columns} FROM PlanArtifacts WHERE SessionId = $value AND IsCurrent = 1
        ORDER BY UpdatedAtUtc DESC LIMIT 1
        """;

    private const string ListBySessionSql = $"""
        SELECT {Columns} FROM PlanArtifacts WHERE SessionId = $value ORDER BY UpdatedAtUtc DESC
        """;

    public async Task<PlanArtifactEntity?> GetAsync(string planArtifactId, CancellationToken ct = default)
        => (await ReadAsync(GetByIdSql, planArtifactId, ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<PlanArtifactEntity?> GetCurrentAsync(string sessionId, CancellationToken ct = default)
        => (await ReadAsync(GetCurrentSql, sessionId, ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<IReadOnlyList<PlanArtifactEntity>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
        => await ReadAsync(ListBySessionSql, sessionId, ct).ConfigureAwait(false);

    public async Task SaveAsync(PlanArtifactEntity artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();

        // A save preserves the existing selection. Only SetCurrentAsync can change it.
        command.CommandText = $"""
            INSERT INTO PlanArtifacts ({Columns})
            VALUES ($id, $sessionId, $version, $status, $source, $goal, $markdown, $previewHash, $planPath,
                    $tasks, $reviews, $approval, $projection, $parent, $supersededBy, 0, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                SessionId = excluded.SessionId,
                Version = excluded.Version,
                Status = excluded.Status,
                Source = excluded.Source,
                Goal = excluded.Goal,
                Markdown = excluded.Markdown,
                PreviewProjectedMarkdownHash = excluded.PreviewProjectedMarkdownHash,
                PlanFilePath = excluded.PlanFilePath,
                TasksJson = excluded.TasksJson,
                ReviewsJson = excluded.ReviewsJson,
                ApprovalJson = excluded.ApprovalJson,
                ProjectionJson = excluded.ProjectionJson,
                ParentPlanId = excluded.ParentPlanId,
                SupersededByPlanId = excluded.SupersededByPlanId,
                CreatedAtUtc = excluded.CreatedAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc
            """;
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$sessionId", artifact.SessionId);
        command.Parameters.AddWithValue("$version", artifact.Version);
        command.Parameters.AddWithValue("$status", artifact.Status);
        command.Parameters.AddWithValue("$source", artifact.Source);
        command.Parameters.AddWithValue("$goal", artifact.Goal);
        command.Parameters.AddWithValue("$markdown", artifact.Markdown);
        AddNullableText(command, "$previewHash", artifact.PreviewProjectedMarkdownHash);
        AddNullableText(command, "$planPath", artifact.PlanFilePath);
        command.Parameters.AddWithValue("$tasks", artifact.TasksJson);
        command.Parameters.AddWithValue("$reviews", artifact.ReviewsJson);
        AddNullableText(command, "$approval", artifact.ApprovalJson);
        AddNullableText(command, "$projection", artifact.ProjectionJson);
        AddNullableText(command, "$parent", artifact.ParentPlanId);
        AddNullableText(command, "$supersededBy", artifact.SupersededByPlanId);
        command.Parameters.AddWithValue("$createdAt", artifact.CreatedAtUtc);
        command.Parameters.AddWithValue("$updatedAt", artifact.UpdatedAtUtc);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // One statement switches both selections atomically, even with another writer.
        command.CommandText = """
            UPDATE PlanArtifacts
            SET IsCurrent = CASE WHEN Id = $planId THEN 1 ELSE 0 END
            WHERE SessionId = $sessionId
                AND IsCurrent <> CASE WHEN Id = $planId THEN 1 ELSE 0 END
            """;
        AddNullableText(command, "$planId", planArtifactId);
        AddNullableText(command, "$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<PlanArtifactEntity>> ReadAsync(string sql, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddNullableText(command, "$value", value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<PlanArtifactEntity>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new PlanArtifactEntity
            {
                Id = reader.GetString(0),
                SessionId = reader.GetString(1),
                Version = reader.GetString(2),
                Status = reader.GetString(3),
                Source = reader.GetString(4),
                Goal = reader.GetString(5),
                Markdown = reader.GetString(6),
                PreviewProjectedMarkdownHash = ReadNullableText(reader, 7),
                PlanFilePath = ReadNullableText(reader, 8),
                TasksJson = reader.GetString(9),
                ReviewsJson = reader.GetString(10),
                ApprovalJson = ReadNullableText(reader, 11),
                ProjectionJson = ReadNullableText(reader, 12),
                ParentPlanId = ReadNullableText(reader, 13),
                SupersededByPlanId = ReadNullableText(reader, 14),
                IsCurrent = reader.GetBoolean(15),
                CreatedAtUtc = reader.GetDateTime(16),
                UpdatedAtUtc = reader.GetDateTime(17)
            });
        }

        return rows;
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNullableText(SqliteCommand command, string name, string? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
