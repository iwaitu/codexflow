// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class ToolObservationProcessorTests
{
    [Fact]
    public void Observe_UpdatesLedgerAndRepeatedReadEvidence_ForStableHashlineSnapshot()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();
        var request = CreateRequest();
        var firstRead = BuildHashlineRead("call-1", "CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", "snap_1", "abc123");
        var secondRead = BuildHashlineRead("call-2", "CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", "snap_1", "abc123");

        var firstObservation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults = [firstRead],
            RuntimeRequest = request,
            State = state
        });
        var secondObservation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults = [secondRead],
            RuntimeRequest = request,
            State = state
        });

        Assert.False(firstObservation.HasRepeatedReadEvidence);
        Assert.True(secondObservation.HasRepeatedReadEvidence);
        Assert.Equal(["CodexFlow.Core/Runtime/QueryRuntimeEngine.cs"], secondObservation.RepeatedReadTargets);
        var fileEvidence = Assert.Single(state.EvidenceLedger.Files);
        Assert.Equal("CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", fileEvidence.FilePath);
        Assert.Equal("snap_1", fileEvidence.SnapshotId);
        Assert.Equal("abc123", fileEvidence.FileFingerprint);
        Assert.Equal(["CodexFlow.Core/Runtime/QueryRuntimeEngine.cs"], state.EvidenceLedger.RepeatedEvidenceKeys);
        Assert.Equal(2, state.EvidenceLedger.ToolResults.Count);
    }

    [Fact]
    public void Observe_ClearsPendingModificationEvidence_AfterSuccessfulWrite()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();
        state.EvidenceLedger.PendingModifications.Add(new PendingModificationEvidence
        {
            Source = "unexecuted_write_intent",
            RequiredToolName = "hs_write",
            AssistantPlanSummary = "change Pending to Ready",
            CandidateFiles = ["src/CleanApp.Domain/AppFile.cs"]
        });

        var observation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "hs_write",
                    CallId: "write-1",
                    Result: "wrote src/CleanApp.Domain/AppFile.cs",
                    Success: true,
                    ResultLength: 36)
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.True(observation.HasWriteEvidence);
        Assert.Empty(state.EvidenceLedger.PendingModifications);
        var toolEvidence = Assert.Single(state.EvidenceLedger.ToolResults);
        Assert.Equal("hs_write", toolEvidence.ToolName);
        Assert.True(toolEvidence.Success);
    }

    [Fact]
    public void Observe_KeepsPendingModificationEvidence_AfterFailedWrite()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();
        state.EvidenceLedger.PendingModifications.Add(new PendingModificationEvidence
        {
            Source = "unexecuted_write_intent",
            RequiredToolName = "hs_write",
            AssistantPlanSummary = "change Pending to Ready",
            CandidateFiles = ["src/CleanApp.Domain/AppFile.cs"]
        });

        var observation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "hs_write",
                    CallId: "write-1",
                    Result: "fingerprint mismatch",
                    Success: false,
                    ResultLength: 20)
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.True(observation.HasWriteEvidence);
        Assert.Single(state.EvidenceLedger.PendingModifications);
        Assert.Single(state.EvidenceLedger.Failures);
    }

    [Fact]
    public void Observe_ReportsRequiredToolContractSatisfied_FromSuccessfulTools()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();
        state.ExecutedToolNames.Add("hs_write");
        state.SuccessfulToolNames.Add("hs_write");
        var request = CreateRequest() with
        {
            RequiredToolContract = new RequiredToolExecutionContract
            {
                Name = "forge_worker_write_evidence",
                AnyOfToolNames = ["hs_write"],
                RequireSuccessfulResult = true
            }
        };

        var observation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "hs_write",
                    CallId: "write-1",
                    Result: "ok",
                    Success: true,
                    ResultLength: 2)
            ],
            RuntimeRequest = request,
            State = state
        });

        Assert.True(observation.RequiredToolContractSatisfied);
    }

    [Fact]
    public void Observe_ReportsRequiredToolContractUnsatisfied_WhenRequiredToolFailed()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();
        state.ExecutedToolNames.Add("hs_write");
        var request = CreateRequest() with
        {
            RequiredToolContract = new RequiredToolExecutionContract
            {
                Name = "forge_worker_write_evidence",
                AnyOfToolNames = ["hs_write"],
                RequireSuccessfulResult = true
            }
        };

        var observation = processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "hs_write",
                    CallId: "write-1",
                    Result: "failed",
                    Success: false,
                    ResultLength: 6)
            ],
            RuntimeRequest = request,
            State = state
        });

        Assert.False(observation.RequiredToolContractSatisfied);
    }

    [Fact]
    public void Observe_RecordsChangedFiles_FromGitStatusShortCommand()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();

        processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "exec_cmd",
                    CallId: "cmd-1",
                    Result: "✅ command_task_id: cmd-1\n$ git status --short\n M src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj\n",
                    Success: true,
                    ResultLength: 120,
                    Metadata: new JObject
                    {
                        ["Stdout"] = " M src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj\n"
                    })
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        var changed = Assert.Single(state.EvidenceLedger.ChangedFiles);
        Assert.Equal("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj", changed.FilePath);
        Assert.Equal("M", changed.Status);
        Assert.Equal("exec_cmd", changed.SourceToolName);
    }

    [Fact]
    public void Observe_RecordsPackageChanges_FromGitDiffCommand()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();

        processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "exec_cmd",
                    CallId: "cmd-1",
                    Result: "✅ command_task_id: cmd-1\n$ git diff -- src/CleanApp.Core/CleanApp.Core.csproj\n",
                    Success: true,
                    ResultLength: 900,
                    Metadata: new JObject
                    {
                        ["Stdout"] = """
                            diff --git a/src/CleanApp.Core/CleanApp.Core.csproj b/src/CleanApp.Core/CleanApp.Core.csproj
                            index 663a28d..395b46f 100644
                            --- a/src/CleanApp.Core/CleanApp.Core.csproj
                            +++ b/src/CleanApp.Core/CleanApp.Core.csproj
                            @@ -7,7 +7,9 @@
                            -    <PackageReference Include="MongoDB.Driver" Version="3.6.0" />
                            +    <PackageReference Include="MongoDB.Driver" Version="3.7.0" />
                            +    <PackageReference Include="SharpCompress" Version="0.49.1" />
                            +    <PackageReference Include="Snappier" Version="1.3.1" />
                            """
                    })
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        var diff = Assert.Single(state.EvidenceLedger.DiffChanges);
        Assert.Equal("src/CleanApp.Core/CleanApp.Core.csproj", diff.FilePath);
        Assert.Equal(["PackageReference MongoDB.Driver 3.6.0"], diff.Removed);
        Assert.Equal(
            [
                "PackageReference MongoDB.Driver 3.7.0",
                "PackageReference SharpCompress 0.49.1",
                "PackageReference Snappier 1.3.1"
            ],
            diff.Added);
        Assert.Equal("exec_cmd", diff.SourceToolName);
    }

    [Fact]
    public void Observe_RecordsSuccessfulVerificationEvidence_FromExecCommands()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();

        processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "exec_cmd",
                    CallId: "restore-1",
                    Result: "✅ command_task_id: restore-1\n$ dotnet restore\n",
                    Success: true,
                    ResultLength: 80,
                    Metadata: new JObject
                    {
                        ["Stdout"] = "All projects are up-to-date for restore.\n"
                    }),
                new ToolExecutionResult(
                    ToolName: "exec_cmd",
                    CallId: "vuln-1",
                    Result: "✅ command_task_id: vuln-1\n$ dotnet list package --vulnerable --include-transitive\n",
                    Success: true,
                    ResultLength: 300,
                    Metadata: new JObject
                    {
                        ["Stdout"] = "The given project `CleanApp.Core` has no vulnerable packages given the current sources.\n"
                    })
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.Collection(
            state.EvidenceLedger.Verifications,
            restore =>
            {
                Assert.Equal("dotnet_restore", restore.Kind);
                Assert.Equal("`dotnet restore` 通过", restore.Summary);
                Assert.Equal("dotnet restore", restore.SourceCommand);
            },
            vulnerable =>
            {
                Assert.Equal("dotnet_vulnerable_clean", vulnerable.Kind);
                Assert.Equal("`dotnet list package --vulnerable --include-transitive` 未报告 vulnerable packages", vulnerable.Summary);
                Assert.Equal("dotnet list package --vulnerable --include-transitive", vulnerable.SourceCommand);
            });
    }

    // RunCommandTool reports a command that exited non-zero as a *successful tool call* ("⚠️ 命令执行完成（… exit code 1）") and only puts the
    // outcome in the metadata. Recording "dotnet build 通过" from the echoed command line therefore turned a failed build into success evidence,
    // which then overrode the completion hook's failure detection.
    [Theory]
    [InlineData("build", 1, "Completed")]
    [InlineData("test", 1, "Completed")]
    [InlineData("restore", 1, "Completed")]
    [InlineData("build", 0, "Failed")]
    [InlineData("build", null, "Completed")]
    public void Observe_DoesNotRecordPassedVerification_WhenTheCommandDidNotExitCleanly(string verb, int? exitCode, string status)
    {
        var state = new QueryRuntimeState();
        var metadata = new JObject { ["Stdout"] = "Build FAILED.\n", ["Status"] = status };
        metadata["ExitCode"] = exitCode.HasValue ? exitCode.Value : JValue.CreateNull();

        DefaultToolObservationProcessor.Instance.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult("exec_cmd", "c1", $"⚠️ 命令执行完成（status {status}, exit code {exitCode?.ToString() ?? "n/a"}）\n$ dotnet {verb}\n",
                    Success: true, Metadata: metadata)
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.Empty(state.EvidenceLedger.Verifications);
    }

    [Fact]
    public void Observe_StillRecordsPassedVerification_WhenTheMetadataSaysExitZero()
    {
        var state = new QueryRuntimeState();

        DefaultToolObservationProcessor.Instance.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult("exec_cmd", "c1", "✅ command_task_id: c1\n$ dotnet build\n", Success: true,
                    Metadata: new JObject { ["Stdout"] = "Build succeeded.\n", ["ExitCode"] = 0, ["Status"] = "Completed" })
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.Equal("dotnet_build", Assert.Single(state.EvidenceLedger.Verifications).Kind);
    }

    [Fact]
    public void Observe_ReadsAFailureFromTheResultText_WhenThereIsNoMetadata()
    {
        var state = new QueryRuntimeState();

        DefaultToolObservationProcessor.Instance.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults = [new ToolExecutionResult("exec_cmd", "c1", "⚠️ 命令执行完成（status Failed, exit code 1）\n$ dotnet build\n", Success: true)],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.Empty(state.EvidenceLedger.Verifications);
    }

    [Fact]
    public void Observe_ForcesStartNextTaskNextRound_AfterSuccessfulPlanProjectionHint()
    {
        var processor = DefaultToolObservationProcessor.Instance;
        var state = new QueryRuntimeState();

        processor.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults =
            [
                new ToolExecutionResult(
                    ToolName: "approve_plan",
                    CallId: "approve-1",
                    Result: "PLAN_APPROVED_AND_PROJECTED",
                    Success: true,
                    ResultLength: 27,
                    SystemHint: "计划已批准并投影为待执行任务。下一步必须调用 start_next_task。",
                    SystemHintDetail: new ToolSystemHint(
                        "计划已批准并投影为待执行任务。下一步必须调用 start_next_task。",
                        RequiredToolName: "start_next_task",
                        ToolCallRequired: true))
            ],
            RuntimeRequest = CreateRequest(),
            State = state
        });

        Assert.Equal("start_next_task", state.RequiredToolNameForNextRound);
        Assert.True(state.ForceAllowToolCallsNextRound);
        var hint = Assert.Single(state.RecoveryHints);
        Assert.Equal("start_next_task", hint.RequiredToolName);
        Assert.True(hint.ToolCallRequired);
    }

    private static QueryRuntimeRequest CreateRequest()
        => new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
            InitialMessages = [new ChatMessage(ChatRole.User, "test")],
            Options = new ChatOptions(),
            MaxRounds = 1
        };

    private static ToolExecutionResult BuildHashlineRead(
        string callId,
        string path,
        string snapshotId,
        string fingerprint)
        => new(
            ToolName: "hs_read",
            CallId: callId,
            Result: $"--- File (Hashline): {path}\nSnapshotId: {snapshotId}\nFingerprint: {fingerprint}",
            Success: true,
            ResultLength: 120,
            Metadata: new JObject
            {
                ["FilePath"] = path,
                ["SnapshotId"] = snapshotId,
                ["FileFingerprint"] = fingerprint,
                ["WindowStartLine"] = 1,
                ["WindowEndLine"] = 20,
                ["TotalLineCount"] = 200
            });
}
