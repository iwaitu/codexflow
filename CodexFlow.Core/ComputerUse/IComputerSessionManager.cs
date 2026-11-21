// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.ComputerUse;

namespace CodexFlow.Core.ComputerUse;

public sealed record ComputerScreenshotData(
    long ScreenshotSeq,
    string MimeType,
    ReadOnlyMemory<byte> Bytes,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc);

public sealed record ComputerOperationResult(
    bool Succeeded,
    string Output,
    string? Summary = null,
    ComputerScreenshotData? Screenshot = null,
    ComputerCaptureViewport? Viewport = null,
    IReadOnlyList<ComputerDisplayInfoDto>? Displays = null,
    IReadOnlyList<ComputerWindowInfoDto>? Windows = null,
    string? ErrorCode = null);

public sealed record ComputerActRequest(
    string Action,
    double? X = null,
    double? Y = null,
    double? EndX = null,
    double? EndY = null,
    string? Button = null,
    string? Text = null,
    string? Keys = null,
    int? DeltaX = null,
    int? DeltaY = null,
    int? DurationMs = null,
    string? CoordinateSpace = null);

/// <summary>
/// Manages desktop Computer Use sessions, exclusive desktop lease, window pinning,
/// coordinate mapping, visual overlays, self-UI protection, and emergency stop.
/// </summary>
public interface IComputerSessionManager : IAsyncDisposable
{
    ComputerDriverStatusDto GetDriverStatus();

    RegisterProtectedWindowsResponse RegisterProtectedWindows(
        int processId,
        IReadOnlyList<string>? windowHandles);

    Task<ComputerOperationResult> GetScreenInfoAsync(
        ComputerTrustedCallerContext caller,
        string? pinWindowHandle = null,
        string? pinProcessName = null,
        bool unpin = false,
        CancellationToken ct = default);

    Task<ComputerOperationResult> CaptureScreenshotAsync(
        ComputerTrustedCallerContext caller,
        bool fullDesktop = false,
        int? displayIndex = null,
        bool? includeGrid = null,
        CancellationToken ct = default);

    Task<ComputerOperationResult> ActAsync(
        ComputerTrustedCallerContext caller,
        ComputerActRequest request,
        CancellationToken ct = default);

    Task<ComputerOperationResult> RequestTakeoverAsync(
        ComputerTrustedCallerContext caller,
        string reason,
        int timeoutSeconds = 300,
        CancellationToken ct = default);

    Task<ComputerOperationResult> StartTakeoverAsync(
        string sessionId,
        string? reason = null,
        CancellationToken ct = default);

    Task<ComputerOperationResult> CompleteTakeoverAsync(
        string sessionId,
        string? note = null,
        CancellationToken ct = default);

    void CancelTakeover(string sessionId, string reason);

    void ResetEmergencyStopForNewTurn(string sessionId);

    Task<ComputerEmergencyStopResponse> EmergencyStopAsync(
        string? sessionId = null,
        string? reason = null,
        CancellationToken ct = default);

    ComputerSessionStateDto GetSessionState(string sessionId);

    string? GetSessionGenerationId(string sessionId) => null;

    bool TryGetLatestScreenshot(string sessionId, out ComputerScreenshotData? screenshot);

    Task CloseSessionAsync(string sessionId, string reason = "closed", CancellationToken ct = default);

    Task CleanupIdleSessionsAsync(CancellationToken ct = default);
}
