// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

// Live developer simulation (dependency-upgrade-compliant): forge tasks that needed no change were pushed to make one - an empty test project
// for a task whose condition did not hold, a marker comment for a task that only ran the tests.
public sealed class VerifiedNoChangeCompletionTests
{
    private static readonly RequiredToolExecutionContract ForgeWrite = new()
    {
        Name = VerifiedNoChangeCompletion.ForgeWriteContractName,
        AnyOfToolNames = ["hs_write", "write_file"]
    };

    private static QueryEvidenceLedger Ledger(WorkerStatusReport? report, string? passed = "dotnet_test", string? failed = null, bool preview = false)
    {
        var ledger = new QueryEvidenceLedger { WorkerReport = report };
        if (passed != null) ledger.Verifications.Add(new RuntimeVerificationEvidence { Kind = passed, Summary = passed });
        if (failed != null) ledger.FailedVerifications.Add(new RuntimeVerificationEvidence { Kind = failed, Summary = failed });
        if (preview) ledger.UnappliedPreviews.Add(new UnappliedPreviewEvidence { ToolName = "hs_write" });
        return ledger;
    }

    private static WorkerStatusReport Completed(params string[] unfinished) => new(WorkerStatusTool.Completed, "条件不成立", unfinished);

    [Fact]
    public void ACompletedReportAndACleanBuildOrTest_IsTheEvidence()
    {
        Assert.True(VerifiedNoChangeCompletion.IsStated(Ledger(Completed())));
        Assert.True(VerifiedNoChangeCompletion.IsStated(Ledger(Completed(), passed: "dotnet_build")));
        Assert.True(VerifiedNoChangeCompletion.Waives(ForgeWrite, QueryLoopEntryPoint.ForgeWorker, Ledger(Completed())));
    }

    [Fact]
    public void WithoutEveryPartOfIt_TheWorkerMustStillWrite()
    {
        Assert.False(VerifiedNoChangeCompletion.IsStated(null));
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(report: null)));                                   // said nothing
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(new WorkerStatusReport(WorkerStatusTool.Blocked, "缺包", []))));
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(Completed("Snappier"))));                         // lists unfinished changes
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(Completed(), passed: null)));                     // verified nothing
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(Completed(), passed: "dotnet_restore")));         // a restore proves no behaviour
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(Completed(), failed: "dotnet_build")));           // something is still failing
        Assert.False(VerifiedNoChangeCompletion.IsStated(Ledger(Completed(), preview: true)));                    // previewed an edit it never applied
    }

    [Fact]
    public void ItStandsInOnlyForTheForgeWorkersWriteContract()
    {
        var ledger = Ledger(Completed());
        var plan = new RequiredToolExecutionContract { Name = "explicit_plan_request", AnyOfToolNames = ["create_session_plan"] };

        Assert.False(VerifiedNoChangeCompletion.Waives(plan, QueryLoopEntryPoint.ForgeWorker, ledger));
        Assert.False(VerifiedNoChangeCompletion.Waives(ForgeWrite, QueryLoopEntryPoint.GatewayMessageProcessor, ledger));
        Assert.False(VerifiedNoChangeCompletion.Waives(null, QueryLoopEntryPoint.ForgeWorker, ledger));
    }

    [Fact]
    public void TheWorkerIsToldTheHonestWayOut()
    {
        Assert.Contains("report_task_status", VerifiedNoChangeCompletion.Feedback, StringComparison.Ordinal);
        Assert.Contains("do NOT invent one", VerifiedNoChangeCompletion.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheTaskDeclaresItProduces_MustAlreadyBeInTheWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("no-change-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.csproj"), "<PackageReference Include=\"Snappier\" Version=\"1.0.0\" />");
            var task = new CodexTask { Id = "T1", Title = "固定 Snappier 1.3.1" };
            task.RequiredArtifacts.Add(new ArtifactAssertion("file_contains", "A.csproj", "1.3.1"));

            Assert.NotEmpty(VerifiedNoChangeCompletion.UnmetAssertions(root, task));

            File.WriteAllText(Path.Combine(root, "A.csproj"), "<PackageReference Include=\"Snappier\" Version=\"1.3.1\" />");

            Assert.Empty(VerifiedNoChangeCompletion.UnmetAssertions(root, task));
            Assert.Empty(VerifiedNoChangeCompletion.UnmetAssertions(root, new CodexTask { Id = "T2", Title = "回归测试" }));
            Assert.Empty(VerifiedNoChangeCompletion.UnmetAssertions(root, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
