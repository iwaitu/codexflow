// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodexFlow.Contracts.BusinessSkills;

/// <summary>
/// 业务写入审批请求类型常量，扩展现有 ExecutionApprovalBroker/GatewayApprovalController 时使用。
/// </summary>
public static class BusinessApprovalConstants
{
    public const string BusinessSkillWriteKind = "business_skill_write";

    /// <summary>
    /// 首版写入审批仅允许本次批准（allow_once）或拒绝（deny），禁止 allow_session / allow_always。
    /// </summary>
    public static IReadOnlyList<string> AllowedDecisions { get; } = ["deny", "allow_once"];
}

public sealed record BusinessApprovalRequestPreviewDto(
    string CapabilityId,
    string MethodOrTool,
    string TargetPath,
    string RedactedBodyPreview);

public sealed record BusinessApprovalSummaryDto
{
    public string SystemName { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string TargetRecordSummary { get; init; } = string.Empty;
    public string PlannedMutation { get; init; } = string.Empty;
    public int BatchCount { get; init; }
    public decimal? KeyAmount { get; init; }
    public string? Currency { get; init; }
    public bool WriteStepUnverified { get; init; }
    public IReadOnlyList<BusinessApprovalRequestPreviewDto> RequestPreviews { get; init; } = [];
}

/// <summary>
/// 绑定企业写入批准上下文的全部不可变要素；任一字段改变均导致批准失效。
/// 不包含明文凭据。
/// </summary>
public sealed record BusinessApprovalDigestInput(
    string RunId,
    string SkillId,
    int SkillVersion,
    string SkillContentHash,
    string ConnectionId,
    int ConnectionRevision,
    string CredentialRevision,
    string CapabilityId,
    string CatalogSnapshotHash,
    string NormalizedArgumentsJson,
    IReadOnlyList<string> BatchItemIds,
    DateTimeOffset ExpiresAtUtc)
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string ComputeRequestDigest()
    {
        var canonical = new
        {
            kind = BusinessApprovalConstants.BusinessSkillWriteKind,
            runId = RunId,
            skillId = SkillId,
            skillVersion = SkillVersion,
            skillContentHash = SkillContentHash,
            connectionId = ConnectionId,
            connectionRevision = ConnectionRevision,
            credentialRevision = CredentialRevision,
            capabilityId = CapabilityId,
            catalogSnapshotHash = CatalogSnapshotHash,
            normalizedArgumentsJson = NormalizedArgumentsJson,
            batchItemIds = BatchItemIds,
            expiresAtUtc = ExpiresAtUtc.ToUniversalTime().ToString("O")
        };

        var serialized = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record BusinessApprovalRequestDto
{
    public string ApprovalId { get; init; } = string.Empty;
    public string RequestDigest { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string StepId { get; init; } = string.Empty;
    public string Kind { get; init; } = BusinessApprovalConstants.BusinessSkillWriteKind;
    public IReadOnlyList<string> AllowedDecisions { get; init; } = BusinessApprovalConstants.AllowedDecisions;
    public BusinessApprovalSummaryDto Summary { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record SkillDraftDto
{
    public string Id { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string CatalogSnapshotId { get; init; } = string.Empty;
    public int Revision { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string BusinessGoal { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedCapabilityIds { get; init; } = [];
    public SkillDefinitionV1? CandidateDefinition { get; init; }
    public IReadOnlyList<string> ClarificationQuestions { get; init; } = [];
    public IReadOnlyList<BusinessSkillFieldError> ValidationErrors { get; init; } = [];
    public bool NeedsRevalidation { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record CreateSkillDraftRequest(
    string PluginId,
    string? CatalogSnapshotId,
    string Name,
    string BusinessGoal,
    IReadOnlyList<string>? SelectedCapabilityIds = null);

public sealed record UpdateSkillDraftRequest(
    int ExpectedRevision,
    string? Name = null,
    string? BusinessGoal = null,
    SkillDefinitionV1? CandidateDefinition = null);

public sealed record PutDraftSelectionRequest(
    int ExpectedRevision,
    string CatalogSnapshotId,
    IReadOnlyList<string> SelectedCapabilityIds);

public sealed record GenerateSkillCandidateRequest(
    string ClientRequestId,
    bool ConfirmModelDataScope,
    bool IncludeSanitizedSamples = false);

public sealed record GenerateSkillCandidateResultDto(
    PluginOperationAcceptedDto Operation,
    SkillDraftDto Draft,
    IReadOnlyList<string> DisclosedDataCategories);

public sealed record PublishSkillDraftRequest(
    int ExpectedRevision,
    string? ReleaseNote = null);

public sealed record ActivateSkillVersionRequest(
    int Version);

public sealed record SkillMarkdownPreviewDto(
    string SkillId,
    int Version,
    string ContentHash,
    string Markdown);

public sealed record PublishedSkillVersionDto(
    int Version,
    string ContentHash,
    string CatalogSnapshotId,
    bool NeedsRevalidation,
    bool WriteStepVerified,
    DateTimeOffset PublishedAtUtc);

public sealed record PublishedSkillDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string Environment { get; init; } = "default";
    public int ActiveVersion { get; init; }
    public IReadOnlyList<PublishedSkillVersionDto> Versions { get; init; } = [];
}

public sealed record StartSkillRunRequest(
    int? Version,
    string ClientRequestId,
    IReadOnlyDictionary<string, JsonElement> Inputs,
    bool IsTestRun = false,
    string? SessionId = null,
    bool AllowWriteInTestRun = false);

public sealed record SkillRunStepDto
{
    public string StepId { get; init; } = string.Empty;
    public string StepType { get; init; } = string.Empty;
    public SkillRunStepState State { get; init; }
    public string? RequestSummary { get; init; }
    public string? ResponseSummary { get; init; }
    public string? Evidence { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

public sealed record SkillRunDto
{
    public string RunId { get; init; } = string.Empty;
    public string SkillId { get; init; } = string.Empty;
    public int SkillVersion { get; init; }
    public string CatalogSnapshotId { get; init; } = string.Empty;
    public bool IsTestRun { get; init; }
    public string ClientRequestId { get; init; } = string.Empty;
    public SkillRunStatus Status { get; init; }
    public bool StopRequested { get; init; }
    public int ExternalRequestCount { get; init; }
    public int ReadOnlyRetryCount { get; init; }
    public IReadOnlyList<SkillRunStepDto> Steps { get; init; } = [];
    public BusinessApprovalRequestDto? PendingApproval { get; init; }
    public JsonElement? OutputPayload { get; init; }
    public BusinessSkillErrorDto? Error { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
