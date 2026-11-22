// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;

namespace CodexFlow.Core.Remote;

/// <summary>
/// Orthogonal channel distinguishing local desktop UI control from mobile remote relay traffic.
/// Never populated from LLM tool arguments.
/// </summary>
public enum ControlChannel
{
    Local = 0,
    Remote = 1
}

/// <summary>
/// Cryptographically authenticated remote pairing principal attached by the managed Electron bridge.
/// </summary>
public sealed record RemotePrincipalStamp(
    Guid PairingId,
    int BindingVersion,
    long AccountRevocationSeq,
    long DeviceRevocationSeq);

/// <summary>
/// Validated attachment staged through pairing-isolated RemoteAttachmentUploads before turn execution.
/// </summary>
public sealed record VerifiedRemoteAttachment(
    string UploadId,
    string RelativePath,
    string FileName,
    string ContentType,
    string FileSha256,
    long SizeBytes);

/// <summary>
/// Async-local ambient scope carrying authoritative remote channel and pairing principal identity.
/// </summary>
public sealed class RemoteExecutionScope
{
    public RemoteExecutionScope(
        ControlChannel channel,
        RemotePrincipalStamp? stamp = null,
        string? sessionId = null,
        string? jobId = null,
        CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker? approvalBroker = null)
    {
        Channel = channel;
        Stamp = stamp;
        SessionId = sessionId;
        JobId = jobId;
        ApprovalBroker = approvalBroker;
    }

    public ControlChannel Channel { get; }
    public RemotePrincipalStamp? Stamp { get; }
    public string? SessionId { get; }
    public string? JobId { get; }
    public CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker? ApprovalBroker { get; }
}

/// <summary>
/// Thread-safe AsyncLocal holder for <see cref="RemoteExecutionScope"/>.
/// </summary>
public static class RemoteExecutionAmbientContext
{
    private static readonly AsyncLocal<RemoteExecutionScope?> Slot = new();

    public static RemoteExecutionScope? Current => Slot.Value;

    public static bool IsRemote => Slot.Value is { Channel: ControlChannel.Remote };

    public static IDisposable BeginScope(
        ControlChannel channel,
        RemotePrincipalStamp? stamp = null,
        string? sessionId = null,
        string? jobId = null,
        CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker? approvalBroker = null)
    {
        var previous = Slot.Value;
        Slot.Value = new RemoteExecutionScope(channel, stamp, sessionId, jobId, approvalBroker ?? previous?.ApprovalBroker);
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(RemoteExecutionScope? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Slot.Value = previous;
            }
        }
    }
}

public static class RemoteBridgeHeaderNames
{
    public const string BridgeToken = "X-CF-Remote-Bridge-Token";
    public const string ControlChannelHeader = "X-CF-Control-Channel";
    public const string PairingId = "X-CF-Pairing-Id";
    public const string BindingVersion = "X-CF-Binding-Version";
    public const string AccountRevocationSeq = "X-CF-Account-Rev-Seq";
    public const string DeviceRevocationSeq = "X-CF-Device-Rev-Seq";
    public const string UploadId = "X-CF-Upload-Id";
    public const string FileSha256 = "X-CF-File-Sha256";
    public const string ClientOpId = "X-CF-Client-Op-Id";
    public const string VerifiedEnvelopeKind = "X-CF-Verified-Envelope-Kind";
    public const string VerifiedEnvelopeHash = "X-CF-Verified-Envelope-Hash";
}

/// <summary>
/// Authenticates internal requests forwarded by the managed Electron remote dispatcher.
/// Implemented only in Pipe credential delivery mode by <c>LocalHostCredentials</c>.
/// </summary>
public interface IRemoteBridgeAuthenticator
{
    bool IsBridgeAvailable { get; }
    bool ValidateBridgeToken(string? candidate);
}

/// <summary>
/// Verified remote HTTP request metadata bound into <see cref="HttpContext.Items"/> by the remote context middleware.
/// </summary>
public sealed record RemoteRequestMetadata(
    ControlChannel Channel,
    RemotePrincipalStamp Stamp,
    string? UploadId,
    string? FileSha256,
    string? ClientOpId,
    string? VerifiedEnvelopeKind,
    string? VerifiedEnvelopeHash);

public sealed record RemoteAttachmentUploadRecord(
    string PairingId,
    string SessionId,
    string UploadId,
    string FileSha256,
    string Status,
    string RelativePath,
    string PairingIdHash8,
    string? UploadedAttachmentDtoJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum RemoteUploadReservationOutcome
{
    AcquiredNew = 0,
    AlreadyCompletedSameHash = 1,
    PendingInProgress = 2,
    HashConflict = 3
}

public sealed record RemoteUploadReservationResult(
    RemoteUploadReservationOutcome Outcome,
    RemoteAttachmentUploadRecord? Record);

public interface IRemoteAttachmentUploadStore
{
    Task<RemoteUploadReservationResult> TryReserveUploadAsync(
        RemoteAttachmentUploadRecord record,
        bool leaseIsActive,
        CancellationToken ct = default);

    Task MarkUploadCompletedAsync(
        string pairingId,
        string sessionId,
        string uploadId,
        string fileSha256,
        string relativePath,
        string uploadedAttachmentDtoJson,
        CancellationToken ct = default);

    Task<bool> DeletePendingUploadAsync(
        string pairingId,
        string sessionId,
        string uploadId,
        CancellationToken ct = default);

    Task<RemoteAttachmentUploadRecord?> FindUploadAsync(
        string pairingId,
        string sessionId,
        string uploadId,
        CancellationToken ct = default);

    Task<RemoteAttachmentUploadRecord?> FindCompletedByRelativePathAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default);

    Task<IReadOnlyList<RemoteAttachmentUploadRecord>> ListPendingUploadsAsync(CancellationToken ct = default);
}

