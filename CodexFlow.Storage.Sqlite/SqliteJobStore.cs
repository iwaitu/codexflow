// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

public sealed class SqliteJobStore(SqliteConnectionFactory connections) : IJobStore
{
    internal const string BackgroundJobColumns = """
        JobId, SessionId, TaskId, JobType, Status, PayloadJson,
        ResultPayloadJson, ErrorMessage, RunnerId, ClaimToken, LeaseExpiresAtUtc, HeartbeatAtUtc,
        DequeuedAtUtc, Attempt, LastEventSeq, WaitingReason, CreatedAtUtc, StartedAtUtc,
        FinishedAtUtc, UpdatedAtUtc, CancelledAtUtc, Notified, NotifiedAtUtc, NotificationStatus,
        NotificationAttemptCount, NotificationLastAttemptAtUtc, NotificationLastError, NotificationLeaseOwner, NotificationLeaseExpiresAtUtc, NotificationSuppressedReason,
        NotificationChannels, SummaryMode, SummaryMessageId, SummaryConversationId, SummaryTokenUsageInput, SummaryTokenUsageOutput,
        SummaryGeneratedAtUtc, ShortSummary, NotificationRead, NotificationReadAtUtc
        """;

    private const string JobCheckpointColumns = """
        CheckpointId, JobId, SessionId, TaskId, SequenceNumber, CurrentStep,
        PendingActionType, PendingPayloadJson, ResumeToken, WorkspacePath, ActiveTaskId, Attempt,
        LastEventSeq, CheckpointData, CreatedAtUtc
        """;

    public async Task CreateAsync(BackgroundJob job, JobCheckpoint checkpoint, OutboxEvent entry, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await InsertBackgroundJobAsync(connection, transaction, job, ct).ConfigureAwait(false);
        await InsertJobCheckpointAsync(connection, transaction, checkpoint, ct).ConfigureAwait(false);
        var sequence = await SqliteOutboxStore.InsertAsync(connection, transaction, entry, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        entry.Seq = sequence;
    }

    public async Task<BackgroundJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM BackgroundJobs WHERE JobId = $job";
        Add(command, "$job", jobId);
        var status = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return status is long value ? (BackgroundJobStatus)value : null;
    }

    public async Task<BackgroundJob?> FindAsync(string jobId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BackgroundJobColumns} FROM BackgroundJobs WHERE JobId = $job";
        Add(command, "$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    public async Task<JobCheckpoint?> FindLatestCheckpointAsync(string jobId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {JobCheckpointColumns} FROM JobCheckpoints WHERE JobId = $job ORDER BY SequenceNumber DESC LIMIT 1
            """;
        Add(command, "$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadCheckpoint(reader) : null;
    }

    public async Task<IReadOnlyList<BackgroundJob>> ReadActiveAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {BackgroundJobColumns} FROM BackgroundJobs WHERE SessionId = $session
              AND Status IN ($queued, $running, $waiting, $recovery)
            """;
        Add(command, "$session", sessionId);
        Add(command, "$queued", (int)BackgroundJobStatus.Queued);
        Add(command, "$running", (int)BackgroundJobStatus.Running);
        Add(command, "$waiting", (int)BackgroundJobStatus.WaitingUser);
        Add(command, "$recovery", (int)BackgroundJobStatus.FailedRecoveryNeeded);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var jobs = new List<BackgroundJob>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            jobs.Add(ReadJob(reader));
        return jobs;
    }

