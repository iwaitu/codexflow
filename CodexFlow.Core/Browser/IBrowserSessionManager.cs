// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Browser;

public sealed record BrowserTrustedCallerContext(
    string SessionId,
    SessionWorkMode WorkMode,
    bool PlanModeActive,
    string? WorkspaceRoot = null);

public sealed record BrowserScreenshotData(
    long ScreenshotSeq,
    string MimeType,
    ReadOnlyMemory<byte> Bytes,
    DateTimeOffset CapturedAtUtc);

public sealed record BrowserOperationResult(
    bool Succeeded,
    string Output,
    string? Summary = null,
    string? Url = null,
    string? Title = null,
    BrowserScreenshotData? Screenshot = null,
    string? ErrorCode = null);

public sealed record BrowserActRequest(
    string Action,
    string? Ref = null,
    string? Text = null,
    string? Key = null,
    int? DeltaY = null,
    int? TimeoutMs = null);

/// <summary>
/// Manages isolated Chromium BrowserContexts per CodexFlow session over a single shared managed browser process.
/// </summary>
public interface IBrowserSessionManager : IAsyncDisposable
{
    Task<BrowserOperationResult> NavigateAsync(
        BrowserTrustedCallerContext caller,
        string action,
        string? url,
        CancellationToken ct = default);

    Task<BrowserOperationResult> SnapshotAsync(
        BrowserTrustedCallerContext caller,
        int maxChars = 12000,
        bool includeConsole = false,
        bool includeNetworkErrors = false,
        bool includeScreenshot = false,
        CancellationToken ct = default);

    Task<BrowserOperationResult> ActAsync(
        BrowserTrustedCallerContext caller,
        BrowserActRequest request,
        CancellationToken ct = default);

    Task<BrowserOperationResult> TabsAsync(
        BrowserTrustedCallerContext caller,
        string action,
        string? tabId = null,
        string? url = null,
        CancellationToken ct = default);

    BrowserSessionStateDto GetSessionState(string sessionId);

    string? GetSessionGenerationId(string sessionId) => null;

    bool TryGetLatestScreenshot(string sessionId, out BrowserScreenshotData? screenshot);

    Task CloseSessionAsync(string sessionId, string reason = "closed", CancellationToken ct = default);

    Task CleanupIdleSessionsAsync(CancellationToken ct = default);
}
