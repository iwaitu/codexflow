// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Orchestration.Chat;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteSessionStateStore(SqliteConnectionFactory connections)
    : ISessionPolicyStore, ISessionCompressionPersistence, ILateToolOutputStore
{
    public async Task<StoredSessionPolicy?> FindPolicyAsync(string sessionId, string? userId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT UserId, WorkMode, ModeVersion, PlanModeActive, PlanModeVersion, PlanModeChangedAtUtc, PlanModeChangedBy
            FROM Sessions
            WHERE Id = $sessionId AND ($userId IS NULL OR UserId = $userId);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredSessionPolicy(reader.GetString(0), new SessionPolicySnapshot(
            (SessionWorkMode)reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetString(6)));
    }

    public async Task<bool> TrySetPlanModeAsync(
        string sessionId,
        StoredSessionPolicy expected,
        bool active,
        int version,
        DateTime changedAtUtc,
        string changedBy,
        CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sessions
            SET PlanModeActive = $active, PlanModeVersion = $version,
                PlanModeChangedAtUtc = $changedAt, PlanModeChangedBy = $changedBy
            WHERE Id = $sessionId AND UserId = $userId
              AND PlanModeActive = $oldActive AND PlanModeVersion = $oldPlanVersion
              AND WorkMode = $oldWorkMode AND ModeVersion = $oldModeVersion;
            """;
        var state = expected.Policy;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$userId", expected.UserId);
        command.Parameters.AddWithValue("$active", active);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$changedAt", changedAtUtc);
        command.Parameters.AddWithValue("$changedBy", changedBy);
        command.Parameters.AddWithValue("$oldActive", state.PlanModeActive);
        command.Parameters.AddWithValue("$oldPlanVersion", state.PlanModeVersion);
        command.Parameters.AddWithValue("$oldWorkMode", (int)state.WorkMode);
        command.Parameters.AddWithValue("$oldModeVersion", state.ModeVersion);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
    }

    public async Task<SessionCompressionState?> ReadCompressionAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT HistorySummary, LastCompressedAt
            FROM Sessions WHERE Id = $sessionId
              AND HistorySummary <> '' AND LastCompressedAt IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new SessionCompressionState(reader.GetString(0), reader.GetDateTime(1))
            : null;
    }

    public async Task WriteCompressionAsync(
        string sessionId, string? summary, DateTime? compressedAtUtc, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sessions
            SET HistorySummary = $historySummary, LastCompressedAt = $compressedAt
            WHERE Id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$historySummary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$compressedAt", (object?)compressedAtUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendLateToolOutputAsync(
        string sessionId,
        string? preferredConversationId,
        Func<string?, string> mergeEnvelope,
        CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var conversationId = preferredConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            command.CommandText = """
                SELECT Id FROM Conversations
                WHERE SessionId = $sessionId AND IsArchived = 0
                ORDER BY UpdateAt DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            conversationId = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            command.Parameters.Clear();
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        command.CommandText = """
            SELECT Id, ToolCallResult FROM ChatMessages
            WHERE ConversationId = $conversationId AND (MessageType IS NULL OR MessageType = '')
            ORDER BY Created DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        string? messageId = null;
        string? previousEnvelope = null;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                messageId = reader.GetString(0);
                previousEnvelope = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$envelope", mergeEnvelope(previousEnvelope));
        if (messageId is null)
        {
            command.CommandText = """
                INSERT INTO ChatMessages (Id, ConversationId, UserContent, AiContent, SystemPrompt, ReplyId,
                    Created, TokenUsed, ToolCallResult)
                VALUES ($messageId, $conversationId, '', '', '', '', $createdAt, 0, $envelope);
                """;
            command.Parameters.AddWithValue("$messageId", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$conversationId", conversationId);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow);
        }
        else
        {
            command.CommandText = "UPDATE ChatMessages SET ToolCallResult = $envelope WHERE Id = $messageId;";
            command.Parameters.AddWithValue("$messageId", messageId);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
