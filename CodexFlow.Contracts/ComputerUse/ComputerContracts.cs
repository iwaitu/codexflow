// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts.ComputerUse;

public sealed record ComputerDisplayInfoDto(
    string DisplayId,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int PixelWidth,
    int PixelHeight,
    double ScaleFactor);

public sealed record ComputerWindowInfoDto(
    string WindowHandle,
    int ProcessId,
    string ProcessName,
    string Title,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMinimized,
    bool IsForeground,
    bool IsProtected);

public sealed record ComputerDriverStatusDto(
    bool Available,
    string Platform,
    string DriverName,
    bool ScreenCaptureGranted,
    bool AccessibilityGranted,
    int DisplayCount,
    string? DiagnosticMessage = null,
    string? PermissionGuide = null,
    string EmergencyStopShortcut = "CommandOrControl+.");

public sealed record ComputerActionRecordDto(
    long Seq,
    string Tool,
    string Action,
    string? TargetProcessName,
    string? TargetWindowTitle,
    int? TargetX,
    int? TargetY,
    bool Succeeded,
    string? Summary,
    long DurationMs,
    DateTimeOffset TimestampUtc);

public sealed record ComputerSessionStateDto(
    string SessionId,
    string Status,
    bool Active,
    bool Enabled,
    string? PinnedWindowHandle,
    string? PinnedProcessName,
    string? PinnedWindowTitle,
    long ScreenshotSeq,
    int? ImageWidth,
    int? ImageHeight,
    string CoordinateMode,
    string? LastActionSummary,
    IReadOnlyList<ComputerActionRecordDto> RecentActions,
    bool TakeoverPending = false,
    string? TakeoverReason = null,
    DateTimeOffset? TakeoverStartedAtUtc = null,
    bool EmergencyStopped = false,
    bool HostWindowAvoidanceRequested = false,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record ComputerApprovalRequestDto(
    string ApprovalId,
    string RequestDigest,
    string SessionId,
    string Tool,
    string Action,
    string TargetProcessName,
    string TargetWindowTitle,
    string TargetWindowHandle,
    string ActionSummary,
    string RiskReason,
    int? TargetX,
    int? TargetY,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> AllowedDecisions,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection,
    string? PreviewImageBase64 = null);

public sealed record ComputerSettingsDto(
    bool Enabled,
    string InteractionApprovalPolicy,
    string CoordinateMode,
    int MaxImageLongEdge,
    int MaxScreenshotsPerTurn,
    bool EnableGridOverlay,
    bool AutoMinimizeHostWindow,
    IReadOnlyList<string> BlockedProcessNames,
    string EmergencyStopShortcut,
    int IdleTimeoutMinutes,
    ComputerDriverStatusDto DriverStatus);

public sealed record UpdateComputerSettingsRequest(
    bool? Enabled = null,
    string? InteractionApprovalPolicy = null,
    string? CoordinateMode = null,
    int? MaxImageLongEdge = null,
    int? MaxScreenshotsPerTurn = null,
    bool? EnableGridOverlay = null,
    bool? AutoMinimizeHostWindow = null,
    IReadOnlyList<string>? BlockedProcessNames = null,
    string? EmergencyStopShortcut = null,
    int? IdleTimeoutMinutes = null);

public sealed record ComputerTakeoverStartRequest(
    string? Reason = null);

public sealed record ComputerTakeoverCompleteRequest(
    string? Note = null);

public sealed record ComputerTakeoverResponse(
    bool Active,
    string SessionId,
    string? Message = null);

public sealed record ComputerSessionCloseResponse(
    bool Closed,
    string SessionId);

public sealed record ComputerEmergencyStopRequest(
    string? SessionId = null,
    string? Reason = null);

public sealed record ComputerEmergencyStopResponse(
    bool Stopped,
    int CancelledSessions,
    string Message);

public sealed record RegisterProtectedWindowsRequest(
    int ProcessId,
    IReadOnlyList<string>? WindowHandles = null);

public sealed record RegisterProtectedWindowsResponse(
    int RegisteredProcessCount,
    int RegisteredWindowCount);

public sealed record ComputerApiErrorDto(
    string Code,
    string Message);
