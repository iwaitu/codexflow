// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Serialization;

namespace CodexFlow.Contracts.BusinessSkills;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BusinessPluginSourceType
{
    OpenApi = 0,
    Mcp = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BusinessAuthKind
{
    None = 0,
    ApiKey = 1,
    Bearer = 2,
    BasicTlsOnly = 3,
    OAuth2AuthCodePkce = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityRiskLevel
{
    Unknown = 0,
    Read = 1,
    Write = 2,
    Destructive = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityExecutabilityStatus
{
    Executable = 0,
    NeedsConfiguration = 1,
    Unsupported = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillRunStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    PartialSucceeded = 6,
    OutcomeUnknown = 7
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillRunStepState
{
    Prepared = 0,
    Dispatching = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    OutcomeUnknown = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginOperationStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>
/// P0 冻结的默认产品保护阈值与协议兼容矩阵基线。
/// 对应设计文档第 3 节与第 12 节。
/// </summary>
public static class BusinessSkillDefaults
{
    public const int MaxDescriptionBytes = 10 * 1024 * 1024;
    public const int MaxParseDepth = 64;
    public const int MaxCatalogCapabilities = 10_000;
    public const int MaxExternalRefFiles = 20;
    public const int MaxSingleResponseBytes = 10 * 1024 * 1024;
    public const int MaxSkillSteps = 30;
    public const int MaxUsedCapabilities = 20;
    public const int MaxExternalRequestsPerRun = 100;
    public const int MaxReadRecordsPerRun = 10_000;
    public const int MaxJoinOutputRows = 10_000;
    public const int MaxWriteBatchItems = 100;
    public const int ConnectTimeoutSeconds = 10;
    public const int RequestTimeoutSeconds = 60;
    public const int ActiveExecutionTimeoutMinutes = 10;
    public const int ApprovalValidityMinutes = 10;
    public const int WallClockLimitMinutes = 30;
    public const int MaxReadOnlyRetries = 2;
    public const int MaxModelInputTokens = 64_000;
    public const int MaxModelOutputTokens = 8_000;
    public const int CatalogFreshnessMinutes = 15;
    public const int RunRetentionDays = 30;
    public const int DiagnosticRetentionDays = 7;

    public const string LockedMcpProtocolVersion = "2025-11-25";
    public const string DeferredMcpProtocolVersion = "2026-07-28";

    public const string OpenApiSdkPackageVersion = "Microsoft.OpenApi 2.13.0";
    public const string OpenApiYamlReaderPackageVersion = "Microsoft.OpenApi.YamlReader 2.13.0";
    public const string McpSdkPackageVersion = "ModelContextProtocol.Core 2.4.0";

    public static ProtocolSupportMatrixDto CreateFrozenMatrix() => new()
    {
        LockedPackages =
        [
            OpenApiSdkPackageVersion,
            OpenApiYamlReaderPackageVersion,
            McpSdkPackageVersion
        ],
        SupportedOpenApiSpecFamilies = ["2.0", "3.0", "3.1"],
        UnsupportedOpenApiSpecFamilies = ["3.2"],
        SupportedOpenApiEncodings = ["json", "yaml"],
        LockedMcpProtocolVersion = LockedMcpProtocolVersion,
        SupportedMcpTransports = ["StreamableHttp"],
        UnsupportedMcpProtocolVersions = [DeferredMcpProtocolVersion],
        UnsupportedMcpTransports = ["LegacyHttpSse", "Stdio"],
        UnsupportedMcpClientCapabilities = ["sampling", "roots", "elicitation"],
        SupportedAuthKinds =
        [
            BusinessAuthKind.None,
            BusinessAuthKind.ApiKey,
            BusinessAuthKind.Bearer,
            BusinessAuthKind.BasicTlsOnly,
            BusinessAuthKind.OAuth2AuthCodePkce
        ],
        UnsupportedAuthSchemes = ["SAML", "NTLM", "Kerberos", "ArbitraryLoginScript"],
        UnsupportedHttpCapabilities = ["multipart", "binary_stream", "callback", "webhook", "async_long_task"],
        AllowedStepTypes = ["tool", "transform", "branch", "foreach", "generateText", "approval", "output"]
    };
}

public sealed record ProtocolSupportMatrixDto
{
    public IReadOnlyList<string> LockedPackages { get; init; } = [];
    public IReadOnlyList<string> SupportedOpenApiSpecFamilies { get; init; } = [];
    public IReadOnlyList<string> UnsupportedOpenApiSpecFamilies { get; init; } = [];
    public IReadOnlyList<string> SupportedOpenApiEncodings { get; init; } = [];
    public string LockedMcpProtocolVersion { get; init; } = BusinessSkillDefaults.LockedMcpProtocolVersion;
    public IReadOnlyList<string> SupportedMcpTransports { get; init; } = [];
    public IReadOnlyList<string> UnsupportedMcpProtocolVersions { get; init; } = [];
    public IReadOnlyList<string> UnsupportedMcpTransports { get; init; } = [];
    public IReadOnlyList<string> UnsupportedMcpClientCapabilities { get; init; } = [];
    public IReadOnlyList<BusinessAuthKind> SupportedAuthKinds { get; init; } = [];
    public IReadOnlyList<string> UnsupportedAuthSchemes { get; init; } = [];
    public IReadOnlyList<string> UnsupportedHttpCapabilities { get; init; } = [];
    public IReadOnlyList<string> AllowedStepTypes { get; init; } = [];
}