public sealed record RemoteChatOpRecord(
    string PairingId,
    string SessionId,
    string ClientOpId,
    string RequestDigest,
    string Status,
    string? ConversationId,
    string? MessageId,
    int BindingVersion,
    long AccountRevocationSeq,
    long DeviceRevocationSeq,
    string? ResponseDtoJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum RemoteChatOpReserveOutcome
{
    AcquiredNew = 0,
    DuplicateSameDigest = 1,
    DigestConflict = 2
}

public sealed record RemoteChatOpReserveResult(
    RemoteChatOpReserveOutcome Outcome,
    RemoteChatOpRecord? Existing);

public interface IRemoteChatOpStore
{
    Task<RemoteChatOpReserveResult> TryReserveAsync(RemoteChatOpRecord record, CancellationToken ct = default);

    Task UpdateStatusAsync(
        string pairingId,
        string sessionId,
        string clientOpId,
        string status,
        string? conversationId = null,
        string? messageId = null,
        string? responseDtoJson = null,
        CancellationToken ct = default);

    Task<RemoteChatOpRecord?> FindAsync(
        string pairingId,
        string sessionId,
        string clientOpId,
        CancellationToken ct = default);
}

public sealed record RemoteWorkspaceAuthorizationEntry(
    string WorkspaceRealPath,
    string? SessionId,
    DateTimeOffset AuthorizedAtUtc);

public interface IRemoteWorkspaceAuthorizationStore
{
    Task<bool> IsAuthorizedExternalWorkspaceAsync(string workspaceRealPath, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteWorkspaceAuthorizationEntry>> ListAuthorizedWorkspacesAsync(CancellationToken ct = default);
    Task SetExternalWorkspaceAuthorizedAsync(string workspaceRealPath, string? sessionId, bool authorized, CancellationToken ct = default);
}

public sealed record RemoteWorkspaceAuthDecision(
    bool Allowed,
    string ReasonCode,
    string Message,
    string? ResolvedPath = null);

public interface IRemoteWorkspaceAuthorizationGuard
{
    Task<RemoteWorkspaceAuthDecision> AuthorizeWorkspacePathAsync(
        string? workspacePath,
        CancellationToken ct = default);

    Task<RemoteWorkspaceAuthDecision> AuthorizeSessionAsync(
        string sessionId,
        string? workspacePath,
        string? projectLocation = null,
        CancellationToken ct = default);
}

public interface IRemoteRevocationStore
{
    Task ApplyRevocationAsync(
        long? minAccountRevocationSeq,
        long? minDeviceRevocationSeq,
        IReadOnlyList<Guid>? revokedPairingIds,
        CancellationToken ct = default);

    Task<bool> IsRevokedAsync(RemotePrincipalStamp? stamp, CancellationToken ct = default);
}

public sealed record RemoteJobOriginRecord(
    string JobId,
    ControlChannel Channel,
    RemotePrincipalStamp? Stamp,
    DateTimeOffset CreatedAtUtc);

public interface IRemoteJobOriginStore
{
    Task RecordJobOriginAsync(RemoteJobOriginRecord origin, CancellationToken ct = default);
    Task<RemoteJobOriginRecord?> FindJobOriginAsync(string jobId, CancellationToken ct = default);
}

public sealed record RemotePendingTodoItem(
    long EventId,
    string SourceKind,
    string OpaqueRefId,
    string SessionId,
    string Title,
    string Summary,
    string Status,
    bool IsTerminal,
    bool ReadOnlyInRemote,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ResolvedReason = null,
    string? ApprovalId = null,
    string? RequestDigest = null);

public sealed record RemotePendingEventEnvelope(
    long EventId,
    long SummaryEpoch,
    string SourceKind,
    string OpaqueRefId,
    string SessionId,
    string Status,
    bool IsTerminal,
    string Summary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ResolvedReason = null);

public sealed record RemotePendingSummarySnapshotResponse(
    string Mode,
    string? SnapshotId,
    long SummaryEpoch,
    long ResumeAfterEventId,
    string? NextPageToken,
    bool SnapshotComplete,
    bool ResetRequired,
    IReadOnlyList<RemotePendingTodoItem> Items);

public interface IRemotePendingProjection
{
    long SummaryEpoch { get; }

    Task<long> PublishAsync(
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
        CancellationToken ct = default);

    Task<RemotePendingSummarySnapshotResponse> QuerySummaryAsync(
        string mode,
        string? snapshotId,
        string? pageToken,
        long? afterEventId,
        int limit,
        Func<string, CancellationToken, Task<bool>> isSessionAuthorizedAsync,
        CancellationToken ct = default);

    Task<IReadOnlyList<RemotePendingEventEnvelope>> ReadHostEventsAfterAsync(
        long afterEventId,
        int limit,
        Func<string, CancellationToken, Task<bool>> isSessionAuthorizedAsync,
        CancellationToken ct = default);
}

public static class RemoteNamingHelper
{
    public static string ComputePairingIdHash8(Guid pairingId)
    {
        var raw = Encoding.UTF8.GetBytes(pairingId.ToString("D"));
        return Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()[..8];
    }

    public static string ComputePairingIdHash8(string pairingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        return Guid.TryParse(pairingId, out var parsed)
            ? ComputePairingIdHash8(parsed)
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pairingId.Trim()))).ToLowerInvariant()[..8];
    }
}
