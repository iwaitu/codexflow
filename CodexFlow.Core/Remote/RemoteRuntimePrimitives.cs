// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Remote;

/// <summary>
/// Shared fail-closed gate forcing every direct workspace write tool (write_file, edit_file, delete_file,
/// create_directory, notebook_edit, apply_patch, hs_write, ivilson_smart_patch) through single-use
/// interactive Ask approval whenever the active control channel is Remote.
/// </summary>
public static class RemoteWorkspaceWriteGate
{
    private const int MaxPreviewChars = 2048;

    public static async Task<CodexToolResult?> AuthorizeWriteIfRemoteAsync(
        string toolName,
        string workspaceRoot,
        string relativePath,
        string previewContent,
        IExecutionApprovalBroker? explicitBroker,
        IDictionary<string, object?>? arguments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (!RemoteExecutionAmbientContext.IsRemote)
        {
            return null;
        }

        var scope = RemoteExecutionAmbientContext.Current;
        var broker = explicitBroker ?? scope?.ApprovalBroker;
        if (broker is null)
        {
            return CodexToolResult.Error(
                $"{toolName} denied: [ApprovalUnavailable] Remote workspace write requires an active execution approval broker.");
        }

        var sessionId = scope?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) && arguments is not null)
        {
            arguments.TryGetValue("session_id", out var sidObj);
            sessionId = sidObj?.ToString();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = "remote-session";
        }

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var scriptText = previewContent ?? string.Empty;
        var preview = scriptText.Length <= MaxPreviewChars
            ? scriptText
            : scriptText[..MaxPreviewChars];

        var req = new ExecutionRequest
        {
            SessionId = sessionId,
            JobId = scope?.JobId ?? string.Empty,
            TurnId = $"remote-write:{Guid.NewGuid():N}",
            WorkspaceRoot = fullRoot,
            ToolName = toolName,
            Executable = "workspace-write",
            InterpreterChain = ["workspace-write"],
            Argv = [relativePath.Replace('\\', '/')],
            Cwd = ".",
            StdinSha256 = null,
            CodeSha256 = null,
            ScriptSha256 = ExecutionRequest.Sha256Text(scriptText),
            EnvironmentDelta = [],
            ScriptText = scriptText,
            ScriptPreview = preview,
            ScriptLength = scriptText.Length,
            Backend = ExecutionBackend.Local,
            Network = ExecutionNetworkMode.None.ToString(),
            Filesystem = "workspace-write",
            TrustedOriginVerified = false,
            Origin = ExecutionOrigin.DailyChat,
            OwnerKind = ApprovalOwnerKind.GatewayTurn,
            OwnerId = sessionId,
            ControlChannel = ControlChannel.Remote
        };

        var authContext = new ExecutionAuthorizationContext(
            Request: req,
            WorkspaceRoot: fullRoot,
            StagingRoot: fullRoot,
            StagingWorkspaceDir: string.Empty,
            GuestGitDir: null);

        var result = await broker.AuthorizeAsync(authContext, ct).ConfigureAwait(false);
        if (!result.Decision.IsAllowed)
        {
            return ToolIsolationGate.Refuse(toolName, result.Decision);
        }

        return null;
    }
}

