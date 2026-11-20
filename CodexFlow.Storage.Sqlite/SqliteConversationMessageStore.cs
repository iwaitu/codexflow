// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Data;
using CodexFlow.Application;
using CodexFlow.Domain.Chat;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Stores conversation messages through SQLite commands on a caller-owned connection.</summary>
internal sealed class SqliteConversationMessageStore(SqliteConnection connection) : IConversationMessageStore
{
    private const string MessageColumns = """
        Id, ConversationId, UserContent, AiContent, ThinkingContent, TokenUsed,
        SystemPrompt, ReplyId, Created, ToolCallResult, MessageType, NotificationMeta
        """;

    public async Task<List<IvilsonChatMessage>> ReadAfterAsync(string conversationId, DateTime? createdAfterUtc, int? hardLimit)
    {
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$conversationId", conversationId);
            var query = $"SELECT {MessageColumns} FROM ChatMessages WHERE ConversationId = $conversationId";
            if (createdAfterUtc.HasValue)
            {
                query += " AND Created > $createdAfterUtc";
                command.Parameters.AddWithValue("$createdAfterUtc", createdAfterUtc.Value);
                if (hardLimit.HasValue)
                {
                    // Match the EF query: cap from the newest end, then return in chronological order.
                    query = $"SELECT {MessageColumns} FROM ({query} ORDER BY Created DESC LIMIT $limit)";
                    command.Parameters.AddWithValue("$limit", hardLimit.Value);
                }
            }

            command.CommandText = query + " ORDER BY Created";
            await using var reader = await command.ExecuteReaderAsync();
            var messages = new List<IvilsonChatMessage>();
            while (await reader.ReadAsync())
            {
                messages.Add(new IvilsonChatMessage
                {
                    Id = reader.GetString(0),
                    ConversationId = reader.GetString(1),
                    UserContent = ReadNullableText(reader, 2),
                    AiContent = ReadNullableText(reader, 3),
                    ThinkingContent = ReadNullableText(reader, 4),
                    TokenUsed = reader.GetInt32(5),
                    SystemPrompt = ReadNullableText(reader, 6),
                    ReplyId = ReadNullableText(reader, 7),
                    Created = reader.GetDateTime(8),
                    ToolCallResult = ReadNullableText(reader, 9),
                    MessageType = ReadNullableText(reader, 10),
                    NotificationMeta = ReadNullableText(reader, 11),
                });
            }

            return messages;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    public async Task AppendAsync(string userId, string? model, IvilsonChatMessage message)
    {
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync();

        try
        {
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Conversations (Id, UserId, Name, Model, Created, UpdateAt, ThreadType, IsArchived, IsIndexed)
                VALUES ($id, $userId, $name, $model, $created, $created, 0, 0, 0)
                ON CONFLICT(Id) DO UPDATE SET UpdateAt = excluded.UpdateAt
                """;
            var title = message.UserContent ?? string.Empty;
            command.Parameters.AddWithValue("$id", message.ConversationId);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$name", title.Length > 20 ? title[..20] : title);
            command.Parameters.AddWithValue("$model", model ?? "gpt-3.5-turbo");
            command.Parameters.AddWithValue("$created", message.Created);
            await command.ExecuteNonQueryAsync();

            // Appended messages use the explicit ConversationId, as does the existing EF writer.
            command.CommandText = $"""
                INSERT INTO ChatMessages ({MessageColumns})
                VALUES ($id, $conversationId, $userContent, $aiContent, $thinkingContent, $tokenUsed,
                        $systemPrompt, $replyId, $created, $toolCallResult, $messageType, $notificationMeta)
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$conversationId", message.ConversationId);
            AddNullableText(command, "$userContent", message.UserContent);
            AddNullableText(command, "$aiContent", message.AiContent);
            AddNullableText(command, "$thinkingContent", message.ThinkingContent);
            command.Parameters.AddWithValue("$tokenUsed", message.TokenUsed);
            AddNullableText(command, "$systemPrompt", message.SystemPrompt);
            AddNullableText(command, "$replyId", message.ReplyId);
            command.Parameters.AddWithValue("$created", message.Created);
            AddNullableText(command, "$toolCallResult", message.ToolCallResult);
            AddNullableText(command, "$messageType", message.MessageType);
            AddNullableText(command, "$notificationMeta", message.NotificationMeta);
            await command.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNullableText(SqliteCommand command, string name, string? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
