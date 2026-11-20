// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using System.Text.Json;
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
                        ["Stdout"] = " M src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj\n",
                        ["ExitCode"] = 0
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
                        ["ExitCode"] = 0,
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
                        ["Stdout"] = "All projects are up-to-date for restore.\n",
                        ["ExitCode"] = 0
                    }),
                new ToolExecutionResult(
                    ToolName: "exec_cmd",
                    CallId: "vuln-1",
                    Result: "✅ command_task_id: vuln-1\n$ dotnet list package --vulnerable --include-transitive\n",
                    Success: true,
                    ResultLength: 300,
                    Metadata: new JObject
                    {
                        ["Stdout"] = "The given project `CleanApp.Core` has no vulnerable packages given the current sources.\n",
                        ["ExitCode"] = 0
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
    public void Observe_ReplacesVerificationAfterKnownFailureAndLaterPass()
    {
        var state = new QueryRuntimeState();
        ObserveRound(state, Command("dotnet build", new JObject { ["ExitCode"] = 0, ["Status"] = "Completed" }));
        Assert.Equal("dotnet_build", Assert.Single(state.EvidenceLedger.Verifications).Kind);

        ObserveRound(state, Command("dotnet build", ParseJson("""{"EXIT_CODE":1,"STATUS":"Completed"}""")));
        Assert.Empty(state.EvidenceLedger.Verifications);

        ObserveRound(state, Command("dotnet build", ParseJson("""{"EXIT_CODE":" 0 ","STATUS":"Completed"}""")));
        Assert.Equal("dotnet_build", Assert.Single(state.EvidenceLedger.Verifications).Kind);
    }

    [Theory]
    [InlineData("stdout-only")]
    [InlineData("null-exit")]
    [InlineData("empty-exit")]
    [InlineData("overflow-exit")]
    [InlineData("malformed-status")]
    [InlineData("unsupported-object")]
    [InlineData("disposed-json")]
    [InlineData("undefined-json")]
    public void Observe_UnknownVerificationRemovesStalePassWithoutReportingFailure(string scenario)
    {
        var state = new QueryRuntimeState();
        ObserveRound(state, Command("dotnet build", new JObject { ["ExitCode"] = 0 }));
        Assert.Single(state.EvidenceLedger.Verifications);

        object metadata = scenario switch
        {
            "stdout-only" => new JObject { ["Stdout"] = "Build succeeded." },
            "null-exit" => ParseJson("""{"exitCode":null}"""),
            "empty-exit" => ParseJson("""{"exitCode":""}"""),
            "overflow-exit" => ParseJson("""{"exitCode":9223372036854775808}"""),
            "malformed-status" => ParseJson("""{"exitCode":0,"status":[]}"""),
            "unsupported-object" => new { ExitCode = 0, Status = "Completed" },
            "disposed-json" => ReadDisposedObject(),
            "undefined-json" => default(JsonElement),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        ObserveRound(state, Command("dotnet build", metadata, "Build succeeded.\n"));

        Assert.Empty(state.EvidenceLedger.Verifications);
        Assert.Empty(state.EvidenceLedger.FailedVerifications);
    }

    [Fact]
    public void Observe_VulnerabilityFindingInReceiptInvalidatesPriorCleanEvidence()
    {
        var state = new QueryRuntimeState();
        const string listing = "dotnet list package --vulnerable --include-transitive";
        var metadata = new JObject { ["ExitCode"] = 0, ["Stdout"] = "Project has no vulnerable packages." };
        ObserveRound(state, Command(listing, metadata));
        Assert.Equal("dotnet_vulnerable_clean", Assert.Single(state.EvidenceLedger.Verifications).Kind);

        ObserveRound(state, Command(listing, metadata, "stderr: NU1903 a dependency has a known high severity vulnerability.\n"));

        Assert.Empty(state.EvidenceLedger.Verifications);
    }

    [Theory]
    [InlineData("git status --short")]
    [InlineData("git diff")]
    public void Observe_GitUpdatesOnlyFromPassedParseableOrEmptyOutput(string command)
    {
        var state = new QueryRuntimeState();
        var status = command == "git status --short";
        int SnapshotCount() => status ? state.EvidenceLedger.ChangedFiles.Count : state.EvidenceLedger.DiffChanges.Count;

        var initialOutput = status ? " M src/project.csproj\n" : """
            diff --git a/src/project.csproj b/src/project.csproj
            +    <PackageReference Include="Sample" Version="1.2.3" />
            """;
        ObserveRound(state, Command(command, new JObject { ["ExitCode"] = 0, ["Stdout"] = initialOutput }));
        Assert.Equal(1, SnapshotCount());

        ObserveRound(state, Command(command, new JObject { ["ExitCode"] = 1, ["Stdout"] = "" }));
        Assert.Equal(1, SnapshotCount());
        ObserveRound(state, Command(command, new JObject { ["Stdout"] = "" }));
        Assert.Equal(1, SnapshotCount());
        ObserveRound(state, Command(command, ParseJson("""{"ExitCode":0,"Stdout":null}""")));
        Assert.Equal(1, SnapshotCount());
        ObserveRound(state, Command(command, ParseJson("""{"ExitCode":0,"Stdout":42}""")));
        Assert.Equal(1, SnapshotCount());
        ObserveRound(state, Command(command, new JObject { ["ExitCode"] = 0, ["Stdout"] = "not Git output" }));
        Assert.Equal(1, SnapshotCount());
        ObserveRound(state, Command($"echo '$ {command}'", new JObject { ["ExitCode"] = 0, ["Stdout"] = "" }));
        Assert.Equal(1, SnapshotCount());
        if (!status)
        {
            ObserveRound(state, Command("git diff --cached --check", new JObject { ["ExitCode"] = 0, ["Stdout"] = "" }));
            Assert.Equal(1, SnapshotCount());
        }

        ObserveRound(state, Command(command, ParseJson("""{"exit_code":"0","stdout":""}""")));
        Assert.Equal(0, SnapshotCount());
    }

    [Fact]
    public void Observe_UsesJsonReadAliasesAndKeepsFingerprintAndRangeKeysDistinct()
    {
        var state = new QueryRuntimeState();
        var result = BuildHashlineRead("read", "fixtures.txt", "legacy", "a");
        var first = ObserveRound(state, result with
        {
            Metadata = ParseJson("""{"PATH":"ignored","filePATH":"fixtures.txt","snapshotID":"2024-05-10T12:00:00Z","fileFingerprint":" ","fingerPRINT":"a","startLine":"1","endLINE":20,"lines":100}""")
        });
        var file = Assert.Single(first.UpdatedLedger.Files);
        Assert.Equal("2024-05-10T12:00:00Z", file.SnapshotId);
        Assert.Equal("a", file.FileFingerprint);
        Assert.Equal(1, file.WindowStartLine);
        Assert.Equal(20, file.WindowEndLine);
        Assert.Equal(100, file.TotalLineCount);

        var sameFingerprint = ObserveRound(state, result with
        {
            Metadata = ParseJson("""{"filePath":"fixtures.txt","fingerprint":"a","startLine":21,"endLine":40}""")
        });
        Assert.True(sameFingerprint.HasRepeatedReadEvidence);
        var firstRange = result with { Metadata = ParseJson("""{"filePath":"fixtures.txt","startLine":"1","endLine":20}""") };
        Assert.False(ObserveRound(state, firstRange).HasRepeatedReadEvidence);
        Assert.False(ObserveRound(state, result with
        {
            Metadata = ParseJson("""{"filePath":"fixtures.txt","startLine":21,"endLine":40}""")
        }).HasRepeatedReadEvidence);
        Assert.True(ObserveRound(state, firstRange).HasRepeatedReadEvidence);
    }

    [Fact]
    public void ObservationMetadata_DistinguishesAbsentNullEmptyAndMalformedValues()
    {
        Assert.False(ToolObservationMetadata.From(null).IsProvided);
        foreach (var malformed in new object[] { new { ExitCode = 0 }, ReadDisposedObject(), default(JsonElement), ParseJson("[]") })
        {
            var unreadable = ToolObservationMetadata.From(malformed);
            Assert.True(unreadable.IsProvided);
            Assert.False(unreadable.IsReadable);
        }

        var metadata = ToolObservationMetadata.From(ParseJson("""{"stdout":"","status":null,"exitCode":20000000000,"exit_code":"-2"}"""));
        Assert.True(metadata.IsReadable);
        Assert.True(metadata.HasField("Status"));
        Assert.False(metadata.HasNonNullValue("Status"));
        Assert.False(metadata.HasField("missing"));
        Assert.Equal("", metadata.GetString("Stdout"));
        Assert.True(metadata.HasNonNullValue("Stdout"));
        Assert.Null(metadata.GetString("exitCode"));
        Assert.Null(metadata.GetInt("exitCode"));
        Assert.Equal(-2, metadata.GetInt("exitCode", "exit_code"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Observe_ReadsDuplicateGuardHintFromSupportedMetadata(bool useJson)
    {
        var state = new QueryRuntimeState();
        var reason = DefaultToolObservationProcessor.DuplicateHashlineReadReasonCode;
        object metadata = useJson
            ? ParseJson($$"""{"reasonCODE":"{{reason}}"}""")
            : new { ReasonCode = reason };
        var result = new ToolExecutionResult(
            "hs_read", "refused", "duplicate read", false, Metadata: metadata,
            SystemHintDetail: new ToolSystemHint("Use the existing snapshot.", "hs_write", ToolCallRequired: true));

        ObserveRound(state, result);

        Assert.Equal(useJson ? "hs_write" : null, state.RequiredToolNameForNextRound);
        Assert.Equal(useJson, state.ForceAllowToolCallsNextRound);
        Assert.Empty(state.EvidenceLedger.Files);
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

    private static ToolObservationResult ObserveRound(QueryRuntimeState state, ToolExecutionResult result)
        => DefaultToolObservationProcessor.Instance.Observe(new ToolObservationRequest
        {
            ToolCalls = [],
            ToolResults = [result],
            RuntimeRequest = CreateRequest(),
            State = state
        });

    private static ToolExecutionResult Command(string command, object? metadata, string body = "")
        => new("exec_cmd", "fixture", $"✅ command_task_id: fixture\n$ {command}\n{body}", true, Metadata: metadata);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ReadDisposedObject()
    {
        using var document = JsonDocument.Parse("""{"exitCode":0}""");
        return document.RootElement;
    }

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