public static class RemotePathSecurityHelper
{
    public static bool TryNormalizeRealDirectory(string? rawPath, out string normalizedRealPath)
    {
        normalizedRealPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(rawPath.Trim());
            if (Directory.Exists(candidate))
            {
                var info = new DirectoryInfo(candidate);
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo;
                if (resolved is not null)
                {
                    candidate = Path.GetFullPath(resolved.FullName);
                }
            }

            normalizedRealPath = Path.TrimEndingDirectorySeparator(candidate);
            return !string.IsNullOrWhiteSpace(normalizedRealPath);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSubpathOrEqual(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        if (string.Equals(normCandidate, normRoot, comparison))
        {
            return true;
        }

        return normCandidate.StartsWith(normRoot + Path.DirectorySeparatorChar, comparison);
    }
}

public sealed class InMemoryRemoteAttachmentUploadStore : IRemoteAttachmentUploadStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PairingId, string SessionId, string UploadId), RemoteAttachmentUploadRecord> _records = new();

    public Task<RemoteUploadReservationResult> TryReserveUploadAsync(
        RemoteAttachmentUploadRecord record,
        bool leaseIsActive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = (record.PairingId, record.SessionId, record.UploadId);
            if (_records.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.FileSha256, record.FileSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new RemoteUploadReservationResult(RemoteUploadReservationOutcome.HashConflict, existing));
                }

                if (string.Equals(existing.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new RemoteUploadReservationResult(RemoteUploadReservationOutcome.AlreadyCompletedSameHash, existing));
                }

                var isFreshPending = leaseIsActive || existing.UpdatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5);
                if (isFreshPending)
                {
                    return Task.FromResult(new RemoteUploadReservationResult(RemoteUploadReservationOutcome.PendingInProgress, existing));
                }
            }

            _records[key] = record;
            return Task.FromResult(new RemoteUploadReservationResult(RemoteUploadReservationOutcome.AcquiredNew, record));
        }
    }

    public Task MarkUploadCompletedAsync(
        string pairingId,
        string sessionId,
        string uploadId,
        string fileSha256,
        string relativePath,
        string uploadedAttachmentDtoJson,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (pairingId, sessionId, uploadId);
            var now = DateTimeOffset.UtcNow;
            var createdAt = _records.TryGetValue(key, out var prev) ? prev.CreatedAtUtc : now;
            _records[key] = new RemoteAttachmentUploadRecord(
                pairingId,
                sessionId,
                uploadId,
                fileSha256,
                "completed",
                relativePath,
                RemoteNamingHelper.ComputePairingIdHash8(pairingId),
                uploadedAttachmentDtoJson,
                createdAt,
                now);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeletePendingUploadAsync(string pairingId, string sessionId, string uploadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (pairingId, sessionId, uploadId);
            if (_records.TryGetValue(key, out var existing) &&
                string.Equals(existing.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(_records.Remove(key));
            }

            return Task.FromResult(false);
        }
    }

    public Task<RemoteAttachmentUploadRecord?> FindUploadAsync(string pairingId, string sessionId, string uploadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _records.TryGetValue((pairingId, sessionId, uploadId), out var record);
            return Task.FromResult(record);
        }
    }

    public Task<RemoteAttachmentUploadRecord?> FindCompletedByRelativePathAsync(string sessionId, string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var match = _records.Values.FirstOrDefault(r =>
                string.Equals(r.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(match);
        }
    }

    public Task<IReadOnlyList<RemoteAttachmentUploadRecord>> ListPendingUploadsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<RemoteAttachmentUploadRecord> list = _records.Values
                .Where(static r => string.Equals(r.Status, "pending", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Task.FromResult(list);
        }
    }
}