    public async Task<int> ApplyTransitionsAsync(IReadOnlyList<JobTransition> transitions, CancellationToken ct = default)
    {
        if (transitions.Count == 0) return 0;

        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var sequences = new List<(OutboxEvent Event, long Sequence)>(transitions.Count);
        foreach (var transition in transitions)
        {
            if (!await ApplyAsync(connection, transaction, transition, ct).ConfigureAwait(false)) continue;
            if (transition.Checkpoint is { } checkpoint)
                await InsertJobCheckpointAsync(connection, transaction, checkpoint, ct).ConfigureAwait(false);
            var sequence = await SqliteOutboxStore.InsertAsync(connection, transaction, transition.Event, ct).ConfigureAwait(false);
            sequences.Add((transition.Event, sequence));
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (var (entry, sequence) in sequences) entry.Seq = sequence;
        return sequences.Count;
    }

    private static async Task<bool> ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobTransition transition, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var job = transition.UpdatedJob;
        var changes = transition.Kind switch
        {
            JobTransitionKind.Retry => "PayloadJson = $payload, Attempt = $attempt, ErrorMessage = NULL, " +
                "WaitingReason = NULL, CancelledAtUtc = NULL, FinishedAtUtc = NULL",
            JobTransitionKind.Cancel => "CancelledAtUtc = $cancelled, FinishedAtUtc = $finished, " +
                "NotificationStatus = CASE WHEN $worker THEN $notification ELSE NotificationStatus END, " +
                "ErrorMessage = CASE WHEN $worker THEN $error ELSE ErrorMessage END",
            JobTransitionKind.Resume => "PayloadJson = $payload, WaitingReason = NULL, ErrorMessage = NULL, RunnerId = NULL, " +
                "ClaimToken = NULL, LeaseExpiresAtUtc = NULL, DequeuedAtUtc = NULL, StartedAtUtc = NULL, " +
                "FinishedAtUtc = NULL, NotificationStatus = $notification",
            JobTransitionKind.DenyApproval => "WaitingReason = NULL, ErrorMessage = $error, ResultPayloadJson = $result, " +
                "FinishedAtUtc = $finished",
            _ => throw new ArgumentOutOfRangeException(nameof(transition))
        };
        command.CommandText = $"""
            UPDATE BackgroundJobs SET Status = $status, UpdatedAtUtc = $now, {changes}
            WHERE JobId = $job AND Status = $expectedStatus AND ClaimToken IS $expectedToken AND Attempt = $expectedAttempt
            """;
        if (transition.Kind is JobTransitionKind.Resume or JobTransitionKind.DenyApproval)
        {
            command.CommandText += """

                AND (SELECT CheckpointId FROM JobCheckpoints WHERE JobId = $job ORDER BY SequenceNumber DESC LIMIT 1)
                    IS $expectedCheckpoint
                """;
            Add(command, "$expectedCheckpoint", transition.ExpectedCheckpointId);
        }

        Add(command, "$job", job.JobId);
        Add(command, "$status", (int)job.Status);
        Add(command, "$now", job.UpdatedAtUtc);
        Add(command, "$expectedStatus", (int)transition.ExpectedStatus);
        Add(command, "$expectedToken", transition.ExpectedClaimToken);
        Add(command, "$expectedAttempt", transition.ExpectedAttempt);
        switch (transition.Kind)
        {
            case JobTransitionKind.Retry:
                Add(command, "$payload", job.PayloadJson);
                Add(command, "$attempt", job.Attempt);
                break;
            case JobTransitionKind.Cancel:
                Add(command, "$cancelled", job.CancelledAtUtc);
                Add(command, "$finished", job.FinishedAtUtc);
                Add(command, "$worker", WorkerJobTypes.IsWorkerJobType(job.JobType));
                Add(command, "$notification", job.NotificationStatus?.ToString());
                Add(command, "$error", job.ErrorMessage);
                break;
            case JobTransitionKind.Resume:
                Add(command, "$payload", job.PayloadJson);
                Add(command, "$notification", job.NotificationStatus?.ToString());
                break;
            case JobTransitionKind.DenyApproval:
                Add(command, "$error", job.ErrorMessage);
                Add(command, "$result", job.ResultPayloadJson);
                Add(command, "$finished", job.FinishedAtUtc);
                break;
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<JobDeletionResult> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT JobId FROM BackgroundJobs WHERE SessionId = $session";
        Add(command, "$session", sessionId);
        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }

        command.CommandText = """
            DELETE FROM JobCheckpoints WHERE SessionId = $session;
            DELETE FROM OutboxEvents WHERE SessionId = $session;
            DELETE FROM BackgroundJobs WHERE SessionId = $session;
            """;
        var changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new JobDeletionResult(ids, changed);
    }

