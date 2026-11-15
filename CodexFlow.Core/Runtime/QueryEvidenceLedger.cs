// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Runtime working-set evidence that should survive prompt projection decisions.
/// It is intentionally compact and stores handles/summaries instead of raw tool output.
/// </summary>
public sealed class QueryEvidenceLedger
{
    public List<FileEvidence> Files { get; } = [];

    public List<ToolEvidence> ToolResults { get; } = [];

    public List<ChangedFileEvidence> ChangedFiles { get; } = [];

    public List<DiffChangeEvidence> DiffChanges { get; } = [];

    public List<RuntimeVerificationEvidence> Verifications { get; } = [];

    /// <summary>Verification commands whose LATEST run did not exit cleanly (a later clean run of the same kind removes the entry).</summary>
    public List<RuntimeVerificationEvidence> FailedVerifications { get; } = [];

    /// <summary>Write-tool calls that only previewed their edit (dryRun) with no real write after them: nothing was changed.</summary>
    public List<UnappliedPreviewEvidence> UnappliedPreviews { get; } = [];

    /// <summary>What the worker stated about its own task through <c>report_task_status</c>; the latest call wins. Null when it never called it.</summary>
    public WorkerStatusReport? WorkerReport { get; set; }

    public List<PendingModificationEvidence> PendingModifications { get; } = [];

    public string? LastToolBatchSummary { get; set; }

    public List<RuntimeFailureEvidence> Failures { get; } = [];

    public List<string> RepeatedEvidenceKeys { get; } = [];

    public Dictionary<string, int> SeenReadEvidenceKeys { get; } = new(StringComparer.Ordinal);
}

public sealed record FileEvidence
{
    public required string FilePath { get; init; }
    public string? ToolName { get; init; }
    public string? SnapshotId { get; init; }
    public string? FileFingerprint { get; init; }
    public int? WindowStartLine { get; init; }
    public int? WindowEndLine { get; init; }
    public int? TotalLineCount { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record UnappliedPreviewEvidence
{
    public required string ToolName { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ToolEvidence
{
    public required string ToolName { get; init; }
    public required string CallId { get; init; }
    public required bool Success { get; init; }
    public string? Summary { get; init; }
    public int? ResultLength { get; init; }
    public bool IsOutputTruncated { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChangedFileEvidence
{
    public required string FilePath { get; init; }
    public string? Status { get; init; }
    public string? SourceToolName { get; init; }
    public string? SourceCommand { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DiffChangeEvidence
{
    public required string FilePath { get; init; }
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Added { get; init; } = [];
    public string? SourceToolName { get; init; }
    public string? SourceCommand { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RuntimeVerificationEvidence
{
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string? SourceToolName { get; init; }
    public string? SourceCommand { get; init; }

    /// <summary>
    /// For a failed verification: the failure is a limit of the environment the worker cannot repair (a package missing from the offline source,
    /// an unreachable feed), identified by the NuGet diagnostic code in the command's own output. It is reported, not fixed.
    /// </summary>
    public bool EnvironmentLimit { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PendingModificationEvidence
{
    public required string Source { get; init; }
    public string? RequiredToolName { get; init; }
    public string? AssistantPlanSummary { get; init; }
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RuntimeFailureEvidence
{
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RuntimeRecoveryHint
{
    public required string Source { get; init; }
    public int Attempt { get; init; }
    public string? RequiredToolName { get; init; }
    public bool ToolCallRequired { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}
