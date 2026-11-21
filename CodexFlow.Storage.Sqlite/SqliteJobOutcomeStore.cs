// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Chat;
using CodexFlow.Domain.Jobs;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteJobOutcomeStore(SqliteConnectionFactory connections) : IJobOutcomeStore
{
    public async Task<string?> FindAnnouncementConversationAsync(string sessionId, string userId, string? taskId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id FROM Conversations WHERE SessionId = $session AND UserId = $user AND IsArchived = 0
              AND (ThreadType IN ($main, $chat) OR (ThreadType = $worker AND TaskId = $task))
            ORDER BY CASE ThreadType WHEN $worker THEN 0 WHEN $main THEN 1 ELSE 2 END, UpdateAt DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$task", string.IsNullOrWhiteSpace(taskId) ? DBNull.Value : (object)taskId);
        command.Parameters.AddWithValue("$main", (int)ConversationThreadType.Master);
        command.Parameters.AddWithValue("$chat", (int)ConversationThreadType.Chat);
        command.Parameters.AddWithValue("$worker", (int)ConversationThreadType.Worker);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task RecordAnnouncementAsync(BackgroundJob job, string? messageId, string conversationId, DateTime now, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BackgroundJobs SET NotificationStatus = $delivered, NotificationChannels = 'main_session',
              SummaryMessageId = $message, SummaryConversationId = $conversation, Notified = 1,
              NotifiedAtUtc = $now, UpdatedAtUtc = $now
            WHERE JobId = $job AND Attempt = $attempt AND Status = $status
              AND (NotificationStatus IS NULL OR NotificationStatus NOT IN ($delivered, $sync, $suppressed))
            """;
        command.Parameters.AddWithValue("$job", job.JobId);
        command.Parameters.AddWithValue("$attempt", job.Attempt);
        command.Parameters.AddWithValue("$status", (int)job.Status);
        command.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$delivered", nameof(NotificationStatus.SummaryDelivered));
        command.Parameters.AddWithValue("$sync", nameof(NotificationStatus.SyncDelivered));
        command.Parameters.AddWithValue("$suppressed", nameof(NotificationStatus.Suppressed));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
