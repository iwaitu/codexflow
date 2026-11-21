// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteSessionDirectory(SqliteConnectionFactory connections) : ISessionDirectory
{
    public async Task<SessionRecord?> FindAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, UserId, WorkspacePath FROM Sessions WHERE Id = $sessionId
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}

public sealed class SqliteConversationDirectory(SqliteConnectionFactory connections) : IConversationDirectory
{
    private const string FindLiveIdSql = """
        SELECT Id FROM Conversations
        WHERE Id = $value AND SessionId IS $sessionId AND UserId = $userId AND IsArchived = 0
        """;

    private const string FindLatestLiveIdSql = """
        SELECT Id FROM Conversations
        WHERE SessionId IS $sessionId AND UserId = $userId AND ThreadType = $value AND IsArchived = 0
        ORDER BY UpdateAt DESC LIMIT 1
        """;

    public async Task<IvilsonConversation?> FindAsync(string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SessionId, TaskId, ThreadType, IsArchived, Model, Name, UserId, Created, UpdateAt, IsIndexed
            FROM Conversations WHERE Id = $conversationId
            """;
        command.Parameters.AddWithValue("$conversationId", (object?)conversationId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new IvilsonConversation
        {
            Id = reader.GetString(0),
            SessionId = ReadNullableText(reader, 1),
            TaskId = ReadNullableText(reader, 2),
            ThreadType = (ConversationThreadType)reader.GetInt32(3),
            IsArchived = reader.GetBoolean(4),
            Model = reader.GetString(5),
            Name = reader.GetString(6),
            UserId = reader.GetString(7),
            Created = reader.GetDateTime(8),
            UpdateAt = reader.GetDateTime(9),
            IsIndexed = reader.GetBoolean(10)
        };
    }

    public Task<string?> FindLiveIdAsync(string conversationId, string sessionId, string userId, CancellationToken ct)
        => FindIdAsync(FindLiveIdSql, sessionId, userId, conversationId, ct);

    public Task<string?> FindLatestLiveIdAsync(string sessionId, string userId, ConversationThreadType type, CancellationToken ct)
        => FindIdAsync(FindLatestLiveIdSql, sessionId, userId, (int)type, ct);

    private async Task<string?> FindIdAsync(string sql, string sessionId, string userId, object? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", value ?? DBNull.Value);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
