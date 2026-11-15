// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Planning.Deviation;

public enum PlanDeviationSeverity
{
    None,
    Informational,
    Recoverable,
    Blocking,
    PlanInvalidated
}

public sealed record PlanDeviationRequest(
    CodexSession Session,
    CodexTask Task,
    string MainRoot,
    string WorkPath,
    IReadOnlyList<GitFileChange> ChangedFiles,
    TaskFileScopeDescriptor? TaskFileScope,
    ValidationResult? ValidationResult,
    TaskExecutionEvidenceResult? Evidence);

public sealed record PlanDeviationDecision(
    PlanDeviationSeverity Severity,
    bool RequiresPlanPatch,
    bool RequiresPlanReset,
    string Summary,
    IReadOnlyList<string> Findings)
{
    public IReadOnlyList<string> SuggestedScopeFiles { get; init; } = Array.Empty<string>();

    public static PlanDeviationDecision None { get; } =
        new(PlanDeviationSeverity.None, false, false, "未检测到计划偏差。", Array.Empty<string>());
}

public interface IPlanDeviationDetector
{
    Task<PlanDeviationDecision> DetectAsync(
        PlanDeviationRequest request,
        CancellationToken ct = default);
}
