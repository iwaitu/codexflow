// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Planning.SelfCorrection;

public sealed class DefaultPlanSelfCorrectionService : IPlanSelfCorrectionService
{
    private readonly ILogger<DefaultPlanSelfCorrectionService> _logger;

    public DefaultPlanSelfCorrectionService(ILogger<DefaultPlanSelfCorrectionService> logger)
    {
        _logger = logger;
    }

    public Task<PlanSelfCorrectionResult> CorrectAsync(
        PlanSelfCorrectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var findings = request.LintIssues
            .Where(issue => issue.Severity != PlanContractLintSeverity.Info)
            .Select(issue => $"{issue.Severity}: {issue.Code} - {issue.Message}")
            .ToList();

        if (request.IsExistingBlueprint)
        {
            _logger.LogInformation(
                "Plan self-correction linted existing blueprint and requires confirmation before mutation. Issues={IssueCount}",
                findings.Count);

            return Task.FromResult(new PlanSelfCorrectionResult(
                request.RawBlueprint,
                request.LintIssues,
                findings,
                Changed: false,
                IsFallback: true,
                RequiresConfirmation: findings.Count > 0));
        }

        var corrected = request.RawBlueprint;
        if (findings.Count > 0)
        {
            corrected += "\n\n## Self-Correction Contract Notes\n" +
                string.Join("\n", findings.Select((finding, index) => $"{index + 1}. {finding}")) +
                "\n\n以上问题必须在委员会评审与最终任务投影中通过明确文件范围、验收契约、验证命令或风险回滚说明解决。";
        }

        return Task.FromResult(new PlanSelfCorrectionResult(
            corrected,
            request.LintIssues,
            findings,
            Changed: !string.Equals(corrected, request.RawBlueprint, StringComparison.Ordinal),
            IsFallback: false,
            RequiresConfirmation: false));
    }
}