public sealed class InMemoryRemoteChatOpStore : IRemoteChatOpStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PairingId, string SessionId, string ClientOpId), RemoteChatOpRecord> _records = new();

    public Task<RemoteChatOpReserveResult> TryReserveAsync(RemoteChatOpRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = (record.PairingId, record.SessionId, record.ClientOpId);
            if (_records.TryGetValue(key, out var existing))
            {
                var outcome = string.Equals(existing.RequestDigest, record.RequestDigest, StringComparison.OrdinalIgnoreCase)
                    ? RemoteChatOpReserveOutcome.DuplicateSameDigest
                    : RemoteChatOpReserveOutcome.DigestConflict;
                return Task.FromResult(new RemoteChatOpReserveResult(outcome, existing));
            }

            _records[key] = record;
            return Task.FromResult(new RemoteChatOpReserveResult(RemoteChatOpReserveOutcome.AcquiredNew, record));
        }
    }

    public Task UpdateStatusAsync(
        string pairingId,
        string sessionId,
        string clientOpId,
        string status,
        string? conversationId = null,
        string? messageId = null,
        string? responseDtoJson = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (pairingId, sessionId, clientOpId);
            if (_records.TryGetValue(key, out var existing))
            {
                _records[key] = existing with
                {
                    Status = status,
                    ConversationId = conversationId ?? existing.ConversationId,
                    MessageId = messageId ?? existing.MessageId,
                    ResponseDtoJson = responseDtoJson ?? existing.ResponseDtoJson,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task<RemoteChatOpRecord?> FindAsync(string pairingId, string sessionId, string clientOpId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _records.TryGetValue((pairingId, sessionId, clientOpId), out var record);
            return Task.FromResult(record);
        }
    }
}

public sealed class InMemoryRemoteRevocationStore : IRemoteRevocationStore
{
    private readonly object _gate = new();
    private long _minAccountRevocationSeq;
    private long _minDeviceRevocationSeq;
    private readonly HashSet<Guid> _revokedPairings = new();

    public Task ApplyRevocationAsync(
        long? minAccountRevocationSeq,
        long? minDeviceRevocationSeq,
        IReadOnlyList<Guid>? revokedPairingIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (minAccountRevocationSeq.HasValue && minAccountRevocationSeq.Value > _minAccountRevocationSeq)
            {
                _minAccountRevocationSeq = minAccountRevocationSeq.Value;
            }

            if (minDeviceRevocationSeq.HasValue && minDeviceRevocationSeq.Value > _minDeviceRevocationSeq)
            {
                _minDeviceRevocationSeq = minDeviceRevocationSeq.Value;
            }

            if (revokedPairingIds is not null)
            {
                foreach (var pairingId in revokedPairingIds)
                {
                    if (pairingId != Guid.Empty)
                    {
                        _revokedPairings.Add(pairingId);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsRevokedAsync(RemotePrincipalStamp? stamp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (stamp is null)
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            var revoked = _revokedPairings.Contains(stamp.PairingId) ||
                          stamp.AccountRevocationSeq < _minAccountRevocationSeq ||
                          stamp.DeviceRevocationSeq < _minDeviceRevocationSeq;
            return Task.FromResult(revoked);
        }
    }
}

public sealed class InMemoryRemoteWorkspaceAuthorizationStore : IRemoteWorkspaceAuthorizationStore
{
    private readonly ConcurrentDictionary<string, RemoteWorkspaceAuthorizationEntry> _entries =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public Task<bool> IsAuthorizedExternalWorkspaceAsync(string workspaceRealPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!RemotePathSecurityHelper.TryNormalizeRealDirectory(workspaceRealPath, out var normalized))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_entries.Keys.Any(root => RemotePathSecurityHelper.IsSubpathOrEqual(normalized, root)));
    }

    public Task<IReadOnlyList<RemoteWorkspaceAuthorizationEntry>> ListAuthorizedWorkspacesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<RemoteWorkspaceAuthorizationEntry> result = _entries.Values
            .OrderBy(static e => e.WorkspaceRealPath, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task SetExternalWorkspaceAuthorizedAsync(string workspaceRealPath, string? sessionId, bool authorized, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!RemotePathSecurityHelper.TryNormalizeRealDirectory(workspaceRealPath, out var normalized))
        {
            throw new ArgumentException("Invalid workspace directory path.", nameof(workspaceRealPath));
        }

        if (authorized)
        {
            _entries[normalized] = new RemoteWorkspaceAuthorizationEntry(normalized, sessionId, DateTimeOffset.UtcNow);
        }
        else
        {
            _entries.TryRemove(normalized, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class InMemoryRemoteJobOriginStore : IRemoteJobOriginStore
{
    private readonly ConcurrentDictionary<string, RemoteJobOriginRecord> _origins = new(StringComparer.Ordinal);

    public Task RecordJobOriginAsync(RemoteJobOriginRecord origin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ct.ThrowIfCancellationRequested();
        _origins[origin.JobId] = origin;
        return Task.CompletedTask;
    }

    public Task<RemoteJobOriginRecord?> FindJobOriginAsync(string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _origins.TryGetValue(jobId, out var origin);
        return Task.FromResult(origin);
    }
}

/// <summary>
/// Authoritative host-side pending TODO projection aggregating Execution Broker, Business Skill,
/// Browser, Computer, and Plan approvals with monotonic event IDs and 60s immutable frozen snapshots.
/// </summary>
public sealed class DefaultRemotePendingProjection : IRemotePendingProjection
{
    private const int MaxRingEvents = 4096;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly long _summaryEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _nextEventId;
    private readonly Dictionary<(string SourceKind, string OpaqueRefId), RemotePendingTodoItem> _activeItems = new();
    private readonly List<RemotePendingEventEnvelope> _eventRing = [];
    private readonly Dictionary<string, FrozenSnapshot> _snapshots = new(StringComparer.Ordinal);

    public long SummaryEpoch => _summaryEpoch;

    private sealed record FrozenSnapshot(
        string SnapshotId,
        long ResumeAfterEventId,
        DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<RemotePendingTodoItem> Items);

    public Task<long> PublishAsync(
        string sourceKind,
        string opaqueRefId,
        string sessionId,
        string title,
        string summary,
        string status,
        bool isTerminal,
        bool readOnlyInRemote,
        DateTimeOffset expiresAtUtc,
        string? resolvedReason = null,
        string? approvalId = null,
        string? requestDigest = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(opaqueRefId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var eventId = ++_nextEventId;
            var now = DateTimeOffset.UtcNow;
            var key = (sourceKind, opaqueRefId);

            if (isTerminal)
            {
                _activeItems.Remove(key);
            }
            else
            {
                _activeItems[key] = new RemotePendingTodoItem(
                    EventId: eventId,
                    SourceKind: sourceKind,
                    OpaqueRefId: opaqueRefId,
                    SessionId: sessionId,
                    Title: title,
                    Summary: summary,
                    Status: status,
                    IsTerminal: false,
                    ReadOnlyInRemote: readOnlyInRemote,
                    CreatedAtUtc: now,
                    ExpiresAtUtc: expiresAtUtc,
                    ResolvedReason: resolvedReason,
                    ApprovalId: approvalId,
                    RequestDigest: requestDigest);
            }

            _eventRing.Add(new RemotePendingEventEnvelope(
                EventId: eventId,
                SummaryEpoch: _summaryEpoch,
                SourceKind: sourceKind,
                OpaqueRefId: opaqueRefId,
                SessionId: sessionId,
                Status: status,
                IsTerminal: isTerminal,
                Summary: summary,
                CreatedAtUtc: now,
                ExpiresAtUtc: expiresAtUtc,
                ResolvedReason: resolvedReason));

            if (_eventRing.Count > MaxRingEvents)
            {
                _eventRing.RemoveRange(0, _eventRing.Count - MaxRingEvents);
            }

            return Task.FromResult(eventId);
        }
    }

    public async Task<RemotePendingSummarySnapshotResponse> QuerySummaryAsync(
        string mode,
        string? snapshotId,
        string? pageToken,
        long? afterEventId,
        int limit,
        Func<string, CancellationToken, Task<bool>> isSessionAuthorizedAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isSessionAuthorizedAsync);
        ct.ThrowIfCancellationRequested();

        var boundedLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "snapshot" : mode.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        if (normalizedMode == "delta")
        {
            List<RemotePendingEventEnvelope> candidates;
            var resetRequired = false;

            lock (_gate)
            {
                var requestedAfter = afterEventId ?? 0;
                if (requestedAfter > _nextEventId)
                {
                    resetRequired = true;
                    candidates = [];
                }
                else if (_eventRing.Count > 0 && requestedAfter > 0 && requestedAfter < _eventRing[0].EventId - 1)
                {
                    resetRequired = true;
                    candidates = [];
                }
                else
                {
                    candidates = _eventRing
                        .Where(e => e.EventId > requestedAfter)
                        .Take(boundedLimit)
                        .ToList();
                }
            }

            if (resetRequired)
            {
                return new RemotePendingSummarySnapshotResponse(
                    Mode: "delta",
                    SnapshotId: null,
                    SummaryEpoch: _summaryEpoch,
                    ResumeAfterEventId: _nextEventId,
                    NextPageToken: null,
                    SnapshotComplete: false,
                    ResetRequired: true,
                    Items: []);
            }

            var filtered = new List<RemotePendingTodoItem>();
            long latestReturned = afterEventId ?? 0;
            foreach (var env in candidates)
            {
                latestReturned = Math.Max(latestReturned, env.EventId);
                if (await isSessionAuthorizedAsync(env.SessionId, ct).ConfigureAwait(false))
                {
                    filtered.Add(new RemotePendingTodoItem(
                        EventId: env.EventId,
                        SourceKind: env.SourceKind,
                        OpaqueRefId: env.OpaqueRefId,
                        SessionId: env.SessionId,
                        Title: env.SourceKind,
                        Summary: env.Summary,
                        Status: env.Status,
                        IsTerminal: env.IsTerminal,
                        ReadOnlyInRemote: false,
                        CreatedAtUtc: env.CreatedAtUtc,
                        ExpiresAtUtc: env.ExpiresAtUtc,
                        ResolvedReason: env.ResolvedReason));
                }
            }

            return new RemotePendingSummarySnapshotResponse(
                Mode: "delta",
                SnapshotId: null,
                SummaryEpoch: _summaryEpoch,
                ResumeAfterEventId: latestReturned,
                NextPageToken: candidates.Count == boundedLimit ? latestReturned.ToString(CultureInfo.InvariantCulture) : null,
                SnapshotComplete: true,
                ResetRequired: false,
                Items: filtered);
        }

        // Snapshot mode (first page or subsequent pageToken)
        if (!string.IsNullOrWhiteSpace(snapshotId) || !string.IsNullOrWhiteSpace(pageToken))
        {
            FrozenSnapshot? existingSnapshot = null;
            var offset = 0;
            if (!int.TryParse(pageToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                offset = -1;
            }

            lock (_gate)
            {
                PruneExpiredSnapshotsLocked(now);
                if (!string.IsNullOrWhiteSpace(snapshotId) &&
                    offset >= 0 &&
                    _snapshots.TryGetValue(snapshotId, out var found) &&
                    found.ExpiresAtUtc > now)
                {
                    existingSnapshot = found;
                }
            }

            if (existingSnapshot is null)
            {
                return new RemotePendingSummarySnapshotResponse(
                    Mode: "snapshot",
                    SnapshotId: null,
                    SummaryEpoch: _summaryEpoch,
                    ResumeAfterEventId: _nextEventId,
                    NextPageToken: null,
                    SnapshotComplete: false,
                    ResetRequired: true,
                    Items: []);
            }

            var slice = existingSnapshot.Items.Skip(offset).Take(boundedLimit).ToArray();
            var nextOffset = offset + slice.Length;
            var complete = nextOffset >= existingSnapshot.Items.Count;
            return new RemotePendingSummarySnapshotResponse(
                Mode: "snapshot",
                SnapshotId: existingSnapshot.SnapshotId,
                SummaryEpoch: _summaryEpoch,
                ResumeAfterEventId: existingSnapshot.ResumeAfterEventId,
                NextPageToken: complete ? null : nextOffset.ToString(CultureInfo.InvariantCulture),
                SnapshotComplete: complete,
                ResetRequired: false,
                Items: slice);
        }

        List<RemotePendingTodoItem> rawSnapshot;
        long resumeEventId;
        var newSnapshotId = $"snap-{Guid.NewGuid():N}";
        lock (_gate)
        {
            PruneExpiredSnapshotsLocked(now);
            resumeEventId = _nextEventId;
            rawSnapshot = _activeItems.Values
                .Where(item => item.ExpiresAtUtc > now)
                .OrderBy(static item => item.EventId)
                .ToList();
        }

        var authorizedItems = new List<RemotePendingTodoItem>();
        foreach (var item in rawSnapshot)
        {
            if (await isSessionAuthorizedAsync(item.SessionId, ct).ConfigureAwait(false))
            {
                authorizedItems.Add(item);
            }
        }

        lock (_gate)
        {
            _snapshots[newSnapshotId] = new FrozenSnapshot(
                newSnapshotId,
                resumeEventId,
                now.Add(SnapshotTtl),
                authorizedItems);
        }

        var firstPage = authorizedItems.Take(boundedLimit).ToArray();
        var firstComplete = firstPage.Length >= authorizedItems.Count;
        return new RemotePendingSummarySnapshotResponse(
            Mode: "snapshot",
            SnapshotId: newSnapshotId,
            SummaryEpoch: _summaryEpoch,
            ResumeAfterEventId: resumeEventId,
            NextPageToken: firstComplete ? null : firstPage.Length.ToString(CultureInfo.InvariantCulture),
            SnapshotComplete: firstComplete,
            ResetRequired: false,
            Items: firstPage);
    }

    public async Task<IReadOnlyList<RemotePendingEventEnvelope>> ReadHostEventsAfterAsync(
        long afterEventId,
        int limit,
        Func<string, CancellationToken, Task<bool>> isSessionAuthorizedAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isSessionAuthorizedAsync);
        ct.ThrowIfCancellationRequested();

        var boundedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        List<RemotePendingEventEnvelope> raw;
        lock (_gate)
        {
            raw = _eventRing
                .Where(e => e.EventId > afterEventId)
                .Take(boundedLimit)
                .ToList();
        }

        var result = new List<RemotePendingEventEnvelope>(raw.Count);
        foreach (var env in raw)
        {
            if (await isSessionAuthorizedAsync(env.SessionId, ct).ConfigureAwait(false))
            {
                result.Add(env);
            }
        }

        return result;
    }

    private void PruneExpiredSnapshotsLocked(DateTimeOffset now)
    {
        var expired = _snapshots
            .Where(kv => kv.Value.ExpiresAtUtc <= now)
            .Select(static kv => kv.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _snapshots.Remove(key);
        }
    }
}
