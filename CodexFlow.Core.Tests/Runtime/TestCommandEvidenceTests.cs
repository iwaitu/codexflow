// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

// Live developer simulation (bugfix-python-pager): forge tasks with nothing left to change ran pytest cleanly and were failed all the same,
// because only `dotnet build` / `dotnet test` counted as evidence; one of them then made a same-text "write" to get past the rule.
public sealed class TestCommandEvidenceTests
{
    [Theory]
    [InlineData("$ python -m pytest", TestCommandEvidence.PythonTest)]
    [InlineData("$ python3 -m pytest test_pager.py -v", TestCommandEvidence.PythonTest)]
    [InlineData("$ py -3 -m pytest", TestCommandEvidence.PythonTest)]
    [InlineData("$ python -m unittest discover", TestCommandEvidence.PythonTest)]
    [InlineData("$ pytest -q", TestCommandEvidence.PythonTest)]
    [InlineData("$ npm test", TestCommandEvidence.NodeTest)]
    [InlineData("$ npm run test -- --watch=false", TestCommandEvidence.NodeTest)]
    [InlineData("$ pnpm test", TestCommandEvidence.NodeTest)]
    [InlineData("$ node --test", TestCommandEvidence.NodeTest)]
    [InlineData("$ npx jest pager.test.js", TestCommandEvidence.NodeTest)]
    public void TheTestCommandsOfPythonAndNode_AreRecognisedFromTheEchoedCommandLine(string echoed, string kind)
        => Assert.Equal(kind, TestCommandEvidence.Classify(echoed + "\n12 passed in 0.02s")?.Kind);

    [Theory]
    [InlineData("$ python pager.py")]
    [InlineData("$ python -m pip install pytest")]
    [InlineData("$ npm install")]
    [InlineData("$ npm run build")]
    [InlineData("$ node index.js")]
    [InlineData("I ran python -m pytest and all 12 tests passed")]   // prose, not an echoed command line
    [InlineData("")]
    public void AnythingElse_IsNotATestRun(string text)
        => Assert.Null(TestCommandEvidence.Classify(text));

    [Fact]
    public void OnlyABuildOrATestRun_ShowsTheProjectBehaves()
    {
        Assert.True(TestCommandEvidence.ProvesBehaviour("dotnet_build"));
        Assert.True(TestCommandEvidence.ProvesBehaviour("dotnet_test"));
        Assert.True(TestCommandEvidence.ProvesBehaviour(TestCommandEvidence.PythonTest));
        Assert.True(TestCommandEvidence.ProvesBehaviour(TestCommandEvidence.NodeTest));
        Assert.False(TestCommandEvidence.ProvesBehaviour("dotnet_restore"));
        Assert.False(TestCommandEvidence.ProvesBehaviour("dotnet_vulnerable_clean"));
        Assert.False(TestCommandEvidence.ProvesBehaviour(null));
    }

    private static QreV2BackedQueryRuntimeEngine.TurnEvidence Turn()
        => new(new QueryRuntimeRequest { SessionId = "test-evidence", EntryPoint = QueryLoopEntryPoint.ForgeWorker, InitialMessages = [] });

    private static ToolExecutionResult Exec(string output) => new("exec_cmd", Guid.NewGuid().ToString("N"), output, true);

    [Fact]
    public void ACleanPytestRun_IsVerificationEvidenceOfTheTurn()
    {
        var turn = Turn();

        turn.Record(Exec("$ python -m pytest\n12 passed in 0.02s"));

        var ledger = turn.SnapshotForStopHook();
        Assert.Contains(ledger.Verifications, static verification => verification.Kind == TestCommandEvidence.PythonTest);
        Assert.Empty(ledger.FailedVerifications);
    }

    [Fact]
    public void ALaterFailingRun_RetiresThePass_AndALaterCleanRunRetiresTheFailure()
    {
        var turn = Turn();
        turn.Record(Exec("$ npm test\n3 passing"));

        turn.Record(Exec("⚠️ 命令执行完成 (exit code 1)\n$ npm test\n1 failing"));

        var failing = turn.SnapshotForStopHook();
        Assert.DoesNotContain(failing.Verifications, static verification => verification.Kind == TestCommandEvidence.NodeTest);
        Assert.Contains(failing.FailedVerifications, static verification => verification.Kind == TestCommandEvidence.NodeTest);

        turn.Record(Exec("$ npm test\n4 passing"));

        var passing = turn.SnapshotForStopHook();
        Assert.Contains(passing.Verifications, static verification => verification.Kind == TestCommandEvidence.NodeTest);
        Assert.Empty(passing.FailedVerifications);
    }

    [Fact]
    public void ACompletedReportAfterACleanPytestRun_LetsAForgeTaskFinishWithoutAChange()
    {
        var ledger = new QueryEvidenceLedger { WorkerReport = new WorkerStatusReport(WorkerStatusTool.Completed, "代码已正确", []) };
        ledger.Verifications.Add(new RuntimeVerificationEvidence { Kind = TestCommandEvidence.PythonTest, Summary = "pytest" });

        Assert.True(VerifiedNoChangeCompletion.IsStated(ledger));
    }
}
