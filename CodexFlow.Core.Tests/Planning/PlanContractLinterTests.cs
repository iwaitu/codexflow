// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.SelfCorrection;
using Xunit;

namespace CodexFlow.Core.Tests.Planning;

public sealed class PlanContractLinterTests
{
    [Fact]
    public void LintBlueprint_MissingAcceptance_ReturnsError()
    {
        var linter = new DefaultPlanContractLinter();

        var issues = linter.LintBlueprint("## 目标\n修改服务代码\n\n## 影响范围\n- 代码目录：src/App");

        Assert.Contains(issues, issue => issue.Code == "MissingAcceptance" && issue.Severity == PlanContractLintSeverity.Error);
    }

    [Fact]
    public void LintTasks_HighRiskWithRelaxedFallbackGate_ReturnsError()
    {
        var task = new CodexTask
        {
            Id = "T1",
            TaskType = CodexTaskClassifier.CodeTaskType,
            RiskLevel = "High",
            UnsafeIfDependencyFallbackPassed = false
        };
        task.ReplaceRequiredArtifacts([new ArtifactAssertion("file_exists", "src/App/Program.cs")]);

        var linter = new DefaultPlanContractLinter();
        var issues = linter.LintTasks([task]);

        Assert.Contains(issues, issue => issue.Code == "HighRiskFallbackGateRelaxed" && issue.TaskId == "T1");
    }
}
