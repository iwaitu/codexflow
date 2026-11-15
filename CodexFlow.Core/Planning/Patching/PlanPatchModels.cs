// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Deviation;

namespace CodexFlow.Core.Planning.Patching;

public sealed record PlanPatchRequest(
    CodexSession Session,
    CodexTask FailedTask,
    PlanDeviationDecision Deviation,
    ValidationResult? ValidationResult,
    TaskExecutionEvidenceResult? Evidence,
    string? DiffArtifactPath);

public sealed record PlanPatchResult(
    bool Applied,
    bool RequiresUserConfirmation,
    IReadOnlyList<CodexTask> PatchedTasks,
    string Summary,
    IReadOnlyList<string> Warnings);

public sealed record PlanPatchResolutionRequest(
    CodexSession Session,
    string? TaskId,
    bool Approved,
    string? Notes,
    IReadOnlyList<string>? ApprovedFiles = null);

public interface IPlanPatchService
{
    Task<PlanPatchResult> CreatePendingPatchAsync(
        PlanPatchRequest request,
        CancellationToken ct = default);

    Task<PlanPatchResult> ResolvePendingPatchAsync(
        PlanPatchResolutionRequest request,
        CancellationToken ct = default);
}
