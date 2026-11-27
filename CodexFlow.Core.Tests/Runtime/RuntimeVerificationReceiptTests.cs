// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Services.Background;
using CodexFlow.Services.Verification;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class RuntimeVerificationReceiptTests
{
    [Theory]
    [InlineData(true, 2, 0, 0, false, "passed")]
    [InlineData(false, 2, 1, 1, false, "failed")]
    [InlineData(false, 0, 0, 1, true, "not_run")]
    [InlineData(true, 0, 0, 0, false, "not_run")]
    public void TestReceipt_UsesAssertionsAndExitCode_EvenWhenTheToolWrappedItAsSuccess(
        bool testSuccess, int passed, int failed, int exitCode, bool preparationFailed, string expected)
    {
        var tool = new ToolExecutionResult("run_tests", "call-1", "tool output", true, Metadata: new JObject
        {
            ["exitCode"] = exitCode,
            ["dependencyPreparationFailed"] = preparationFailed,
            ["testResult"] = new JObject
            {
                ["success"] = testSuccess,
                ["passedCount"] = passed,
                ["failedCount"] = failed
            }
        });
        var receipt = RuntimeVerificationReceipt.Read(tool, command: null);
        Assert.NotNull(receipt);
        Assert.Equal(expected, receipt.Outcome);
        Assert.Equal(exitCode, receipt.ExitCode);
    }

    [Theory]
    [InlineData(true, false, "not_run", "dependency_preparation_failed")]
    [InlineData(false, true, "not_run", "scoped_export_rejected")]
    [InlineData(false, false, "failed", "command_failed")]
    public void NodeBuild_UsesTheHostReceipt(bool preparationFailed, bool exportRejected, string outcome, string reason)
    {
        var tool = new ToolExecutionResult("exec_cmd", "build-1", "$ npm run build\ncommand output", true, Metadata: new JObject
        {
            ["status"] = "Failed",
            ["exitCode"] = 1,
            ["dependencyPreparationFailed"] = preparationFailed,
            ["scopedExportRejected"] = exportRejected
        });

        var receipt = RuntimeVerificationReceipt.Read(tool, ToolCommandObservation.Read(tool));
        Assert.NotNull(receipt);
        Assert.Equal("node_build", receipt.Kind);
        Assert.Equal(outcome, receipt.Outcome);
        Assert.Equal(reason, receipt.Reason);
    }

    [Theory]
    [InlineData("skipped", true, true)]
    [InlineData("cancelled", true, true)]
    [InlineData("skipped", false, false)]
    [InlineData("completed", true, false)]
    public void StrictPlanKeepsStoppedTasksInTheDispatchOrder(string status, bool enforcePlan, bool expected)
        => Assert.Equal(expected, PlanTaskDispatchService.IsActionableTaskListStatus(status, enforcePlan));

    [Theory]
    [InlineData(3, 3, 0, 0, true)]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(3, 2, 1, 0, false)]
    [InlineData(3, 2, 0, 1, false)]
    public void NativeNodeArtifactTestsRequireCompletePassingTapEvidence(
        int total, int passed, int failed, int skipped, bool accepted)
    {
        var report = System.Text.Encoding.UTF8.GetBytes(
            $"TAP version 13\n# tests {total}\n# pass {passed}\n# fail {failed}\n# cancelled 0\n# skipped {skipped}\n# todo 0\n");
        Assert.Equal(accepted, TestEvidence.TryAcceptNodeTap(report, out var count));
        Assert.Equal(accepted ? total : 0, count);
    }

    [Fact]
    public void OrdinaryWorkerSummaryIsNotAnArtifactVerificationFailure()
    {
        const string summary = """{"success":true,"summary":"Forge completed."}""";
        Assert.Null(WorkerProjectionHelpers.ResolveVerification(summary));
        const string explicitFailure = """{"verificationPassed":false,"summary":"Build failed."}""";
        var verification = WorkerProjectionHelpers.ResolveVerification(explicitFailure);
        Assert.NotNull(verification);
        Assert.False(verification.Passed);
        Assert.Equal("Build failed.", verification.Summary);
    }

    [Fact]
    public void HistoricalCompletionWithAChangedFileAndTheBaselineHeadRequiresRecovery()
    {
        var result = new JObject
        {
            ["changedFiles"] = new JArray("candidate.txt"),
            ["artifactManifest"] = new JObject { ["BaseCommit"] = "baseline", ["HeadCommit"] = "baseline" }
        };
        Assert.True(ForgeArtifactManifestFactory.HasUncommittedResult(result));
        result["artifactManifest"]!["HeadCommit"] = "committed-candidate";
        Assert.False(ForgeArtifactManifestFactory.HasUncommittedResult(result));
        result["changedFiles"] = new JArray();
        result["artifactManifest"]!["HeadCommit"] = "baseline";
        Assert.False(ForgeArtifactManifestFactory.HasUncommittedResult(result));
    }

    [Fact]
    public void RecoveryRequiresTheSameApprovedPlanVersionAndTask()
    {
        var plan = new PlanArtifact { Status = PlanArtifactStatus.Executing, Markdown = "Implement T1." };
        plan.Tasks.Add(new CodexTask { Id = "T1" });
        var hash = PlanArtifactSupport.ComputeMarkdownHash(plan.Markdown);
        plan.Approval = new PlanApprovalRecord
        {
            ApprovedByUserId = "user",
            ApprovedMarkdownHash = hash,
            ApprovedPlanVersion = plan.Version
        };
        var payload = new JObject
        {
            ["planArtifactId"] = plan.Id,
            ["planVersion"] = plan.Version,
            ["planHash"] = hash,
            ["userId"] = "user"
        };

        Assert.True(PlanTaskRetryBinding.CanRetry(payload, plan, "T1"));
        Assert.False(PlanTaskRetryBinding.CanRetry(payload, plan, "T2"));
        plan.Version = "superseded";
        Assert.False(PlanTaskRetryBinding.CanRetry(payload, plan, "T1"));
    }

    [Fact]
    public void VerificationStop_DeliversHostEvidence_WithoutAnAutomaticQualityRetry()
    {
        var runtime = new QueryRuntimeResult
        {
            TerminationReason = QueryTerminationReason.Exception,
            TotalRounds = 1,
            TotalToolCalls = 1,
            ZeroToolCallRounds = 0,
            EmptyResponseCount = 0,
            RecoveryCount = 0,
            MalformedProtocolCount = 0,
            FinalText = "",
            TotalDurationMs = 1,
            TerminalDetailCode = "recovery_exhausted",
            TerminalDetailMessage = "failed_verification",
            VerificationReceipts = [new("node_build", "not_run", 1, "dependency_preparation_failed")]
        };
        var payload = new JObject
        {
            ["success"] = false,
            ["detailCode"] = runtime.TerminalDetailCode,
            ["failureContext"] = ForgeFailureContext.FromAttempt(0, runtime, 3, usedShadow: true, retained: false)
        };

        Assert.False(ForgeQualityRetry.ShouldRetry(payload, new JObject()));
        var failure = JobFailureClassifier.Classify(null, payload.ToString());
        var wire = JobFailureWire.FromJson(JobFailureWire.ToJson(failure));
        Assert.NotNull(wire);
        Assert.Contains(wire.Evidence, line => line.Contains("隔离工作树已清理", StringComparison.Ordinal));
        Assert.Contains(wire.Evidence, line => line.Contains("离线依赖准备失败", StringComparison.Ordinal));
    }
}
