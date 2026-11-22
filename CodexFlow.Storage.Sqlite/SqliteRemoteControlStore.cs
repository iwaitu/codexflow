// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Core.Remote;
using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// SQLite persistence for pairing-isolated attachment staging, idempotent chat ops,
/// revocation watermarks, external workspace authorizations, and job origin tracking.
/// </summary>
public sealed class SqliteRemoteControlStore(SqliteConnectionFactory connections) :
    IRemoteAttachmentUploadStore,
    IRemoteChatOpStore,
    IRemoteWorkspaceAuthorizationStore,
    IRemoteRevocationStore,
    IRemoteJobOriginStore
{
    private const string AccountScopeKey = "account_revocation_seq";
    private const string DeviceScopeKey = "device_revocation_seq";

    private readonly SqliteConnectionFactory _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<RemoteUploadReservationResult> TryReserveUploadAsync(
        RemoteAttachmentUploadRecord record,
        bool leaseIsActive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        await using var conn = _connections.Open();
        await using var tx = conn.BeginTransaction(deferred: false);

        var existing = await ReadAttachmentUploadAsync(conn, tx, record.PairingId, record.SessionId, record.UploadId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.FileSha256, record.FileSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteUploadReservationResult(RemoteUploadReservationOutcome.HashConflict, existing);
            }

            if (string.Equals(existing.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteUploadReservationResult(RemoteUploadReservationOutcome.AlreadyCompletedSameHash, existing);
            }

            if (leaseIsActive || existing.UpdatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                return new RemoteUploadReservationResult(RemoteUploadReservationOutcome.PendingInProgress, existing);
            }

            await using var updateCmd = CreateCommand(conn, tx, """
                UPDATE "RemoteAttachmentUploads"
                   SET "FileSha256" = $hash,
                       "Status" = $status,
                       "RelativePath" = $path,
                       "PairingIdHash8" = $hash8,
                       "UploadedAttachmentDtoJson" = NULL,
                       "UpdatedAtUtc" = $updated
                 WHERE "PairingId" = $pid AND "SessionId" = $sid AND "UploadId" = $uid;
                """);
            updateCmd.Parameters.AddWithValue("$hash", record.FileSha256);
            updateCmd.Parameters.AddWithValue("$status", record.Status);
            updateCmd.Parameters.AddWithValue("$path", record.RelativePath);
            updateCmd.Parameters.AddWithValue("$hash8", record.PairingIdHash8);
            updateCmd.Parameters.AddWithValue("$updated", record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            updateCmd.Parameters.AddWithValue("$pid", record.PairingId);
            updateCmd.Parameters.AddWithValue("$sid", record.SessionId);
            updateCmd.Parameters.AddWithValue("$uid", record.UploadId);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new RemoteUploadReservationResult(RemoteUploadReservationOutcome.AcquiredNew, record);
        }

        await using var insertCmd = CreateCommand(conn, tx, """
            INSERT INTO "RemoteAttachmentUploads" (
                "PairingId", "SessionId", "UploadId", "FileSha256", "Status",
                "RelativePath", "PairingIdHash8", "UploadedAttachmentDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
            ) VALUES (
                $pid, $sid, $uid, $hash, $status,
                $path, $hash8, $dto, $created, $updated
            );
            """);
        insertCmd.Parameters.AddWithValue("$pid", record.PairingId);
        insertCmd.Parameters.AddWithValue("$sid", record.SessionId);
        insertCmd.Parameters.AddWithValue("$uid", record.UploadId);
        insertCmd.Parameters.AddWithValue("$hash", record.FileSha256);
        insertCmd.Parameters.AddWithValue("$status", record.Status);
        insertCmd.Parameters.AddWithValue("$path", record.RelativePath);
        insertCmd.Parameters.AddWithValue("$hash8", record.PairingIdHash8);
        insertCmd.Parameters.AddWithValue("$dto", (object?)record.UploadedAttachmentDtoJson ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        insertCmd.Parameters.AddWithValue("$updated", record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RemoteUploadReservationResult(RemoteUploadReservationOutcome.AcquiredNew, record);
    }

    public async Task MarkUploadCompletedAsync(
        string pairingId,
        string sessionId,
        string uploadId,
        string fileSha256,
        string relativePath,
        string uploadedAttachmentDtoJson,
        CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var tx = conn.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var cmd = CreateCommand(conn, tx, """
            UPDATE "RemoteAttachmentUploads"
               SET "Status" = 'completed',
                   "FileSha256" = $hash,
                   "RelativePath" = $path,
                   "UploadedAttachmentDtoJson" = $dto,
                   "UpdatedAtUtc" = $updated
             WHERE "PairingId" = $pid AND "SessionId" = $sid AND "UploadId" = $uid;
            """);
        cmd.Parameters.AddWithValue("$hash", fileSha256);
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.Parameters.AddWithValue("$dto", uploadedAttachmentDtoJson);
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.Parameters.AddWithValue("$pid", pairingId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$uid", uploadId);
        var changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (changed == 0)
        {
            await using var ins = CreateCommand(conn, tx, """
                INSERT INTO "RemoteAttachmentUploads" (
                    "PairingId", "SessionId", "UploadId", "FileSha256", "Status",
                    "RelativePath", "PairingIdHash8", "UploadedAttachmentDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
                ) VALUES (
                    $pid, $sid, $uid, $hash, 'completed',
                    $path, $hash8, $dto, $now, $now
                );
                """);
            ins.Parameters.AddWithValue("$pid", pairingId);
            ins.Parameters.AddWithValue("$sid", sessionId);
            ins.Parameters.AddWithValue("$uid", uploadId);
            ins.Parameters.AddWithValue("$hash", fileSha256);
            ins.Parameters.AddWithValue("$path", relativePath);
            ins.Parameters.AddWithValue("$hash8", RemoteNamingHelper.ComputePairingIdHash8(pairingId));
            ins.Parameters.AddWithValue("$dto", uploadedAttachmentDtoJson);
            ins.Parameters.AddWithValue("$now", now);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeletePendingUploadAsync(string pairingId, string sessionId, string uploadId, CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            DELETE FROM "RemoteAttachmentUploads"
             WHERE "PairingId" = $pid AND "SessionId" = $sid AND "UploadId" = $uid AND "Status" = 'pending';
            """);
        cmd.Parameters.AddWithValue("$pid", pairingId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$uid", uploadId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<RemoteAttachmentUploadRecord?> FindUploadAsync(string pairingId, string sessionId, string uploadId, CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        return await ReadAttachmentUploadAsync(conn, null, pairingId, sessionId, uploadId, ct).ConfigureAwait(false);
    }

    public async Task<RemoteAttachmentUploadRecord?> FindCompletedByRelativePathAsync(string sessionId, string relativePath, CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            SELECT "PairingId", "SessionId", "UploadId", "FileSha256", "Status", "RelativePath", "PairingIdHash8",
                   "UploadedAttachmentDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
              FROM "RemoteAttachmentUploads"
             WHERE "SessionId" = $sid AND "Status" = 'completed' AND "RelativePath" = $path
             LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAttachmentUploadRow(reader) : null;
    }

    public async Task<IReadOnlyList<RemoteAttachmentUploadRecord>> ListPendingUploadsAsync(CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            SELECT "PairingId", "SessionId", "UploadId", "FileSha256", "Status", "RelativePath", "PairingIdHash8",
                   "UploadedAttachmentDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
              FROM "RemoteAttachmentUploads"
             WHERE "Status" = 'pending';
            """);
        var list = new List<RemoteAttachmentUploadRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAttachmentUploadRow(reader));
        }

        return list;
    }

    public async Task<RemoteChatOpReserveResult> TryReserveAsync(RemoteChatOpRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = _connections.Open();
        await using var tx = conn.BeginTransaction(deferred: false);

        var existing = await ReadChatOpAsync(conn, tx, record.PairingId, record.SessionId, record.ClientOpId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var outcome = string.Equals(existing.RequestDigest, record.RequestDigest, StringComparison.OrdinalIgnoreCase)
                ? RemoteChatOpReserveOutcome.DuplicateSameDigest
                : RemoteChatOpReserveOutcome.DigestConflict;
            return new RemoteChatOpReserveResult(outcome, existing);
        }

        await using var cmd = CreateCommand(conn, tx, """
            INSERT INTO "RemoteChatOps" (
                "PairingId", "SessionId", "ClientOpId", "RequestDigest", "Status",
                "ConversationId", "MessageId", "BindingVersion", "AccountRevocationSeq", "DeviceRevocationSeq",
                "ResponseDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
            ) VALUES (
                $pid, $sid, $oid, $digest, $status,
                $cid, $mid, $bv, $aseq, $dseq,
                $resp, $created, $updated
            );
            """);
        cmd.Parameters.AddWithValue("$pid", record.PairingId);
        cmd.Parameters.AddWithValue("$sid", record.SessionId);
        cmd.Parameters.AddWithValue("$oid", record.ClientOpId);
        cmd.Parameters.AddWithValue("$digest", record.RequestDigest);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$cid", (object?)record.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)record.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bv", record.BindingVersion);
        cmd.Parameters.AddWithValue("$aseq", record.AccountRevocationSeq);
        cmd.Parameters.AddWithValue("$dseq", record.DeviceRevocationSeq);
        cmd.Parameters.AddWithValue("$resp", (object?)record.ResponseDtoJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updated", record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RemoteChatOpReserveResult(RemoteChatOpReserveOutcome.AcquiredNew, record);
    }

    public async Task UpdateStatusAsync(
        string pairingId,
        string sessionId,
        string clientOpId,
        string status,
        string? conversationId = null,
        string? messageId = null,
        string? responseDtoJson = null,
        CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            UPDATE "RemoteChatOps"
               SET "Status" = $status,
                   "ConversationId" = COALESCE($cid, "ConversationId"),
                   "MessageId" = COALESCE($mid, "MessageId"),
                   "ResponseDtoJson" = COALESCE($resp, "ResponseDtoJson"),
                   "UpdatedAtUtc" = $updated
             WHERE "PairingId" = $pid AND "SessionId" = $sid AND "ClientOpId" = $oid;
            """);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$cid", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resp", (object?)responseDtoJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$pid", pairingId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$oid", clientOpId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<RemoteChatOpRecord?> FindAsync(string pairingId, string sessionId, string clientOpId, CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        return await ReadChatOpAsync(conn, null, pairingId, sessionId, clientOpId, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsAuthorizedExternalWorkspaceAsync(string workspaceRealPath, CancellationToken ct = default)
    {
        if (!RemotePathSecurityHelper.TryNormalizeRealDirectory(workspaceRealPath, out var normalized))
        {
            return false;
        }

        var list = await ListAuthorizedWorkspacesAsync(ct).ConfigureAwait(false);
        return list.Any(entry => RemotePathSecurityHelper.IsSubpathOrEqual(normalized, entry.WorkspaceRealPath));
    }

    public async Task<IReadOnlyList<RemoteWorkspaceAuthorizationEntry>> ListAuthorizedWorkspacesAsync(CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            SELECT "WorkspaceRealPath", "SessionId", "AuthorizedAtUtc"
              FROM "RemoteWorkspaceAuthorizations"
             ORDER BY "WorkspaceRealPath" ASC;
            """);
        var list = new List<RemoteWorkspaceAuthorizationEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RemoteWorkspaceAuthorizationEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    public async Task SetExternalWorkspaceAuthorizedAsync(string workspaceRealPath, string? sessionId, bool authorized, CancellationToken ct = default)
    {
        if (!RemotePathSecurityHelper.TryNormalizeRealDirectory(workspaceRealPath, out var normalized))
        {
            throw new ArgumentException("Invalid workspace directory path.", nameof(workspaceRealPath));
        }

        await using var conn = _connections.Open();
        if (authorized)
        {
            await using var cmd = CreateCommand(conn, null, """
                INSERT INTO "RemoteWorkspaceAuthorizations" ("WorkspaceRealPath", "SessionId", "AuthorizedAtUtc")
                VALUES ($path, $sid, $at)
                ON CONFLICT("WorkspaceRealPath") DO UPDATE
                    SET "SessionId" = COALESCE(excluded."SessionId", "RemoteWorkspaceAuthorizations"."SessionId"),
                        "AuthorizedAtUtc" = excluded."AuthorizedAtUtc";
                """);
            cmd.Parameters.AddWithValue("$path", normalized);
            cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using var cmd = CreateCommand(conn, null, """
                DELETE FROM "RemoteWorkspaceAuthorizations" WHERE "WorkspaceRealPath" = $path;
                """);
            cmd.Parameters.AddWithValue("$path", normalized);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task ApplyRevocationAsync(
        long? minAccountRevocationSeq,
        long? minDeviceRevocationSeq,
        IReadOnlyList<Guid>? revokedPairingIds,
        CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var tx = conn.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        if (minAccountRevocationSeq.HasValue)
        {
            await UpsertWatermarkAsync(conn, tx, AccountScopeKey, minAccountRevocationSeq.Value, now, ct).ConfigureAwait(false);
        }

        if (minDeviceRevocationSeq.HasValue)
        {
            await UpsertWatermarkAsync(conn, tx, DeviceScopeKey, minDeviceRevocationSeq.Value, now, ct).ConfigureAwait(false);
        }

        if (revokedPairingIds is not null)
        {
            foreach (var pairingId in revokedPairingIds)
            {
                if (pairingId == Guid.Empty)
                {
                    continue;
                }

                await using var cmd = CreateCommand(conn, tx, """
                    INSERT INTO "RemoteRevokedPairings" ("PairingId", "RevokedAtUtc")
                    VALUES ($pid, $at)
                    ON CONFLICT("PairingId") DO NOTHING;
                    """);
                cmd.Parameters.AddWithValue("$pid", pairingId.ToString("D"));
                cmd.Parameters.AddWithValue("$at", now);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsRevokedAsync(RemotePrincipalStamp? stamp, CancellationToken ct = default)
    {
        if (stamp is null)
        {
            return false;
        }

        await using var conn = _connections.Open();
        await using (var pairCmd = CreateCommand(conn, null, """
            SELECT 1 FROM "RemoteRevokedPairings" WHERE "PairingId" = $pid LIMIT 1;
            """))
        {
            pairCmd.Parameters.AddWithValue("$pid", stamp.PairingId.ToString("D"));
            if (await pairCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }

        await using var wmCmd = CreateCommand(conn, null, """
            SELECT "ScopeKey", "RevocationSeq" FROM "RemoteRevocationWatermarks";
            """);
        await using var reader = await wmCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var minSeq = reader.GetInt64(1);
            if ((string.Equals(key, AccountScopeKey, StringComparison.Ordinal) && stamp.AccountRevocationSeq < minSeq) ||
                (string.Equals(key, DeviceScopeKey, StringComparison.Ordinal) && stamp.DeviceRevocationSeq < minSeq))
            {
                return true;
            }
        }

        return false;
    }

    public async Task RecordJobOriginAsync(RemoteJobOriginRecord origin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            INSERT INTO "RemoteJobOrigins" (
                "JobId", "ControlChannel", "PairingId", "BindingVersion", "AccountRevocationSeq", "DeviceRevocationSeq", "CreatedAtUtc"
            ) VALUES (
                $jid, $ch, $pid, $bv, $aseq, $dseq, $at
            )
            ON CONFLICT("JobId") DO UPDATE
                SET "ControlChannel" = excluded."ControlChannel",
                    "PairingId" = excluded."PairingId",
                    "BindingVersion" = excluded."BindingVersion",
                    "AccountRevocationSeq" = excluded."AccountRevocationSeq",
                    "DeviceRevocationSeq" = excluded."DeviceRevocationSeq";
            """);
        cmd.Parameters.AddWithValue("$jid", origin.JobId);
        cmd.Parameters.AddWithValue("$ch", origin.Channel.ToString());
        cmd.Parameters.AddWithValue("$pid", origin.Stamp is null ? DBNull.Value : origin.Stamp.PairingId.ToString("D"));
        cmd.Parameters.AddWithValue("$bv", origin.Stamp is null ? DBNull.Value : origin.Stamp.BindingVersion);
        cmd.Parameters.AddWithValue("$aseq", origin.Stamp is null ? DBNull.Value : origin.Stamp.AccountRevocationSeq);
        cmd.Parameters.AddWithValue("$dseq", origin.Stamp is null ? DBNull.Value : origin.Stamp.DeviceRevocationSeq);
        cmd.Parameters.AddWithValue("$at", origin.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<RemoteJobOriginRecord?> FindJobOriginAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = _connections.Open();
        await using var cmd = CreateCommand(conn, null, """
            SELECT "JobId", "ControlChannel", "PairingId", "BindingVersion", "AccountRevocationSeq", "DeviceRevocationSeq", "CreatedAtUtc"
              FROM "RemoteJobOrigins"
             WHERE "JobId" = $jid
             LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$jid", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var ch = Enum.TryParse<ControlChannel>(reader.GetString(1), ignoreCase: true, out var parsed)
            ? parsed
            : ControlChannel.Local;
        RemotePrincipalStamp? stamp = null;
        if (!reader.IsDBNull(2) && Guid.TryParse(reader.GetString(2), out var pid) &&
            !reader.IsDBNull(3) && !reader.IsDBNull(4) && !reader.IsDBNull(5))
        {
            stamp = new RemotePrincipalStamp(pid, reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        return new RemoteJobOriginRecord(
            reader.GetString(0),
            ch,
            stamp,
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
    }

    private static async Task UpsertWatermarkAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string scopeKey,
        long seq,
        string nowIso,
        CancellationToken ct)
    {
        await using var cmd = CreateCommand(conn, tx, """
            INSERT INTO "RemoteRevocationWatermarks" ("ScopeKey", "RevocationSeq", "UpdatedAtUtc")
            VALUES ($k, $seq, $at)
            ON CONFLICT("ScopeKey") DO UPDATE
                SET "RevocationSeq" = MAX("RemoteRevocationWatermarks"."RevocationSeq", excluded."RevocationSeq"),
                    "UpdatedAtUtc" = excluded."UpdatedAtUtc";
            """);
        cmd.Parameters.AddWithValue("$k", scopeKey);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$at", nowIso);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<RemoteAttachmentUploadRecord?> ReadAttachmentUploadAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string pairingId,
        string sessionId,
        string uploadId,
        CancellationToken ct)
    {
        await using var cmd = CreateCommand(conn, tx, """
            SELECT "PairingId", "SessionId", "UploadId", "FileSha256", "Status", "RelativePath", "PairingIdHash8",
                   "UploadedAttachmentDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
              FROM "RemoteAttachmentUploads"
             WHERE "PairingId" = $pid AND "SessionId" = $sid AND "UploadId" = $uid
             LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$pid", pairingId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$uid", uploadId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAttachmentUploadRow(reader) : null;
    }

    private static RemoteAttachmentUploadRecord ReadAttachmentUploadRow(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static async Task<RemoteChatOpRecord?> ReadChatOpAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string pairingId,
        string sessionId,
        string clientOpId,
        CancellationToken ct)
    {
        await using var cmd = CreateCommand(conn, tx, """
            SELECT "PairingId", "SessionId", "ClientOpId", "RequestDigest", "Status",
                   "ConversationId", "MessageId", "BindingVersion", "AccountRevocationSeq", "DeviceRevocationSeq",
                   "ResponseDtoJson", "CreatedAtUtc", "UpdatedAtUtc"
              FROM "RemoteChatOps"
             WHERE "PairingId" = $pid AND "SessionId" = $sid AND "ClientOpId" = $oid
             LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$pid", pairingId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$oid", clientOpId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new RemoteChatOpRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture));
    }

    private static SqliteCommand CreateCommand(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
