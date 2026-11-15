// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Planning.Deviation;

public sealed class DefaultPlanDeviationDetector : IPlanDeviationDetector
{
    public Task<PlanDeviationDecision> DetectAsync(
        PlanDeviationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!request.Session.Plan.Any(t => string.Equals(t.Id, request.Task.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new PlanDeviationDecision(
                PlanDeviationSeverity.PlanInvalidated,
                RequiresPlanPatch: false,
                RequiresPlanReset: true,
                $"当前任务 {request.Task.Id} 未在 session plan 中找到，计划快照已失效。",
                [$"missing task id: {request.Task.Id}"]));
        }

        var findings = new List<string>();
        var suggestedScopeFiles = new List<string>();
        if (request.TaskFileScope is { HasConstraints: true } && request.ChangedFiles.Count > 0)
        {
            var outOfScope = TaskFileScopeGuard.GetOutOfScopeChanges(request.ChangedFiles, request.TaskFileScope);
            if (outOfScope.Count > 0)
            {
                findings.Add($"超出当前任务范围的文件改动：{string.Join(", ", outOfScope.Take(12))}{(outOfScope.Count > 12 ? " ..." : "")}");
                suggestedScopeFiles.AddRange(outOfScope);
            }
        }

        if (request.ValidationResult is { Success: false } validationResult &&
            validationResult.Issues?.Any(issue => issue.Contains("RequiredArtifacts", StringComparison.OrdinalIgnoreCase) ||
                                                 issue.Contains("ForbiddenStates", StringComparison.OrdinalIgnoreCase) ||
                                                 issue.Contains("结构化", StringComparison.OrdinalIgnoreCase)) == true)
        {
            findings.Add("验证失败与结构化验收契约相关，需要局部补强计划契约。");
        }

        if (findings.Count == 0)
        {
            return Task.FromResult(PlanDeviationDecision.None);
        }

        return Task.FromResult(new PlanDeviationDecision(
            PlanDeviationSeverity.Recoverable,
            RequiresPlanPatch: true,
            RequiresPlanReset: false,
            "检测到可通过局部 Plan Patch 处理的计划偏差。",
            findings)
        {
            SuggestedScopeFiles = suggestedScopeFiles
        });
    }
}
