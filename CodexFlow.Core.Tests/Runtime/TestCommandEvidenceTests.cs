// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Newtonsoft.Json.Linq;
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
    public void StructuredOutcomesKeepOnlyTheLatestKnownVerdictForTheCommand()
    {
        var turn = Turn();
        const string receipt = "✅ command_task_id: job\n$ dotnet build\nBuild succeeded.";
        using var failed = JsonDocument.Parse("""{"exit_code":"1","status":"Completed"}""");
        var passed = new JObject { ["ExitCode"] = 0, ["Status"] = "Completed" };
        var unknown = new JObject { ["Stdout"] = "Build succeeded." };

        turn.Record(Exec(receipt) with { Metadata = passed });
        Assert.Equal("dotnet_build", Assert.Single(turn.SnapshotForStopHook().Verifications).Kind);

        turn.Record(Exec(receipt) with { Metadata = unknown });
        Assert.Empty(turn.SnapshotForStopHook().Verifications);
        Assert.Empty(turn.SnapshotForStopHook().FailedVerifications);

        turn.Record(Exec(receipt) with { Metadata = passed });
        turn.Record(Exec(receipt) with { Metadata = failed.RootElement });
        turn.Record(Exec(receipt) with { Metadata = unknown });
        var ledger = turn.SnapshotForStopHook();
        Assert.Empty(ledger.Verifications);
        Assert.Equal("dotnet_build", Assert.Single(ledger.FailedVerifications).Kind);

        turn.Record(Exec(receipt) with { Metadata = passed });
        ledger = turn.SnapshotForStopHook();
        Assert.Equal("dotnet_build", Assert.Single(ledger.Verifications).Kind);
        Assert.Empty(ledger.FailedVerifications);
    }

    [Theory]
    [InlineData("exec_cmd", "COMMAND_TASK_STARTED\ncommand_task_id: job\n$ dotnet build\nUse task_output")]
    [InlineData("task_output", "command output: job\nstatus=Completed exit_code=0 latest_seq=1\ncwd: /work\n$ dotnet build")]
    [InlineData("hs_read", "$ dotnet build\nBuild succeeded.")]
    [InlineData("exec_cmd", "✅ command_task_id: job\n$ cat log.txt\n$ dotnet build\nBuild succeeded.")]
    [InlineData("exec_cmd", "✅ command_task_id: job\n$ echo '$ npm test'\n$ npm test")]
    [InlineData("exec_cmd", "✅ command_task_id: job\n$ dotnet build-server shutdown\nBuild server shut down.")]
    [InlineData("exec_cmd", """{"output":"✅ command_task_id: job\n$ dotnet build\nBuild succeeded."}""")]
    public void UnrelatedOutputsCannotRetireAnExistingFailure(string toolName, string text)
    {
        var turn = Turn();
        turn.Record(Exec("⚠️ 命令执行完成 (exit code 1)\n$ dotnet build\nBuild failed."));
        turn.Record(Exec(text) with { ToolName = toolName });

        var ledger = turn.SnapshotForStopHook();
        Assert.Empty(ledger.Verifications);
        Assert.Equal("dotnet_build", Assert.Single(ledger.FailedVerifications).Kind);
    }

    [Fact]
    public void StructuredRestoreDiagnosticsCarryOverToBuildMissingAssets()
    {
        var turn = Turn();
        turn.Record(Exec("✅ command_task_id: job\n$ dotnet restore") with
        {
            Metadata = new JObject { ["ExitCode"] = 1, ["Stdout"] = "Error NU1301: feed unreachable" }
        });
        turn.Record(Exec("⚠️ 命令执行完成 (exit code 1)\n$ dotnet build\nerror NETSDK1004: Assets file missing"));

        var ledger = turn.SnapshotForStopHook();
        Assert.Equal(2, ledger.FailedVerifications.Count);
        Assert.All(ledger.FailedVerifications, failure => Assert.True(failure.EnvironmentLimit));
    }

    [Fact]
    public void ACommandEchoWithoutATerminalReceiptDoesNotRetireExistingEvidence()
    {
        var turn = Turn();
        turn.Record(Exec("✅ command_task_id: job\n$ dotnet build\nBuild succeeded."));
        turn.Record(Exec("$ dotnet build") with { Metadata = new JObject() });

        var ledger = turn.SnapshotForStopHook();
        Assert.Equal("dotnet_build", Assert.Single(ledger.Verifications).Kind);
        Assert.Empty(ledger.FailedVerifications);
    }

    [Fact]
    public void ACompletedReportAfterACleanPytestRun_LetsAForgeTaskFinishWithoutAChange()
    {
        var ledger = new QueryEvidenceLedger { WorkerReport = new WorkerStatusReport(WorkerStatusTool.Completed, "代码已正确", []) };
        ledger.Verifications.Add(new RuntimeVerificationEvidence { Kind = TestCommandEvidence.PythonTest, Summary = "pytest" });

        Assert.True(VerifiedNoChangeCompletion.IsStated(ledger));
    }
}
