// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Planning.SelfCorrection;

public sealed class DefaultPlanContractLinter : IPlanContractLinter
{
    private static readonly string[] ValidationMarkers =
    [
        "验收", "验证", "verification", "acceptance", "RequiredArtifacts", "ForbiddenStates"
    ];

    private static readonly string[] ScopeMarkers =
    [
        "影响范围", "代码目录", "涉及模块", "涉及文件", "scope", "files"
    ];

    private static readonly string[] TestMarkers =
    [
        "dotnet build", "dotnet test", "npm test", "npm run build", "pytest", "验证步骤"
    ];

    public IReadOnlyList<PlanContractLintIssue> LintBlueprint(string blueprint)
    {
        if (string.IsNullOrWhiteSpace(blueprint))
        {
            return
            [
                new PlanContractLintIssue(
                    PlanContractLintSeverity.Error,
                    "BlueprintEmpty",
                    "初始蓝图为空，无法进入委员会评审。")
            ];
        }

        var issues = new List<PlanContractLintIssue>();
        AddMissingMarkerIssue(blueprint, ValidationMarkers, issues, "MissingAcceptance", "蓝图缺少明确验收标准或结构化验收契约。", PlanContractLintSeverity.Error);
        AddMissingMarkerIssue(blueprint, ScopeMarkers, issues, "MissingScope", "蓝图缺少明确文件范围或模块范围。", PlanContractLintSeverity.Warning);
        AddMissingMarkerIssue(blueprint, TestMarkers, issues, "MissingVerificationCommand", "蓝图缺少可执行 build/test/lint 验证命令。", PlanContractLintSeverity.Warning);

        if (!blueprint.Contains("风险", StringComparison.OrdinalIgnoreCase) &&
            !blueprint.Contains("回滚", StringComparison.OrdinalIgnoreCase) &&
            !blueprint.Contains("rollback", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PlanContractLintIssue(
                PlanContractLintSeverity.Warning,
                "MissingRiskRollback",
                "蓝图缺少风险或回滚说明。"));
        }

        return issues;
    }

    public IReadOnlyList<PlanContractLintIssue> LintTasks(IEnumerable<CodexTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var issues = new List<PlanContractLintIssue>();
        foreach (var task in tasks.Where(t => t != null))
        {
            var isCodeTask = CodexTaskClassifier.IsCodeExecutionTask(task);
            if (isCodeTask && task.RequiredArtifacts.Count == 0 && task.ForbiddenStates.Count == 0)
            {
                issues.Add(new PlanContractLintIssue(
                    PlanContractLintSeverity.Warning,
                    "MissingArtifactContract",
                    "代码任务缺少 RequiredArtifacts/ForbiddenStates，后续 validator 将 fail-closed。",
                    task.Id));
            }

            if (isCodeTask && task.ChecklistItems.Count == 0)
            {
                issues.Add(new PlanContractLintIssue(
                    PlanContractLintSeverity.Warning,
                    "MissingChecklist",
                    "代码任务缺少 ChecklistItems，无法进行增量验证。",
                    task.Id));
            }

            if (string.Equals(task.RiskLevel, "High", StringComparison.OrdinalIgnoreCase) &&
                !task.UnsafeIfDependencyFallbackPassed)
            {
                issues.Add(new PlanContractLintIssue(
                    PlanContractLintSeverity.Error,
                    "HighRiskFallbackGateRelaxed",
                    "高风险任务不能关闭 UnsafeIfDependencyFallbackPassed 门禁。",
                    task.Id));
            }
        }

        return issues;
    }

    private static void AddMissingMarkerIssue(
        string text,
        IReadOnlyList<string> markers,
        List<PlanContractLintIssue> issues,
        string code,
        string message,
        PlanContractLintSeverity severity)
    {
        if (markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        issues.Add(new PlanContractLintIssue(severity, code, message));
    }
}
