// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Contracts;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteChatPersistence(SqliteConnectionFactory connections) : IChatPersistence
{
    private const string SessionColumns = """
        Id, UserId, Name, WorkspacePath, ProjectUrl, CurrentStage, FactsJson, GlobalPlanJson, CurrentPlanArtifactId, ActiveTaskId,
        Created, HistorySummary, LastCompressedAt, IsArchived, IsExternalWorkspace, WorkMode, ModeVersion, PlanModeActive,
        PlanModeVersion, PlanModeChangedAtUtc, PlanModeChangedBy
        """;
    private const string ConversationColumns = """
        Id, SessionId, TaskId, ThreadType, IsArchived, Model, Name, UserId, Created, UpdateAt, IsIndexed
        """;
    private const string ConversationFilter = """
        WHERE ($user IS NULL OR UserId = $user)
          AND ($session IS NULL OR SessionId = $session)
          AND ($includeArchived OR IsArchived = 0)
          AND ($keyword = '' OR instr(Name, $keyword) > 0)
        """;
    private const string SessionPageFilter = """
        WHERE UserId = $user AND ($includeArchived OR IsArchived = 0)
        """;

    public async Task AddConversationAsync(IvilsonConversation conversation)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            INSERT INTO Conversations ({ConversationColumns})
            SELECT $id, $session, $task, $thread, $archived, $model, $name, $user, $created, $updated, $indexed
            WHERE $session IS NULL OR EXISTS (SELECT 1 FROM Sessions WHERE Id = $session);
            """,
            ("$id", conversation.Id), ("$session", conversation.SessionId), ("$task", conversation.TaskId),
            ("$thread", (int)conversation.ThreadType), ("$archived", conversation.IsArchived),
            ("$model", conversation.Model), ("$name", conversation.Name), ("$user", conversation.UserId),
            ("$created", conversation.Created), ("$updated", conversation.UpdateAt), ("$indexed", conversation.IsIndexed));
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            throw new ArgumentException("Session not found");
        }
    }

    public async Task<IvilsonConversation?> FindConversationAsync(string conversationId)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection,
            $"SELECT {ConversationColumns} FROM Conversations WHERE Id = $id;", ("$id", conversationId));
        return await ReadOneAsync(command, ReadConversation);
    }

    public async Task<IvilsonConversation?> FindConversationWithHistoryAsync(string conversationId)
    {
        var conversation = await FindConversationAsync(conversationId).ConfigureAwait(false);
        if (conversation is not null)
        {
            var messages = new SqliteConversationMessageStore(connections);
            conversation.ChatMessages = await messages.ReadAfterAsync(conversationId, null, null).ConfigureAwait(false);
        }
        return conversation;
    }

    public Task<IvilsonConversation?> SetConversationNameAsync(string conversationId, string name, DateTime updatedAt)
        => EditConversationAsync(conversationId, null, false, "Name = $value, UpdateAt = $updated", name, updatedAt);

    public Task<IvilsonConversation?> RenameConversationAsync(string conversationId, string userId, string name, DateTime updatedAt)
        => EditConversationAsync(conversationId, userId, true, "Name = $value, UpdateAt = $updated", name, updatedAt);

    public Task<IvilsonConversation?> ArchiveConversationAsync(string conversationId, string userId, bool isArchived, DateTime updatedAt)
        => EditConversationAsync(conversationId, userId, true, "IsArchived = $value, UpdateAt = $updated", isArchived, updatedAt);

    public async Task SetConversationIndexedAsync(string conversationId)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, "UPDATE Conversations SET IsIndexed = 1 WHERE Id = $id;", ("$id", conversationId));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string?> FindLatestConversationIdAsync(string userId)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, """
            SELECT Id FROM Conversations WHERE UserId = $user ORDER BY Created DESC LIMIT 1;
            """, ("$user", userId));
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task<PageOf<IvilsonConversation>> ListConversationsAsync(
        string? userId, string? sessionId, string? keyword, int page, int pageSize, bool includeArchived)
    {
        await using var connection = connections.Open();
        (string, object?)[] filters =
        [
            ("$user", userId), ("$session", sessionId),
            ("$keyword", keyword ?? string.Empty), ("$includeArchived", includeArchived),
        ];
        await using var count = Command(connection, $"SELECT count(*) FROM Conversations {ConversationFilter};", filters);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        await using var list = Command(connection, $"""
            SELECT {ConversationColumns} FROM Conversations {ConversationFilter}
            ORDER BY UpdateAt DESC LIMIT $limit OFFSET $offset;
            """, filters);
        BindPage(list, page, pageSize);
        return new PageOf<IvilsonConversation>
        {
            List = await ReadManyAsync(list, ReadConversation),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task DeleteConversationAsync(string conversationId, string userId)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var messages = Command(connection, """
            DELETE FROM ChatMessages
            WHERE (ConversationId = $id OR IvilsonConversationId = $id)
              AND EXISTS (SELECT 1 FROM Conversations WHERE Id = $id AND UserId = $user);
            """, ("$id", conversationId), ("$user", userId));
        messages.Transaction = transaction;
        await messages.ExecuteNonQueryAsync();

        await using var conversation = Command(connection,
            "DELETE FROM Conversations WHERE Id = $id AND UserId = $user;", ("$id", conversationId), ("$user", userId));
        conversation.Transaction = transaction;
        await conversation.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task AddNotificationAsync(IvilsonChatMessage message, DateTime updatedAt)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var touch = Command(connection,
            "UPDATE Conversations SET UpdateAt = $updated WHERE Id = $id;",
            ("$updated", updatedAt), ("$id", message.ConversationId));
        touch.Transaction = transaction;
        await touch.ExecuteNonQueryAsync();

        await using var save = Command(connection, """
            INSERT INTO ChatMessages
                (Id, ConversationId, UserContent, AiContent, Created, TokenUsed, MessageType, NotificationMeta)
            VALUES ($id, $conversation, $userContent, $aiContent, $created, $tokens, $type, $meta);
            """, ("$id", message.Id), ("$conversation", message.ConversationId),
            ("$userContent", message.UserContent), ("$aiContent", message.AiContent), ("$created", message.Created),
            ("$tokens", message.TokenUsed), ("$type", message.MessageType), ("$meta", message.NotificationMeta));
        save.Transaction = transaction;
        await save.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<string?> FindReplyContentAsync(string replyId)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, "SELECT AiContent FROM ChatMessages WHERE ReplyId = $reply LIMIT 1;", ("$reply", replyId));
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task AddSessionAsync(IvilsonSession session)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            INSERT INTO Sessions ({SessionColumns})
            VALUES ($id, $user, $name, $workspace, $projectUrl, $stage, $facts, $plan, $artifact, $activeTask,
                $created, $summary, $compressedAt, $archived, $external, $workMode, $modeVersion, $planModeActive,
                $planModeVersion, $planModeChangedAt, $planModeChangedBy);
            """, ("$id", session.Id), ("$user", session.UserId), ("$name", session.Name),
            ("$workspace", session.WorkspacePath), ("$projectUrl", session.ProjectUrl), ("$stage", session.CurrentStage),
            ("$facts", session.FactsJson), ("$plan", session.GlobalPlanJson), ("$artifact", session.CurrentPlanArtifactId),
            ("$activeTask", session.ActiveTaskId), ("$created", session.Created), ("$summary", session.HistorySummary),
            ("$compressedAt", session.LastCompressedAt), ("$archived", session.IsArchived), ("$external", session.IsExternalWorkspace),
            ("$workMode", (int)session.WorkMode), ("$modeVersion", session.ModeVersion), ("$planModeActive", session.PlanModeActive),
            ("$planModeVersion", session.PlanModeVersion), ("$planModeChangedAt", session.PlanModeChangedAtUtc),
            ("$planModeChangedBy", session.PlanModeChangedBy));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IvilsonSession?> FindSessionAsync(string sessionId, bool includeConversations = false)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"SELECT {SessionColumns} FROM Sessions WHERE Id = $id;", ("$id", sessionId));
        var session = await ReadOneAsync(command, ReadSession);
        if (includeConversations && session is not null)
        {
            await using var children = Command(connection,
                $"SELECT {ConversationColumns} FROM Conversations WHERE SessionId = $id;", ("$id", sessionId));
            foreach (var conversation in await ReadManyAsync(children, ReadConversation))
            {
                AddConversationToSession(session, conversation);
            }
        }

        return session;
    }

    public async Task<List<IvilsonSession>> ListActiveSessionsAsync(string userId, bool includeHistory = false)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            SELECT {SessionColumns} FROM Sessions WHERE UserId = $user AND IsArchived = 0 ORDER BY Created DESC;
            """, ("$user", userId));
        var sessions = await ReadManyAsync(command, ReadSession);
        if (includeHistory && sessions.Count != 0)
        {
            await PopulateWorkspaceHistoryAsync(connection, userId, sessions);
        }

        return sessions;
    }

    public async Task<PageOf<IvilsonSession>> ListSessionsAsync(string userId, int page, int pageSize, bool includeArchived)
    {
        await using var connection = connections.Open();
        (string, object?)[] filters = [("$user", userId), ("$includeArchived", includeArchived)];
        await using var count = Command(connection, $"SELECT count(*) FROM Sessions {SessionPageFilter};", filters);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync());
        await using var list = Command(connection, $"""
            SELECT {SessionColumns} FROM Sessions {SessionPageFilter}
            ORDER BY Created DESC LIMIT $limit OFFSET $offset;
            """, filters);
        BindPage(list, page, pageSize);
        return new PageOf<IvilsonSession>
        {
            List = await ReadManyAsync(list, ReadSession),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<IvilsonSession?> FindLatestWorkspaceAsync(string userId, string name, bool daily)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            SELECT {SessionColumns} FROM Sessions
            WHERE UserId = $user AND Name = $name
              AND (NOT $daily OR (WorkMode = $dailyMode AND IsExternalWorkspace = 0 AND IsArchived = 0))
            ORDER BY Created DESC LIMIT 1;
            """, ("$user", userId), ("$name", name), ("$daily", daily), ("$dailyMode", (int)SessionWorkMode.Daily));
        return await ReadOneAsync(command, ReadSession);
    }

    public Task<IvilsonSession?> RenameSessionAsync(string sessionId, string userId, string name)
        => EditSessionAsync(sessionId, userId, true, "Name = $value", name);

    public Task<IvilsonSession?> ArchiveSessionAsync(string sessionId, string userId, bool isArchived)
        => EditSessionAsync(sessionId, userId, true, "IsArchived = $value", isArchived);

    public Task SetSessionStageAsync(string sessionId, int stage)
        => EditSessionAsync(sessionId, null, false, "CurrentStage = $value", stage);

    public Task SetSessionFactsAsync(string sessionId, string factsJson)
        => EditSessionAsync(sessionId, null, false, "FactsJson = $value", factsJson);

    public Task SetSessionPlanAsync(string sessionId, string planJson)
        => EditSessionAsync(sessionId, null, false, "GlobalPlanJson = $value", planJson);

    public Task SetSessionProjectUrlAsync(string sessionId, string url)
        => EditSessionAsync(sessionId, null, false, "ProjectUrl = $value", url);

    public Task ResetExecutionStateAsync(string sessionId, int stage)
        => EditSessionAsync(sessionId, null, false, "CurrentStage = $value, GlobalPlanJson = NULL, ActiveTaskId = NULL", stage);

    public Task<IvilsonSession?> ClearSessionAsync(string sessionId, string userId)
        => RemoveSessionHistoryAsync(sessionId, userId, deleteSession: false);

    public Task<IvilsonSession?> DeleteSessionAsync(string sessionId, string userId)
        => RemoveSessionHistoryAsync(sessionId, userId, deleteSession: true);

    private async Task<IvilsonConversation?> EditConversationAsync(
        string conversationId, string? userId, bool requireOwner, string assignment, object value, DateTime updatedAt)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            UPDATE Conversations SET {assignment} WHERE Id = $id AND (NOT $requireOwner OR UserId = $user)
            RETURNING {ConversationColumns};
            """, ("$id", conversationId), ("$user", userId), ("$requireOwner", requireOwner),
            ("$value", value), ("$updated", updatedAt));
        return await ReadOneAsync(command, ReadConversation);
    }

    private async Task<IvilsonSession?> EditSessionAsync(
        string sessionId, string? userId, bool requireOwner, string assignment, object? value)
    {
        await using var connection = connections.Open();
        await using var command = Command(connection, $"""
            UPDATE Sessions SET {assignment} WHERE Id = $id AND (NOT $requireOwner OR UserId = $user)
            RETURNING {SessionColumns};
            """, ("$id", sessionId), ("$user", userId), ("$requireOwner", requireOwner), ("$value", value));
        return await ReadOneAsync(command, ReadSession);
    }

    private async Task<IvilsonSession?> RemoveSessionHistoryAsync(string sessionId, string userId, bool deleteSession)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var find = Command(connection, $"""
            SELECT {SessionColumns} FROM Sessions WHERE Id = $id AND UserId = $user;
            """, ("$id", sessionId), ("$user", userId));
        find.Transaction = transaction;
        var session = await ReadOneAsync(find, ReadSession);
        if (session is null || (deleteSession && (session.Name == "默认工作区"
            || (session.Name == "日常助手" && session.WorkMode == SessionWorkMode.Daily && !session.IsExternalWorkspace))))
        {
            return null;
        }

        await using var messages = Command(connection, """
            DELETE FROM ChatMessages
            WHERE ConversationId IN (SELECT Id FROM Conversations WHERE SessionId = $id)
               OR IvilsonConversationId IN (SELECT Id FROM Conversations WHERE SessionId = $id);
            """, ("$id", sessionId));
        messages.Transaction = transaction;
        await messages.ExecuteNonQueryAsync();

        await using var conversations = Command(connection, "DELETE FROM Conversations WHERE SessionId = $id;", ("$id", sessionId));
        conversations.Transaction = transaction;
        await conversations.ExecuteNonQueryAsync();

        await using var finish = Command(connection, deleteSession
            ? "DELETE FROM Sessions WHERE Id = $id;"
            : "UPDATE Sessions SET CurrentStage = 0, FactsJson = '[]', GlobalPlanJson = NULL, ActiveTaskId = NULL WHERE Id = $id;",
            ("$id", sessionId));
        finish.Transaction = transaction;
        await finish.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return session;
    }

    private static async Task PopulateWorkspaceHistoryAsync(SqliteConnection connection, string userId, List<IvilsonSession> sessions)
    {
        var sessionsById = sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        await using var conversations = Command(connection, """
            SELECT c.Id, c.SessionId, c.TaskId, c.ThreadType, c.IsArchived, c.Model, c.Name,
                c.UserId, c.Created, c.UpdateAt, c.IsIndexed
            FROM Conversations c JOIN Sessions s ON c.SessionId = s.Id
            WHERE s.UserId = $user AND s.IsArchived = 0;
            """, ("$user", userId));
        var conversationsById = new Dictionary<string, IvilsonConversation>(StringComparer.Ordinal);
        foreach (var conversation in await ReadManyAsync(conversations, ReadConversation))
        {
            if (conversation.SessionId is string sessionId && sessionsById.TryGetValue(sessionId, out var session))
            {
                AddConversationToSession(session, conversation);
                conversationsById.Add(conversation.Id, conversation);
            }
        }

        await using var messages = Command(connection, """
            SELECT m.IvilsonConversationId, m.Id, m.ConversationId, m.UserContent, m.AiContent, m.ThinkingContent,
                m.TokenUsed, m.SystemPrompt, m.ReplyId, m.Created, m.ToolCallResult, m.MessageType, m.NotificationMeta
            FROM ChatMessages m JOIN Conversations c ON m.IvilsonConversationId = c.Id
                JOIN Sessions s ON c.SessionId = s.Id
            WHERE s.UserId = $user AND s.IsArchived = 0;
            """, ("$user", userId));
        await using var reader = await messages.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (conversationsById.TryGetValue(reader.GetString(0), out var conversation))
            {
                conversation.ChatMessages.Add(SqliteConversationMessageStore.ReadMessage(reader, offset: 1));
            }
        }
    }

    private static void AddConversationToSession(IvilsonSession session, IvilsonConversation conversation)
    {
        conversation.Session = session;
        session.Conversations.Add(conversation);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void BindPage(SqliteCommand command, int page, int pageSize)
    {
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
    }

    private static async Task<T?> ReadOneAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> map) where T : class
    {
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
    }

    private static async Task<List<T>> ReadManyAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> map)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static IvilsonConversation ReadConversation(SqliteDataReader row) => new()
    {
        Id = row.GetString(0),
        SessionId = ReadNullableText(row, 1),
        TaskId = ReadNullableText(row, 2),
        ThreadType = (ConversationThreadType)row.GetInt32(3),
        IsArchived = row.GetBoolean(4),
        Model = row.GetString(5),
        Name = row.GetString(6),
        UserId = row.GetString(7),
        Created = row.GetDateTime(8),
        UpdateAt = row.GetDateTime(9),
        IsIndexed = row.GetBoolean(10),
    };

    private static IvilsonSession ReadSession(SqliteDataReader row) => new()
    {
        Id = row.GetString(0),
        UserId = row.GetString(1),
        Name = row.GetString(2),
        WorkspacePath = row.GetString(3),
        ProjectUrl = ReadNullableText(row, 4),
        CurrentStage = row.GetInt32(5),
        FactsJson = row.GetString(6),
        GlobalPlanJson = ReadNullableText(row, 7),
        CurrentPlanArtifactId = ReadNullableText(row, 8),
        ActiveTaskId = ReadNullableText(row, 9),
        Created = row.GetDateTime(10),
        HistorySummary = ReadNullableText(row, 11),
        LastCompressedAt = row.IsDBNull(12) ? null : row.GetDateTime(12),
        IsArchived = row.GetBoolean(13),
        IsExternalWorkspace = row.GetBoolean(14),
        WorkMode = (SessionWorkMode)row.GetInt32(15),
        ModeVersion = row.GetInt32(16),
        PlanModeActive = row.GetBoolean(17),
        PlanModeVersion = row.GetInt32(18),
        PlanModeChangedAtUtc = row.IsDBNull(19) ? null : row.GetDateTime(19),
        PlanModeChangedBy = ReadNullableText(row, 20),
    };

    private static string? ReadNullableText(SqliteDataReader row, int ordinal)
        => row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
}
