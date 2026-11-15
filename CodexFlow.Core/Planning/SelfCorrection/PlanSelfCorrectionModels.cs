// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Planning.SelfCorrection;

public enum PlanContractLintSeverity
{
    Info,
    Warning,
    Error
}

public sealed record PlanContractLintIssue(
    PlanContractLintSeverity Severity,
    string Code,
    string Message,
    string? TaskId = null);

public sealed record PlanSelfCorrectionRequest(
    string Goal,
    string WorkspacePath,
    string ProjectSummary,
    string RawBlueprint,
    bool IsExistingBlueprint,
    IReadOnlyList<PlanContractLintIssue> LintIssues);

public sealed record PlanSelfCorrectionResult(
    string CorrectedBlueprint,
    IReadOnlyList<PlanContractLintIssue> LintIssues,
    IReadOnlyList<string> CriticFindings,
    bool Changed,
    bool IsFallback,
    bool RequiresConfirmation);

public interface IPlanContractLinter
{
    IReadOnlyList<PlanContractLintIssue> LintBlueprint(string blueprint);
    IReadOnlyList<PlanContractLintIssue> LintTasks(IEnumerable<CodexTask> tasks);
}

public interface IPlanSelfCorrectionService
{
    Task<PlanSelfCorrectionResult> CorrectAsync(
        PlanSelfCorrectionRequest request,
        CancellationToken ct = default);
}
