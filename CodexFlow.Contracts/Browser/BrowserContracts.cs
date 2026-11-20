// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Contracts.Browser;

public sealed record BrowserExecutableStatusDto(
    bool Available,
    string? BrowserName,
    string? ExecutablePath,
    string? Version,
    string? Source,
    string? DiagnosticMessage = null);

public sealed record BrowserSettingsDto(
    bool Enabled,
    string? ExecutablePath,
    bool Headless,
    string ProfileMode,
    string InteractionApprovalPolicy,
    bool AllowLocalhostInDailyMode,
    IReadOnlyList<int> DevelopmentAllowedLoopbackPorts,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> BlockedDomains,
    int IdleTimeoutMinutes,
    int MaxConcurrentSessions,
    BrowserExecutableStatusDto DetectedBrowser,
    bool AllowControlledDownloads = false);

public sealed record UpdateBrowserSettingsRequest(
    bool? Enabled = null,
    string? ExecutablePath = null,
    bool? Headless = null,
    string? ProfileMode = null,
    string? InteractionApprovalPolicy = null,
    bool? AllowLocalhostInDailyMode = null,
    IReadOnlyList<int>? DevelopmentAllowedLoopbackPorts = null,
    IReadOnlyList<string>? AllowedDomains = null,
    IReadOnlyList<string>? BlockedDomains = null,
    int? IdleTimeoutMinutes = null,
    int? MaxConcurrentSessions = null,
    bool? AllowControlledDownloads = null);

public sealed record BrowserTabDto(
    string TabId,
    string Url,
    string Title,
    bool Active);

public sealed record BrowserActionRecordDto(
    long Seq,
    string Tool,
    string Action,
    string? Target,
    string? Url,
    bool Succeeded,
    string? Summary,
    long DurationMs,
    DateTimeOffset TimestampUtc);

public sealed record BrowserConsoleEntryDto(
    string Level,
    string Text,
    string? SourceUrl,
    int? LineNumber,
    DateTimeOffset TimestampUtc);

public sealed record BrowserSessionStateDto(
    string SessionId,
    string Status,
    bool Headless,
    string ProfileMode,
    string? Url,
    string? Title,
    long ScreenshotSeq,
    int ConsoleErrorCount,
    IReadOnlyList<BrowserTabDto> Tabs,
    IReadOnlyList<BrowserActionRecordDto> RecentActions,
    IReadOnlyList<BrowserConsoleEntryDto> RecentConsole,
    DateTimeOffset? UpdatedAtUtc = null,
    bool TakeoverActive = false,
    string? TakeoverReason = null,
    DateTimeOffset? TakeoverStartedAtUtc = null);

public sealed record BrowserTakeoverStartRequest(
    string? Reason = null);

public sealed record BrowserTakeoverCompleteRequest(
    string? Note = null);

public sealed record BrowserTakeoverResponse(
    bool Active,
    string SessionId,
    string? Message = null);

public sealed record BrowserApprovalRequestDto(
    string ApprovalId,
    string RequestDigest,
    string SessionId,
    string Tool,
    string Action,
    string TargetUrl,
    string TargetOrigin,
    string TargetDescription,
    string RiskReason,
    string? ExpressionOrPath,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> AllowedDecisions,
    bool ControlPlaneAvailable,
    bool SelfApprovalProtection);
