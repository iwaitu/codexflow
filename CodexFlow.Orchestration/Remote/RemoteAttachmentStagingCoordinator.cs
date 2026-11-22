// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Application;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Remote;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Orchestration.Persistence;
using CodexFlow.Orchestration.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.Remote;

public enum RemoteAttachmentUploadOutcome
{
    Success = 0,
    BadRequest = 1,
    Forbidden = 2,
    Conflict = 3
}

public sealed record RemoteAttachmentUploadOutcomeResult(
    RemoteAttachmentUploadOutcome Outcome,
    string? ErrorCode,
    string? ErrorMessage,
    UploadedAttachmentResult? Attachment);

public sealed record RemoteAttachmentQueryResult(
    string Status,
    string? UploadId,
    string? FileSha256,
    UploadedAttachmentResult? Attachment);

/// <summary>
/// Manages pairing-isolated remote attachment pre-allocation (<c>RemoteAttachmentUploads</c>),
/// active in-flight upload leases, exclusive temp file staging with fsync &amp; SHA-256 verification,
/// non-overwriting publication into <c>.codexflow-attachments</c>, and crash recovery cleanup.
/// </summary>
public sealed partial class RemoteAttachmentStagingCoordinator(
    IRemoteAttachmentUploadStore uploadStore,
    IRemoteWorkspaceAuthorizationGuard workspaceGuard,
    IServiceScopeFactory? scopeFactory = null,
    ILogger<RemoteAttachmentStagingCoordinator>? logger = null)
{
    private readonly IRemoteAttachmentUploadStore _uploadStore = uploadStore
        ?? throw new ArgumentNullException(nameof(uploadStore));
    private readonly IRemoteWorkspaceAuthorizationGuard _workspaceGuard = workspaceGuard
        ?? throw new ArgumentNullException(nameof(workspaceGuard));
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;
    private readonly ILogger<RemoteAttachmentStagingCoordinator>? _logger = logger;

    private readonly ConcurrentDictionary<(string PairingId, string SessionId, string UploadId), byte> _activeUploadLeases = new();

    public bool HasActiveLease(string pairingId, string sessionId, string uploadId)
        => _activeUploadLeases.ContainsKey((pairingId, sessionId, uploadId));

    public async Task<RemoteAttachmentUploadOutcomeResult> UploadRemoteAttachmentAsync(
        Guid pairingId,
        string sessionId,
        string? workspacePath,
        string? uploadId,
        string? declaredSha256,
        string? rawFileName,
        string? contentType,
        Stream contentStream,
        long? declaredLength,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contentStream);

        if (pairingId == Guid.Empty)
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Forbidden,
                "remote_pairing_invalid",
                "缺少有效的远程配对标识。",
                null);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                "invalid_session",
                "缺少会话标识。",
                null);
        }

        if (!IsValidUploadId(uploadId))
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                "invalid_upload_id",
                "远程附件上传缺少或携带非法 X-CF-Upload-Id（仅允许 1-128 位字母、数字、下划线或短横线）。",
                null);
        }

        if (!TryNormalizeSha256(declaredSha256, out var normalizedSha256))
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                "invalid_file_sha256",
                "远程附件上传缺少或携带非法 X-CF-File-Sha256（须为 64 位十六进制摘要）。",
                null);
        }

        if (declaredLength.HasValue && declaredLength.Value <= 0)
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                "empty_attachment",
                "不能上传空文件。",
                null);
        }

        if (declaredLength.HasValue && declaredLength.Value > GatewayAttachmentService.MaxAttachmentBytes)
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                "attachment_too_large",
                $"单个附件不能超过 {GatewayAttachmentService.MaxAttachmentBytes / (1024 * 1024)} MB。",
                null);
        }

        var authDecision = await _workspaceGuard.AuthorizeSessionAsync(sessionId, workspacePath, projectLocation: null, ct)
            .ConfigureAwait(false);
        if (!authDecision.Allowed || string.IsNullOrWhiteSpace(authDecision.ResolvedPath))
        {
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Forbidden,
                authDecision.ReasonCode,
                authDecision.Message,
                null);
        }

        var pairingIdStr = pairingId.ToString("D");
        var pairingIdHash8 = RemoteNamingHelper.ComputePairingIdHash8(pairingId);
        var trimmedUploadId = uploadId!.Trim();
        var leaseKey = (pairingIdStr, sessionId, trimmedUploadId);

        if (!_activeUploadLeases.TryAdd(leaseKey, 0))
        {
            var current = await _uploadStore.FindUploadAsync(pairingIdStr, sessionId, trimmedUploadId, ct).ConfigureAwait(false);
            if (current is not null && !string.Equals(current.FileSha256, normalizedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteAttachmentUploadOutcomeResult(
                    RemoteAttachmentUploadOutcome.Conflict,
                    "upload_hash_conflict",
                    "同一 uploadId 已绑定不同的文件 SHA-256 摘要。",
                    null);
            }

            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Conflict,
                "upload_in_progress",
                "该 uploadId 正在上传写入中，请稍后重试或查询上传状态。",
                null);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var attachDir = Path.Combine(authDecision.ResolvedPath, GatewayAttachmentService.AttachmentDirectoryName);
            var tmpRelativePath = $"{GatewayAttachmentService.AttachmentDirectoryName}/.tmp_{pairingIdHash8}_{trimmedUploadId}";
            var pendingRecord = new RemoteAttachmentUploadRecord(
                PairingId: pairingIdStr,
                SessionId: sessionId,
                UploadId: trimmedUploadId,
                FileSha256: normalizedSha256,
                Status: "pending",
                RelativePath: tmpRelativePath,
                PairingIdHash8: pairingIdHash8,
                UploadedAttachmentDtoJson: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now);

            var reservation = await _uploadStore.TryReserveUploadAsync(pendingRecord, leaseIsActive: false, ct).ConfigureAwait(false);
            switch (reservation.Outcome)
            {
                case RemoteUploadReservationOutcome.HashConflict:
                    return new RemoteAttachmentUploadOutcomeResult(
                        RemoteAttachmentUploadOutcome.Conflict,
                        "upload_hash_conflict",
                        "同一 uploadId 已用于不同的文件摘要，禁止覆盖。",
                        null);

                case RemoteUploadReservationOutcome.PendingInProgress:
                    return new RemoteAttachmentUploadOutcomeResult(
                        RemoteAttachmentUploadOutcome.Conflict,
                        "upload_in_progress",
                        "该 uploadId 存在未超时的进行中占位，请稍后查询状态。",
                        null);

                case RemoteUploadReservationOutcome.AlreadyCompletedSameHash:
                    var cached = TryDeserializeUploadedResult(reservation.Record?.UploadedAttachmentDtoJson);
                    if (cached is not null)
                    {
                        return new RemoteAttachmentUploadOutcomeResult(
                            RemoteAttachmentUploadOutcome.Success,
                            null,
                            null,
                            cached);
                    }

                    return new RemoteAttachmentUploadOutcomeResult(
                        RemoteAttachmentUploadOutcome.Conflict,
                        "upload_already_completed_corrupted",
                        "该 uploadId 已完成但结果元数据损坏，请使用新的 uploadId 重传。",
                        null);

                case RemoteUploadReservationOutcome.AcquiredNew:
                    break;
            }

            return await StageVerifyAndPublishAsync(
                authDecision.ResolvedPath,
                attachDir,
                pairingIdStr,
                pairingIdHash8,
                sessionId,
                trimmedUploadId,
                normalizedSha256,
                rawFileName,
                contentType,
                contentStream,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _activeUploadLeases.TryRemove(leaseKey, out _);
        }
    }

    public async Task<RemoteAttachmentQueryResult> QueryUploadStatusAsync(
        Guid pairingId,
        string sessionId,
        string? uploadId,
        CancellationToken ct = default)
    {
        if (pairingId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId) || !IsValidUploadId(uploadId))
        {
            return new RemoteAttachmentQueryResult("not_found", uploadId, null, null);
        }

        var pairingIdStr = pairingId.ToString("D");
        var trimmedUploadId = uploadId!.Trim();
        var record = await _uploadStore.FindUploadAsync(pairingIdStr, sessionId, trimmedUploadId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return new RemoteAttachmentQueryResult("not_found", trimmedUploadId, null, null);
        }

        if (string.Equals(record.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var attachment = TryDeserializeUploadedResult(record.UploadedAttachmentDtoJson);
            return new RemoteAttachmentQueryResult("completed", record.UploadId, record.FileSha256, attachment);
        }

        return new RemoteAttachmentQueryResult("pending", record.UploadId, record.FileSha256, null);
    }

    public async Task CleanupStalePendingUploadsAsync(CancellationToken ct = default)
    {
        var pending = await _uploadStore.ListPendingUploadsAsync(ct).ConfigureAwait(false);
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (HasActiveLease(item.PairingId, item.SessionId, item.UploadId))
            {
                continue;
            }

            try
            {
                var workspacePath = await TryResolveSessionWorkspacePathAsync(item.SessionId, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(workspacePath) &&
                    RemotePathSecurityHelper.TryNormalizeRealDirectory(workspacePath, out var realWorkspace))
                {
                    var tmpFileName = $".tmp_{item.PairingIdHash8}_{item.UploadId}";
                    var tmpFullPath = Path.Combine(realWorkspace, GatewayAttachmentService.AttachmentDirectoryName, tmpFileName);
                    TryDeleteFileQuietly(tmpFullPath);
                }

                await _uploadStore.DeletePendingUploadAsync(item.PairingId, item.SessionId, item.UploadId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed cleaning stale remote pending upload {UploadId} in session {SessionId}", item.UploadId, item.SessionId);
            }
        }
    }

    private async Task<RemoteAttachmentUploadOutcomeResult> StageVerifyAndPublishAsync(
        string realWorkspaceRoot,
        string attachDir,
        string pairingIdStr,
        string pairingIdHash8,
        string sessionId,
        string uploadId,
        string expectedSha256,
        string? rawFileName,
        string? contentType,
        Stream contentStream,
        CancellationToken ct)
    {
        var dirEnsure = WorkspaceFileWriter.EnsureDirectory(realWorkspaceRoot, attachDir);
        if (!dirEnsure.Success)
        {
            await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Forbidden,
                "attachment_storage_denied",
                dirEnsure.Message,
                null);
        }

        if (!WorkspaceFileExplorerService.TryResolveWorkspaceRelativePath(
                realWorkspaceRoot,
                GatewayAttachmentService.AttachmentDirectoryName,
                allowEmpty: false,
                out _,
                out _,
                out _,
                out _,
                out var pathError))
        {
            await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Forbidden,
                "attachment_storage_denied",
                pathError ?? "附件目录路径校验失败。",
                null);
        }

        var tmpFileName = $".tmp_{pairingIdHash8}_{uploadId}";
        var tmpFullPath = Path.Combine(attachDir, tmpFileName);
        TryDeleteFileQuietly(tmpFullPath);

        long totalBytes = 0;
        string actualSha256;

        try
        {
            using (var fs = new FileStream(
                       tmpFullPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       options: FileOptions.Asynchronous))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalBytes += read;
                    if (totalBytes > GatewayAttachmentService.MaxAttachmentBytes)
                    {
                        throw new GatewayAttachmentValidationException(
                            "attachment_too_large",
                            $"单个附件不能超过 {GatewayAttachmentService.MaxAttachmentBytes / (1024 * 1024)} MB。");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                if (totalBytes <= 0)
                {
                    throw new GatewayAttachmentValidationException("empty_attachment", "不能上传空文件。");
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
                actualSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFileQuietly(tmpFullPath);
                await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
                return new RemoteAttachmentUploadOutcomeResult(
                    RemoteAttachmentUploadOutcome.BadRequest,
                    "sha256_mismatch",
                    "上传文件内容与声明的 X-CF-File-Sha256 摘要不匹配。",
                    null);
            }

            var (displayName, safeFileName) = GatewayAttachmentService.SanitizeFileName(rawFileName);
            var (finalRelativePath, _) = MoveToNonOverwritingTarget(attachDir, tmpFullPath, safeFileName, uploadId);
            var normalizedMime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
            var result = new UploadedAttachmentResult(displayName, finalRelativePath, totalBytes, normalizedMime);
            var dtoJson = JsonSerializer.Serialize(result, OrchestrationSnapshotJsonContext.CamelCase.UploadedAttachmentResult);

            await _uploadStore.MarkUploadCompletedAsync(
                pairingIdStr,
                sessionId,
                uploadId,
                actualSha256,
                finalRelativePath,
                dtoJson,
                CancellationToken.None).ConfigureAwait(false);

            return new RemoteAttachmentUploadOutcomeResult(RemoteAttachmentUploadOutcome.Success, null, null, result);
        }
        catch (GatewayAttachmentValidationException ex)
        {
            TryDeleteFileQuietly(tmpFullPath);
            await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.BadRequest,
                ex.ErrorCode,
                ex.Message,
                null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFileQuietly(tmpFullPath);
            await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Remote attachment staging failed for upload {UploadId} in session {SessionId}", uploadId, sessionId);
            TryDeleteFileQuietly(tmpFullPath);
            await _uploadStore.DeletePendingUploadAsync(pairingIdStr, sessionId, uploadId, CancellationToken.None).ConfigureAwait(false);
            return new RemoteAttachmentUploadOutcomeResult(
                RemoteAttachmentUploadOutcome.Conflict,
                "attachment_staging_failed",
                "附件落盘或发布失败，请重新上传。",
                null);
        }
    }

    private static (string RelativePath, string FullPath) MoveToNonOverwritingTarget(
        string attachDir,
        string tmpFullPath,
        string safeFileName,
        string uploadId)
    {
        var prefix = $"{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{uploadId[..Math.Min(6, uploadId.Length)]}";
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var ext = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "attachment";
        }

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"_{attempt}";
            var candidateFileName = $"{prefix}-{baseName}{suffix}{ext}";
            var candidateFullPath = Path.Combine(attachDir, candidateFileName);
            if (File.Exists(candidateFullPath))
            {
                continue;
            }

            try
            {
                File.Move(tmpFullPath, candidateFullPath, overwrite: false);
                return ($"{GatewayAttachmentService.AttachmentDirectoryName}/{candidateFileName}", candidateFullPath);
            }
            catch (IOException) when (attempt < 63)
            {
                // Race with concurrent upload choosing identical timestamp name; advance suffix.
            }
        }

        var fallbackFileName = $"{prefix}-{Guid.NewGuid():N}-{safeFileName}";
        var fallbackFullPath = Path.Combine(attachDir, fallbackFileName);
        File.Move(tmpFullPath, fallbackFullPath, overwrite: false);
        return ($"{GatewayAttachmentService.AttachmentDirectoryName}/{fallbackFileName}", fallbackFullPath);
    }

    private async Task<string?> TryResolveSessionWorkspacePathAsync(string sessionId, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var chatStore = scope.ServiceProvider.GetService<IChatService>() as IGatewayChatStore
            ?? scope.ServiceProvider.GetService<IGatewayChatStore>();
        if (chatStore is not null)
        {
            var session = await chatStore.GetSession(sessionId).ConfigureAwait(false);
            if (session is not null && !string.IsNullOrWhiteSpace(session.WorkspacePath))
            {
                return session.WorkspacePath;
            }
        }

        var sessionDir = scope.ServiceProvider.GetService<ISessionDirectory>();
        if (sessionDir is not null)
        {
            var rec = await sessionDir.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (rec is not null)
            {
                return rec.WorkspacePath;
            }
        }

        return null;
    }

    private static UploadedAttachmentResult? TryDeserializeUploadedResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, OrchestrationSnapshotJsonContext.CamelCase.UploadedAttachmentResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for staging temp files.
        }
    }

    public static bool IsValidUploadId(string? uploadId)
        => !string.IsNullOrWhiteSpace(uploadId) && UploadIdRegex().IsMatch(uploadId.Trim());

    public static bool TryNormalizeSha256(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (!Sha256HexRegex().IsMatch(trimmed))
        {
            return false;
        }

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadIdRegex();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexRegex();
}

/// <summary>
/// Startup recovery service that deletes orphaned remote upload staging temp files whose active
/// leases vanished across a crash and cleans their pending rows in <c>RemoteAttachmentUploads</c>.
/// </summary>
public sealed class RemoteAttachmentRecoveryHostedService(
    RemoteAttachmentStagingCoordinator coordinator,
    ILogger<RemoteAttachmentRecoveryHostedService>? logger = null) : IHostedService
{
    private readonly RemoteAttachmentStagingCoordinator _coordinator = coordinator
        ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly ILogger<RemoteAttachmentRecoveryHostedService>? _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.CleanupStalePendingUploadsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Remote attachment stale staging cleanup encountered an error during startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