    public async Task<BackgroundJob?> FindOwnedJobAsync(string jobId, string userId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {BackgroundJobColumns} FROM BackgroundJobs AS job WHERE job.JobId = $job
            AND EXISTS (SELECT 1 FROM Sessions WHERE Id = job.SessionId AND UserId = $user)
            """;
        Add(command, "$job", jobId);
        Add(command, "$user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    public async Task<SessionRecord?> FindOwnedSessionAsync(string sessionId, string userId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, UserId, WorkspacePath FROM Sessions WHERE Id = $session AND UserId = $user";
        Add(command, "$session", sessionId);
        Add(command, "$user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new SessionRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<IReadOnlyList<string>> ListSessionIdsAsync(string userId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Sessions WHERE UserId = $user";
        Add(command, "$user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString(0));

        return ids;
    }

    private static async Task InsertBackgroundJobAsync(
        SqliteConnection connection, SqliteTransaction transaction, BackgroundJob job, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO BackgroundJobs ({BackgroundJobColumns})
            VALUES ($JobId, $SessionId, $TaskId, $JobType, $Status, $PayloadJson,
            $ResultPayloadJson, $ErrorMessage, $RunnerId, $ClaimToken, $LeaseExpiresAtUtc, $HeartbeatAtUtc,
            $DequeuedAtUtc, $Attempt, $LastEventSeq, $WaitingReason, $CreatedAtUtc, $StartedAtUtc,
            $FinishedAtUtc, $UpdatedAtUtc, $CancelledAtUtc, $Notified, $NotifiedAtUtc, $NotificationStatus,
            $NotificationAttemptCount, $NotificationLastAttemptAtUtc, $NotificationLastError, $NotificationLeaseOwner, $NotificationLeaseExpiresAtUtc, $NotificationSuppressedReason,
            $NotificationChannels, $SummaryMode, $SummaryMessageId, $SummaryConversationId, $SummaryTokenUsageInput, $SummaryTokenUsageOutput,
            $SummaryGeneratedAtUtc, $ShortSummary, $NotificationRead, $NotificationReadAtUtc)
            """;
        Add(command, "$JobId", job.JobId);
        Add(command, "$SessionId", job.SessionId);
        Add(command, "$TaskId", job.TaskId);
        Add(command, "$JobType", job.JobType);
        Add(command, "$Status", (int)job.Status);
        Add(command, "$PayloadJson", job.PayloadJson);
        Add(command, "$ResultPayloadJson", job.ResultPayloadJson);
        Add(command, "$ErrorMessage", job.ErrorMessage);
        Add(command, "$RunnerId", job.RunnerId);
        Add(command, "$ClaimToken", job.ClaimToken);
        Add(command, "$LeaseExpiresAtUtc", job.LeaseExpiresAtUtc);
        Add(command, "$HeartbeatAtUtc", job.HeartbeatAtUtc);
        Add(command, "$DequeuedAtUtc", job.DequeuedAtUtc);
        Add(command, "$Attempt", job.Attempt);
        Add(command, "$LastEventSeq", job.LastEventSeq);
        Add(command, "$WaitingReason", job.WaitingReason);
        Add(command, "$CreatedAtUtc", job.CreatedAtUtc);
        Add(command, "$StartedAtUtc", job.StartedAtUtc);
        Add(command, "$FinishedAtUtc", job.FinishedAtUtc);
        Add(command, "$UpdatedAtUtc", job.UpdatedAtUtc);
        Add(command, "$CancelledAtUtc", job.CancelledAtUtc);
        Add(command, "$Notified", job.Notified);
        Add(command, "$NotifiedAtUtc", job.NotifiedAtUtc);
        Add(command, "$NotificationStatus", job.NotificationStatus?.ToString());
        Add(command, "$NotificationAttemptCount", job.NotificationAttemptCount);
        Add(command, "$NotificationLastAttemptAtUtc", job.NotificationLastAttemptAtUtc);
        Add(command, "$NotificationLastError", job.NotificationLastError);
        Add(command, "$NotificationLeaseOwner", job.NotificationLeaseOwner);
        Add(command, "$NotificationLeaseExpiresAtUtc", job.NotificationLeaseExpiresAtUtc);
        Add(command, "$NotificationSuppressedReason", job.NotificationSuppressedReason);
        Add(command, "$NotificationChannels", job.NotificationChannels);
        Add(command, "$SummaryMode", job.SummaryMode);
        Add(command, "$SummaryMessageId", job.SummaryMessageId);
        Add(command, "$SummaryConversationId", job.SummaryConversationId);
        Add(command, "$SummaryTokenUsageInput", job.SummaryTokenUsageInput);
        Add(command, "$SummaryTokenUsageOutput", job.SummaryTokenUsageOutput);
        Add(command, "$SummaryGeneratedAtUtc", job.SummaryGeneratedAtUtc);
        Add(command, "$ShortSummary", job.ShortSummary);
        Add(command, "$NotificationRead", job.NotificationRead);
        Add(command, "$NotificationReadAtUtc", job.NotificationReadAtUtc);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task InsertJobCheckpointAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobCheckpoint checkpoint, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO JobCheckpoints ({JobCheckpointColumns})
            VALUES ($CheckpointId, $JobId, $SessionId, $TaskId, $SequenceNumber, $CurrentStep,
            $PendingActionType, $PendingPayloadJson, $ResumeToken, $WorkspacePath, $ActiveTaskId, $Attempt,
            $LastEventSeq, $CheckpointData, $CreatedAtUtc)
            """;
        Add(command, "$CheckpointId", checkpoint.CheckpointId);
        Add(command, "$JobId", checkpoint.JobId);
        Add(command, "$SessionId", checkpoint.SessionId);
        Add(command, "$TaskId", checkpoint.TaskId);
        Add(command, "$SequenceNumber", checkpoint.SequenceNumber);
        Add(command, "$CurrentStep", checkpoint.CurrentStep);
        Add(command, "$PendingActionType", checkpoint.PendingActionType);
        Add(command, "$PendingPayloadJson", checkpoint.PendingPayloadJson);
        Add(command, "$ResumeToken", checkpoint.ResumeToken);
        Add(command, "$WorkspacePath", checkpoint.WorkspacePath);
        Add(command, "$ActiveTaskId", checkpoint.ActiveTaskId);
        Add(command, "$Attempt", checkpoint.Attempt);
        Add(command, "$LastEventSeq", checkpoint.LastEventSeq);
        Add(command, "$CheckpointData", checkpoint.CheckpointData);
        Add(command, "$CreatedAtUtc", checkpoint.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static BackgroundJob ReadJob(SqliteDataReader reader) => new()
    {
        JobId = reader.GetString(0),
        SessionId = reader.IsDBNull(1) ? null : reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetString(2),
        JobType = reader.GetString(3),
        Status = (BackgroundJobStatus)reader.GetInt32(4),
        PayloadJson = reader.GetString(5),
        ResultPayloadJson = reader.IsDBNull(6) ? null : reader.GetString(6),
        ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
        RunnerId = reader.IsDBNull(8) ? null : reader.GetString(8),
        ClaimToken = reader.IsDBNull(9) ? null : reader.GetString(9),
        LeaseExpiresAtUtc = reader.IsDBNull(10) ? null : DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
        HeartbeatAtUtc = reader.IsDBNull(11) ? null : DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc),
        DequeuedAtUtc = reader.IsDBNull(12) ? null : DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
        Attempt = reader.GetInt32(13),
        LastEventSeq = reader.GetInt64(14),
        WaitingReason = reader.IsDBNull(15) ? null : reader.GetString(15),
        CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(16), DateTimeKind.Utc),
        StartedAtUtc = reader.IsDBNull(17) ? null : DateTime.SpecifyKind(reader.GetDateTime(17), DateTimeKind.Utc),
        FinishedAtUtc = reader.IsDBNull(18) ? null : DateTime.SpecifyKind(reader.GetDateTime(18), DateTimeKind.Utc),
        UpdatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(19), DateTimeKind.Utc),
        CancelledAtUtc = reader.IsDBNull(20) ? null : DateTime.SpecifyKind(reader.GetDateTime(20), DateTimeKind.Utc),
        Notified = reader.GetBoolean(21),
        NotifiedAtUtc = reader.IsDBNull(22) ? null : DateTime.SpecifyKind(reader.GetDateTime(22), DateTimeKind.Utc),
        NotificationStatus = reader.IsDBNull(23) ? null : Enum.Parse<NotificationStatus>(reader.GetString(23)),
        NotificationAttemptCount = reader.GetInt32(24),
        NotificationLastAttemptAtUtc = reader.IsDBNull(25) ? null : DateTime.SpecifyKind(reader.GetDateTime(25), DateTimeKind.Utc),
        NotificationLastError = reader.IsDBNull(26) ? null : reader.GetString(26),
        NotificationLeaseOwner = reader.IsDBNull(27) ? null : reader.GetString(27),
        NotificationLeaseExpiresAtUtc = reader.IsDBNull(28) ? null : DateTime.SpecifyKind(reader.GetDateTime(28), DateTimeKind.Utc),
        NotificationSuppressedReason = reader.IsDBNull(29) ? null : reader.GetString(29),
        NotificationChannels = reader.IsDBNull(30) ? null : reader.GetString(30),
        SummaryMode = reader.IsDBNull(31) ? null : reader.GetString(31),
        SummaryMessageId = reader.IsDBNull(32) ? null : reader.GetString(32),
        SummaryConversationId = reader.IsDBNull(33) ? null : reader.GetString(33),
        SummaryTokenUsageInput = reader.IsDBNull(34) ? null : reader.GetInt32(34),
        SummaryTokenUsageOutput = reader.IsDBNull(35) ? null : reader.GetInt32(35),
        SummaryGeneratedAtUtc = reader.IsDBNull(36) ? null : DateTime.SpecifyKind(reader.GetDateTime(36), DateTimeKind.Utc),
        ShortSummary = reader.IsDBNull(37) ? null : reader.GetString(37),
        NotificationRead = reader.GetBoolean(38),
        NotificationReadAtUtc = reader.IsDBNull(39) ? null : DateTime.SpecifyKind(reader.GetDateTime(39), DateTimeKind.Utc),
    };

    private static JobCheckpoint ReadCheckpoint(SqliteDataReader reader) => new()
    {
        CheckpointId = reader.GetString(0),
        JobId = reader.GetString(1),
        SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
        TaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
        SequenceNumber = reader.GetInt32(4),
        CurrentStep = reader.IsDBNull(5) ? null : reader.GetString(5),
        PendingActionType = reader.IsDBNull(6) ? null : reader.GetString(6),
        PendingPayloadJson = reader.IsDBNull(7) ? null : reader.GetString(7),
        ResumeToken = reader.IsDBNull(8) ? null : reader.GetString(8),
        WorkspacePath = reader.IsDBNull(9) ? null : reader.GetString(9),
        ActiveTaskId = reader.IsDBNull(10) ? null : reader.GetString(10),
        Attempt = reader.GetInt32(11),
        LastEventSeq = reader.GetInt64(12),
        CheckpointData = reader.GetString(13),
        CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc)
    };

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
