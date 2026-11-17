// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using QreProtocol = CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using QreV2 = CodexFlow.QueryRuntime.Engine.V2;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class QreV2BackedQueryRuntimeEngineTests
{
    [Fact]
    public async Task IsolatedCommand_ReturnsExecutionEvidenceThroughRuntime()
    {
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("command", "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "build" } })]),
            TextStep("done"));
        var sink = new RecordingEventSink();
        await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new AllowingInterventionHook()
        }, sink);
        Assert.Single(backend.Requests);
        Assert.Contains(sink.Events, e => e is ToolExecutionCompletedEvent { Success: true });
        Assert.Contains("fake-output", JsonSerializer.Serialize(llm.Requests.Last().Messages));
    }

    // Live run (local vLLM, bugfix-paging): one dropped connection ended the whole turn as a non-retryable model_stream_failed.
    [Fact]
    public async Task AConnectionThatFailsBeforeAnyAnswer_IsSentAgain_AndTheTurnCompletes()
    {
        var llm = new ScriptedLlmExecutor(
            new Func<LLMExecutionRequest, ChatResponseUpdate>(_ => throw new HttpRequestException("An error occurred while sending the request.")),
            TextStep("done after retry"));
        var sink = new RecordingEventSink();

        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest(), sink);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("done after retry", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("denied")]
    [InlineData("exit1")]
    [InlineData("hookblock")]
    [InlineData("resultrejected")]
    public async Task IsolatedCommand_Failure_IsVisibleToTheModel_NotAnEmptyToolResult(string mode)
    {
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        if (mode == "unavailable") backend.OpenDecision = CodexFlow.Core.Execution.ExecutionDecision.Unavailable("IsolationUnavailable", "docker-daemon-down-marker");
        if (mode == "denied") backend.OpenDecision = CodexFlow.Core.Execution.ExecutionDecision.Deny("InvalidTrustedContext", "denied-marker");
        if (mode == "exit1")
            backend.OnRun = (req, _) => { req.OnOutput?.Invoke(new CodexFlow.Core.Execution.CommandOutputLine("stdout", "build-error-marker CS1002")); return Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(CodexFlow.Core.Execution.ExecutionDecision.Allow(),
                CodexFlow.Core.Execution.IsolatedCommandStatus.Failed, 1, "build-error-marker CS1002", "build-error-marker CS1002", false, TimeSpan.Zero, "fake")); };
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("command", "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "build" } })]),
            TextStep("done"));
        await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw],
            InterventionHook = mode is "hookblock" or "resultrejected" ? new RejectingInterventionHook(mode) : new AllowingInterventionHook()
        }, new RecordingEventSink());

        var toolMessages = llm.Requests.Last().Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        var toolText = string.Join(" | ", toolMessages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.Result?.ToString() ?? string.Empty));
        Assert.False(string.IsNullOrWhiteSpace(toolText), "the model must never receive an empty tool_result for a failed command");
        var marker = mode switch { "unavailable" => "docker-daemon-down-marker", "denied" => "denied-marker", "hookblock" => "scope-block-marker", "resultrejected" => "rejected-marker", _ => "build-error-marker" };
        Assert.True(toolText.Contains(marker, StringComparison.Ordinal), "tool text was: " + string.Join(" ", toolText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    // In production the stop hook used to be handed an EMPTY evidence ledger, so it could judge a worker only by what the worker wrote about itself.
    // Verification evidence now comes from the real tool results of the turn, and a command that exited non-zero is not a command that passed.
    [Theory]
    [InlineData(false, "dotnet_build")]
    [InlineData(true, "")]
    public async Task TheStopHookIsHandedVerificationEvidenceFromTheRealToolResults(bool exitsNonZero, string expectedKind)
    {
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        if (exitsNonZero)
        {
            backend.OnRun = (req, _) => Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(
                CodexFlow.Core.Execution.ExecutionDecision.Allow(), CodexFlow.Core.Execution.IsolatedCommandStatus.Failed, 1,
                "error CS1002", "error CS1002", false, TimeSpan.Zero, "fake"));
        }

        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("command", "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "build" } })]),
            TextStep("done"));
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var kinds = dispatcher.StopCalls.Last().EvidenceLedger.Verifications.Select(static verification => verification.Kind).ToArray();
        Assert.Equal(expectedKind.Length == 0 ? [] : new[] { expectedKind }, kinds);
    }

    // Success evidence is the LATEST outcome: a build that passed and was then broken must not still count as verified.
    [Theory]
    [InlineData("pass,fail", "")]
    [InlineData("fail,pass", "dotnet_build")]
    [InlineData("pass,pass", "dotnet_build")]
    public async Task TheLatestOutcomeOfAVerificationCommandDecidesTheEvidence(string outcomes, string expectedKind)
    {
        var sequence = outcomes.Split(',');
        var call = 0;
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        backend.OnRun = (req, _) =>
        {
            var passes = sequence[call++] == "pass";
            return Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(
                CodexFlow.Core.Execution.ExecutionDecision.Allow(),
                passes ? CodexFlow.Core.Execution.IsolatedCommandStatus.Completed : CodexFlow.Core.Execution.IsolatedCommandStatus.Failed,
                passes ? 0 : 1, passes ? "Build succeeded." : "error CS1002", "", false, TimeSpan.Zero, "fake"));
        };
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        ChatResponseUpdate Build(string id) => new(ChatRole.Assistant,
            [new FunctionCallContent(id, "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "build" } })]);
        var llm = new ScriptedLlmExecutor(Build("b1"), Build("b2"), TextStep("done"));
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var kinds = dispatcher.StopCalls.Last().EvidenceLedger.Verifications.Select(static verification => verification.Kind).Distinct().ToArray();
        Assert.Equal(expectedKind.Length == 0 ? [] : new[] { expectedKind }, kinds);

        // The same outcomes, seen from the failure side: only a latest run that did not exit cleanly is a failed verification.
        var failed = dispatcher.StopCalls.Last().EvidenceLedger.FailedVerifications.Select(static verification => verification.Kind).ToArray();
        Assert.Equal(expectedKind.Length == 0 ? new[] { "dotnet_build" } : [], failed);
    }

    // A model answer with no content changed nothing, so it is asked once more; a second empty answer is not hidden.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task AnEmptyModelAnswer_IsAskedOnceMore_AndOnlyOnce(int emptyAnswers, bool recovers)
    {
        var steps = new List<object>();
        for (var i = 0; i < emptyAnswers; i++)
        {
            steps.Add((IReadOnlyList<ChatResponseUpdate>)new List<ChatResponseUpdate>());
        }

        steps.Add(TextStep("answer"));
        var llm = new ScriptedLlmExecutor(steps.ToArray());

        var result = await CreateEngine(llm, new ScriptedHookDispatcher(BeforeStopHookResult.None)).ExecuteAsync(CreateRequest(), new RecordingEventSink());

        Assert.Equal(recovers ? emptyAnswers + 1 : 2, llm.Requests.Count);
        Assert.Equal(recovers, result.FinalText?.Contains("answer", StringComparison.Ordinal) == true);
    }

    // The failure of a restore or build is classified by the NuGet diagnostic code in the tool's own output: a package missing from the offline
    // source (NU1101/NU1102/NU1103/NU1301/NU1900) is an environment limit; a compile error is not.
    [Theory]
    [InlineData("error NU1102: Unable to find package OpenTelemetry.Api with version (>= 1.15.3)", true)]
    [InlineData("error NU1101: Unable to find package Foo. No packages exist with this id in source(s): offline", true)]
    [InlineData("error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json", true)]
    [InlineData("error CS1002: ; expected", false)]
    public async Task AFailedRestoreOrBuild_IsClassifiedByItsNuGetDiagnosticCode(string output, bool environmentLimit)
    {
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        backend.OnRun = (req, _) =>
        {
            req.OnOutput?.Invoke(new CodexFlow.Core.Execution.CommandOutputLine("stdout", output));
            return Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(
                CodexFlow.Core.Execution.ExecutionDecision.Allow(), CodexFlow.Core.Execution.IsolatedCommandStatus.Failed, 1,
                output, output, false, TimeSpan.Zero, "fake"));
        };
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "restore" } })]),
            TextStep("done"));
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var failed = Assert.Single(dispatcher.StopCalls.Last().EvidenceLedger.FailedVerifications);
        Assert.Equal("dotnet_restore", failed.Kind);
        Assert.Equal(environmentLimit, failed.EnvironmentLimit);
    }

    // After a restore that failed for the environment, the build and test commands fail with NETSDK1004 (no assets file): a consequence of that
    // restore, not a second problem for the worker to fix. Without an environment-limited restore before it, the same message is a real failure.
    [Theory]
    [InlineData("restore-env,build-assets", true)]
    [InlineData("restore-env,build-assets,test-assets", true)]
    [InlineData("build-assets", false)]
    [InlineData("restore-env,restore-ok,build-assets", false)]
    public async Task AMissingAssetsFile_IsAConsequenceOnlyOfAnEnvironmentLimitedRestore(string steps, bool buildIsEnvironmentLimit)
    {
        var sequence = steps.Split(',');
        var call = 0;
        string Output(string step) => step switch
        {
            "restore-env" => "error NU1102: Unable to find package OpenTelemetry.Api with version (>= 1.15.3)",
            "restore-ok" => "Restored.",
            _ => "error NETSDK1004: Assets file '/w/obj/project.assets.json' not found. Run a NuGet package restore to generate this file.",
        };
        string[] Command(string step) => step.StartsWith("restore", StringComparison.Ordinal) ? ["dotnet", "restore"] : step.StartsWith("build", StringComparison.Ordinal) ? ["dotnet", "build"] : ["dotnet", "test"];
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        backend.OnRun = (req, _) =>
        {
            var step = sequence[call++];
            var ok = step == "restore-ok";
            req.OnOutput?.Invoke(new CodexFlow.Core.Execution.CommandOutputLine("stdout", Output(step)));
            return Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(
                CodexFlow.Core.Execution.ExecutionDecision.Allow(),
                ok ? CodexFlow.Core.Execution.IsolatedCommandStatus.Completed : CodexFlow.Core.Execution.IsolatedCommandStatus.Failed,
                ok ? 0 : 1, Output(step), "", false, TimeSpan.Zero, "fake"));
        };
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var scripted = sequence.Select((step, i) => (object)new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("c" + i, "exec_cmd", new Dictionary<string, object?> { ["command"] = Command(step) })])).ToList();
        scripted.Add(TextStep("done"));
        var llm = new ScriptedLlmExecutor(scripted.ToArray());
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var failed = dispatcher.StopCalls.Last().EvidenceLedger.FailedVerifications;
        var build = Assert.Single(failed, f => f.Kind == "dotnet_build");
        Assert.Equal(buildIsEnvironmentLimit, build.EnvironmentLimit);
    }

    // dryRun is read from the arguments of the call (not from the result's wording), and any later real write clears the preview.
    [Theory]
    [InlineData("true", 1)]
    [InlineData("true,false", 0)]
    [InlineData("false", 0)]
    [InlineData("true,true", 2)]
    public async Task APreviewedWrite_StaysUnapplied_UntilARealWriteFollows(string dryRuns, int unappliedAtStop)
    {
        var flags = dryRuns.Split(',');
        var scripted = flags.Select((flag, i) => (object)new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("w" + i, "hs_write", new Dictionary<string, object?> { ["dryRun"] = flag == "true" })])).ToList();
        scripted.Add(TextStep("done"));
        var llm = new ScriptedLlmExecutor(scripted.ToArray());
        var tool = AIFunctionFactory.Create((bool dryRun) => dryRun ? "validated (DryRun)" : "applied", new AIFunctionFactoryOptions { Name = "hs_write", Description = "Writes." });
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        Assert.Equal(unappliedAtStop, dispatcher.StopCalls.Last().EvidenceLedger.UnappliedPreviews.Count);
    }

    // The worker's own statement is read from the ARGUMENTS of its report_task_status call (never from its prose), reaches the stop hook's ledger
    // and the runtime result, and the latest call wins.
    [Fact]
    public async Task TheWorkersStatusReport_IsEvidence_AndReachesTheResult()
    {
        var scripted = new object[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("s1", WorkerStatusTool.Name,
                new Dictionary<string, object?> { ["status"] = "completed" })]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("s2", WorkerStatusTool.Name,
                new Dictionary<string, object?> { ["status"] = "blocked", ["reason"] = "清单缺失", ["unfinished"] = new[] { "Snappier", "MessagePack" } })]),
            TextStep("T1 部分完成")
        };
        var llm = new ScriptedLlmExecutor(scripted);
        var tool = AIFunctionFactory.Create((string status, string? reason = null, string[]? unfinished = null) => "recorded",
            new AIFunctionFactoryOptions { Name = WorkerStatusTool.Name, Description = "Reports status." });
        var dispatcher = new ScriptedHookDispatcher(BeforeStopHookResult.None);

        var result = await CreateEngine(llm, dispatcher).ExecuteAsync(CreateRequest() with
        {
            EntryPoint = QueryLoopEntryPoint.ForgeWorker, AvailableTools = [tool], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var report = dispatcher.StopCalls.Last().EvidenceLedger.WorkerReport;
        Assert.NotNull(report);
        Assert.True(report.IsBlocked);
        Assert.Equal("清单缺失", report.Reason);
        Assert.Equal(["Snappier", "MessagePack"], report.Unfinished);
        Assert.Equal(report, result.WorkerReport);
    }

    // Live run (dependency-upgrade-out-of-scope on the merged host): told that its unfinished list meant blocked, the worker reported again with
    // the same items moved to `unverified`, and the task was accepted. Only a write made after the blocked report can change it.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ABlockedReport_IsNotRewordedIntoCompleted_UnlessAWriteFollowsIt(bool writesBetween, bool staysBlocked)
    {
        var scripted = new List<object>
        {
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("s1", WorkerStatusTool.Name,
                new Dictionary<string, object?> { ["status"] = "completed", ["unfinished"] = new[] { "Instrumentation 对齐到 1.15.3" } })])
        };
        if (writesBetween)
        {
            scripted.Add(new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("w1", "write_file",
                new Dictionary<string, object?> { ["path"] = "a.csproj", ["content"] = "x" })]));
        }

        scripted.Add(new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("s2", WorkerStatusTool.Name,
            new Dictionary<string, object?> { ["status"] = "completed", ["unverified"] = new[] { "Instrumentation 对齐到 1.15.3" } })]));
        scripted.Add(TextStep("done"));
        var status = AIFunctionFactory.Create((string status, string[]? unfinished = null, string[]? unverified = null) => "recorded",
            new AIFunctionFactoryOptions { Name = WorkerStatusTool.Name, Description = "Reports status." });
        var write = AIFunctionFactory.Create((string path, string content) => "written",
            new AIFunctionFactoryOptions { Name = "write_file", Description = "Writes a file." });
        Assert.True(CodexFlow.Core.Agents.ToolClassification.IsWriteTool("write_file"));

        var result = await CreateEngine(new ScriptedLlmExecutor(scripted.ToArray()), new ScriptedHookDispatcher(BeforeStopHookResult.None))
            .ExecuteAsync(CreateRequest() with
            {
                EntryPoint = QueryLoopEntryPoint.ForgeWorker, AvailableTools = [status, write], InterventionHook = new AllowingInterventionHook()
            }, new RecordingEventSink());

        Assert.NotNull(result.WorkerReport);
        Assert.Equal(staysBlocked, result.WorkerReport.IsBlocked);
    }

    [Fact]
    public async Task WithoutAStatusCall_TheResultHasNoWorkerReport()
    {
        var llm = new ScriptedLlmExecutor([TextStep("done")]);
        var result = await CreateEngine(llm, new ScriptedHookDispatcher(BeforeStopHookResult.None)).ExecuteAsync(CreateRequest() with
        {
            EntryPoint = QueryLoopEntryPoint.ForgeWorker
        }, new RecordingEventSink());

        Assert.Null(result.WorkerReport);
    }

    // A weaker model read for 38-60 calls without ever writing. The count of calls since the last REAL write (a dryRun preview is not one) is
    // evidence in the ledger; a forge worker is told at 10 and made to write at 16.
    [Theory]
    [InlineData("read*9", false, false)]
    [InlineData("read*10", true, false)]
    [InlineData("read*16", true, true)]
    [InlineData("read*12,dry,read*4", true, true)]       // a preview does not reset the count
    [InlineData("read*12,write,read*4", false, false)]   // a real write does
    public async Task AForgeWorkerThatOnlyReads_IsToldToWrite_ThenMadeTo(string script, bool expectNudge, bool expectForced)
    {
        var steps = new List<string>();
        foreach (var part in script.Split(','))
        {
            var pieces = part.Split('*');
            for (var i = 0; i < (pieces.Length > 1 ? int.Parse(pieces[1]) : 1); i++) steps.Add(pieces[0]);
        }
        var scripted = steps.Select((step, i) => (object)new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("c" + i, step == "read" ? "read_thing" : "hs_write",
                step == "read" ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["dryRun"] = step == "dry" })])).ToList();
        scripted.Add(TextStep("done"));
        var llm = new ScriptedLlmExecutor(scripted.ToArray());
        var read = AIFunctionFactory.Create(() => "content", new AIFunctionFactoryOptions { Name = "read_thing", Description = "Reads." });
        var write = AIFunctionFactory.Create((bool dryRun) => dryRun ? "validated (DryRun)" : "applied", new AIFunctionFactoryOptions { Name = "hs_write", Description = "Writes." });

        await CreateEngine(llm, new ScriptedHookDispatcher(BeforeStopHookResult.None)).ExecuteAsync(CreateRequest() with
        {
            EntryPoint = QueryLoopEntryPoint.ForgeWorker, MaxRounds = 40,
            AvailableTools = [read, write], InterventionHook = new AllowingInterventionHook()
        }, new RecordingEventSink());

        var last = llm.Requests.Last();
        Assert.Equal(expectNudge, last.Messages.Any(m => m.Text.Contains("hs_write", StringComparison.Ordinal) && m.Text.Contains("只读", StringComparison.Ordinal)));
        Assert.Equal(expectForced, last.Options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: "hs_write" });
    }

    [Fact]
    public async Task PolicyDenial_IsLoggedStructured_WithSourceAndReason_ButNeverTheModelArguments()
    {
        var backend = new CodexFlow.Core.Tests.Execution.FakeIsolationService();
        var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
        using var bound = accessor.Begin(new CodexFlow.Core.Execution.TrustedExecutionContext
        {
            UserId = "user", SessionId = "qre-v2-c5-session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        });
        var raw = new CodexFlow.Core.Agents.Tools.RunCommandTool(NullLogger<CodexFlow.Core.Agents.Tools.RunCommandTool>.Instance,
            isolationGate: new(backend, accessor, new()));
        var tool = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(raw);
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("command", "exec_cmd", new Dictionary<string, object?> { ["command"] = new[] { "echo", "secret-arg-marker-token-0123456789" } })]),
            TextStep("done"));
        var logger = new CapturingLogger<QreV2BackedQueryRuntimeEngine>();
        await CreateEngine(llm, logger: logger).ExecuteAsync(CreateRequest() with
        {
            AvailableTools = [tool], AvailableCodexTools = [raw], InterventionHook = new RejectingInterventionHook("hookblock")
        }, new RecordingEventSink());

        var entry = Assert.Single(logger.Entries, e => e.Contains("[ToolPolicyDenied]", StringComparison.Ordinal));
        Assert.Contains("Tool=exec_cmd", entry);
        Assert.Contains("Source=InterventionHook", entry);
        Assert.Contains("scope-block-marker", entry);
        Assert.Contains("SessionId=", entry);
        Assert.DoesNotContain("secret-arg-marker", string.Join("\n", logger.Entries));
        Assert.Empty(backend.Requests); // refused before any isolated command
    }

    [Fact]
    public void ToolResultModelText_NeverReturnsEmpty_AndKeepsRealOutputUntouched()
    {
        var id = new QreProtocol.RuntimeInvocationId("call-1");
        QreProtocol.RuntimeToolResult R(string? text, bool ok, QreProtocol.RuntimeError? error = null, QreProtocol.RuntimeToolResultDetails? details = null)
            => new(id, text, ok, error, Details: details);
        var err = (string code, string msg) => new QreProtocol.RuntimeError(QreProtocol.RuntimeErrorCategory.ToolFailed, code, msg);

        Assert.Equal("real output", QreV2BackedQueryRuntimeEngine.ToolResultModelText(R("real output", true)));
        Assert.Equal("  exact  ", QreV2BackedQueryRuntimeEngine.ToolResultModelText(R("  exact  ", true)));
        Assert.Contains("denied-reason", QreV2BackedQueryRuntimeEngine.ToolResultModelText(R(null, false, err("deny", "denied-reason"))));
        Assert.Contains("stderr-reason", QreV2BackedQueryRuntimeEngine.ToolResultModelText(R("", false, null,
            new QreProtocol.RuntimeToolResultDetails(QreProtocol.RuntimeToolOutcome.Failed, StandardError: "stderr-reason"))));
        // Error without a message: code is still surfaced; nothing at all: explicit non-empty fallback.
        var codeOnly = QreV2BackedQueryRuntimeEngine.ToolResultModelText(R(null, false, err("only_code", "  ")));
        Assert.Contains("only_code", codeOnly);
        foreach (var nothing in new[]
        {
            R(null, false), R(null, true), R("", true), R("   ", false),
            R(null, false, null, new QreProtocol.RuntimeToolResultDetails(QreProtocol.RuntimeToolOutcome.Failed))
        })
        {
            var text = QreV2BackedQueryRuntimeEngine.ToolResultModelText(nothing);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("no output", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SafeLogText_StripsControlCharactersAndBoundsLength()
    {
        Assert.Equal("a b c", QreV2BackedQueryRuntimeEngine.SafeLogText("a\r\nb\tc", 50));
        Assert.Equal(303, QreV2BackedQueryRuntimeEngine.SafeLogText(new string('x', 1000), 300).Length);
        Assert.Equal(string.Empty, QreV2BackedQueryRuntimeEngine.SafeLogText(null, 10));
    }

    // A worker that ends with "N tool calls and no change" has to be diagnosable afterwards: one log line per call says which tool ran and, for a
    // failure, how it failed. A successful call's output never reaches the log.
    // A failed worker can only be understood from what the model saw and said. With AuditDirectory set the run is recorded (private diagnostic
    // mode: prompts, the model's output, tool results) so `qre replay` can show it; without it nothing is written.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuditDirectory_RecordsTheRun_OnlyWhenConfigured(bool configured)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var llm = new ScriptedLlmExecutor(TextStep("recorded-answer-marker"));
            var options = Microsoft.Extensions.Options.Options.Create(new QueryRuntimeBackendOptions { AuditDirectory = configured ? directory : null });
            var engine = new QreV2BackedQueryRuntimeEngine(
                llm, contextWindowManager: null, telemetry: null, NullLogger<QreV2BackedQueryRuntimeEngine>.Instance, backendOptions: options);

            await engine.ExecuteAsync(CreateRequest(), new RecordingEventSink());

            var files = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                : [];
            if (!configured)
            {
                Assert.Empty(files);
                return;
            }

            Assert.NotEmpty(files);
            Assert.Contains(files, file => file.Contains(Path.Combine(".qre", "v2", "private", "runs"), StringComparison.Ordinal));
            Assert.Contains(files, file => File.ReadAllText(file).Contains("recorded-answer-marker", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ToolOutcome_IsLogged_WithFailureDetailOnly()
    {
        var logger = new CapturingLogger<QreV2BackedQueryRuntimeEngine>();
        var request = CreateRequest();

        QreV2BackedQueryRuntimeEngine.LogToolOutcome(logger, request, new ToolExecutionResult("hs_write", "c1", "SECRET-OUTPUT", true));
        QreV2BackedQueryRuntimeEngine.LogToolOutcome(logger, request, new ToolExecutionResult("hs_write", "c2", "anchor mismatch:\r\nline 3 " + new string('x', 600), false));

        var ok = Assert.Single(logger.Entries, e => e.Contains("Success=true", StringComparison.Ordinal));
        Assert.Contains("Tool=hs_write", ok, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-OUTPUT", ok, StringComparison.Ordinal);
        var failed = Assert.Single(logger.Entries, e => e.Contains("Success=false", StringComparison.Ordinal));
        Assert.Contains("anchor mismatch: line 3", failed, StringComparison.Ordinal);
        Assert.True(failed.Length < 500, "the failure detail is bounded");
    }

    [Fact]
    public async Task FrozenCatalog_DeferredActivation_ExposesOnlyCurrentSubsetAndExecutesAfterSearch()
    {
        var activated = false;
        var reads = 0;
        var search = AIFunctionFactory.Create(() => { activated = true; return "activated"; }, "tool_search", "Activate read.");
        var read = AIFunctionFactory.Create(() => { reads++; return "read"; }, "read_deferred", "Read deferred.");
        var llm = new ScriptedLlmExecutor(
            InspectingStep(r => Assert.Single(r.Options!.Tools!),
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("search", "tool_search", new Dictionary<string, object?>())])),
            InspectingStep(r => Assert.Equal(2, r.Options!.Tools!.Count),
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("read", "read_deferred", new Dictionary<string, object?>())])),
            TextStep("verified"));
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            FrozenToolCatalog = [search, read], AvailableTools = [search],
            AvailableToolsProvider = () => activated ? [search, read] : [search]
        }, new RecordingEventSink());
        Assert.Equal("verified", result.FinalText);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task FrozenCatalog_HiddenInvocation_IsNotExecuted()
    {
        var reads = 0;
        var visible = AIFunctionFactory.Create(() => "visible", "read_visible", "Read.");
        var hidden = AIFunctionFactory.Create(() => { reads++; return "hidden"; }, "read_hidden", "Hidden.");
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("hidden", hidden.Name, new Dictionary<string, object?>())]),
            TextStep("blocked"));
        await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            FrozenToolCatalog = [visible, hidden], AvailableToolsProvider = () => [visible]
        }, new RecordingEventSink());
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task FrozenCatalog_ChangedSchema_FailsBeforeModel()
    {
        var frozen = AIFunctionFactory.Create((string path) => path, "read", "Read.");
        var changed = AIFunctionFactory.Create((int path) => path, "read", "Read.");
        var llm = new ScriptedLlmExecutor();
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            FrozenToolCatalog = [frozen], AvailableToolsProvider = () => [changed]
        }, new RecordingEventSink());
        Assert.Empty(llm.Requests);
        Assert.Equal(QueryTerminationReason.Exception, result.TerminationReason);
    }

    [Fact]
    public async Task FrozenCatalog_DescriptionChange_IsIgnoredAndToolStillExecutes()
    {
        var reads = 0;
        var frozen = AIFunctionFactory.Create(() => { reads++; return "ok"; }, "read", "Read: next task A.");
        var current = AIFunctionFactory.Create(() => { reads++; return "ok"; }, "read", "Read: next task B (changed in-turn).");
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "read", new Dictionary<string, object?>())]),
            TextStep("done"));
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            FrozenToolCatalog = [frozen], AvailableToolsProvider = () => [current]
        }, new RecordingEventSink());
        Assert.NotEqual(QueryTerminationReason.Exception, result.TerminationReason);
        Assert.Equal("done", result.FinalText);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task FrozenCatalog_NewlyVisibleToolOutsideSnapshot_IsOmittedNotExecutedAndTurnContinues()
    {
        var extraRuns = 0;
        var frozen = AIFunctionFactory.Create(() => "ok", "read", "Read.");
        var extra = AIFunctionFactory.Create(() => { extraRuns++; return "extra"; }, "write_patch", "Newly exposed after a stage change.");
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "write_patch", new Dictionary<string, object?>())]),
            TextStep("deferred to next turn"));
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with
        {
            FrozenToolCatalog = [frozen], AvailableToolsProvider = () => [frozen, extra]
        }, new RecordingEventSink());
        Assert.NotEqual(QueryTerminationReason.Exception, result.TerminationReason);
        Assert.Equal(0, extraRuns);
        Assert.Equal("deferred to next turn", result.FinalText);
        // The model must never have been offered the out-of-snapshot tool.
        Assert.DoesNotContain(llm.Requests, request => (request.Options?.Tools ?? []).Any(tool => tool.Name == "write_patch"));
    }

    [Theory]
    // Arbitrary / unmarked JSON is never reinterpreted, whatever status/output keys it carries.
    [InlineData("""{"status":"failed","output":"x"}""", true)]
    [InlineData("""{"status":"completed","output":"x"}""", true)]
    [InlineData("""{"Status":2,"Output":"x"}""", true)]
    [InlineData("""{"status":false,"output":{"a":1}}""", true)]
    [InlineData("""{"__icodex_tool_result":false,"status":"Failed","output":"x"}""", true)]
    [InlineData("""not json at all""", true)]
    // Explicitly marked envelopes: Success passes; anything else (incl. PartialSuccess/malformed) fails, never throws.
    [InlineData("""{"__icodex_tool_result":true,"status":"Success","output":"x"}""", true)]
    [InlineData("""{"__icodex_tool_result":true,"Status":0,"Output":"x"}""", true)]
    [InlineData("""{"__icodex_tool_result":true,"status":"Failed","output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"Status":2,"Output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":"PartialSuccess","output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":"BlockedByGuardrail","output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":true,"output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":{"a":1},"output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":null,"output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"status":99999999999,"output":"x"}""", false)]
    [InlineData("""{"__icodex_tool_result":true,"output":"x"}""", false)]
    public async Task ToolResultText_OnlyMarkedEnvelopesCanBeBusinessFailures(string toolText, bool expectedSuccess)
    {
        var tool = AIFunctionFactory.Create(() => toolText, "read", "Read.");
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "read", new Dictionary<string, object?>())]),
            TextStep("finished"));
        var sink = new RecordingEventSink();
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with { AvailableTools = [tool] }, sink);

        Assert.Equal("finished", result.FinalText);
        Assert.NotEqual(QueryTerminationReason.Exception, result.TerminationReason);
        Assert.Equal(expectedSuccess, sink.Events.Any(e => e is ToolExecutionCompletedEvent { Success: true }));
        Assert.Equal(!expectedSuccess, sink.Events.Any(e => e is ToolExecutionCompletedEvent { Success: false }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolBusinessFailure_IsNotReportedAsSuccessfulExecution(bool wrapped)
    {
        var tool = wrapped
            ? AIFunctionFactory.Create(() => """{"__icodex_tool_result":true,"status":"Failed","output":"actual failure"}""", "read", "Read.")
            // Typed path: an AIFunction that hands the engine a real CodexToolResult (no JSON round trip).
            : new TypedResultFunction(CodexToolResult.Error("actual failure"));
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("failed", "read", new Dictionary<string, object?>())]),
            TextStep("failed as expected"));
        var sink = new RecordingEventSink();
        await CreateEngine(llm).ExecuteAsync(CreateRequest() with { AvailableTools = [tool] }, sink);
        Assert.DoesNotContain(sink.Events, e => e is ToolExecutionCompletedEvent { Success: true });
        Assert.Contains(sink.Events, e => e is ToolExecutionCompletedEvent { Success: false });
    }

    [Fact]
    public async Task ExecuteAsync_UnconstrainedToolParameter_UsesCompatibleSchemaForModelAndExecution()
    {
        object? received = null;
        var tool = AIFunctionFactory.Create((object operations) =>
        {
            received = operations;
            return "read completed";
        }, "ReadOperations", "Reads an operation payload.");
        var arguments = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""[{"type":"read","path":"fixture.txt"}]""");
        var llm = new ScriptedLlmExecutor(
            InspectingStep(request =>
            {
                var modelTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(request.Options!.Tools!));
                QreV2.RuntimeToolArgumentNormalizer.ValidateSchema(modelTool.JsonSchema);
                Assert.Equal(System.Text.Json.JsonValueKind.Object, modelTool.JsonSchema.GetProperty("properties").GetProperty("operations").ValueKind);
            }, new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-read", tool.Name, new Dictionary<string, object?> { ["operations"] = arguments })])),
            TextStep("verified"));
        var sink = new RecordingEventSink();
        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest() with { AvailableTools = [tool] }, sink);

        Assert.True(!sink.Events.OfType<ErrorEvent>().Any(), string.Join(Environment.NewLine, sink.Events.OfType<ErrorEvent>()));
        Assert.Equal("verified", result.FinalText);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(arguments, System.Text.Json.JsonSerializer.SerializeToElement(received)));
        Assert.Equal(System.Text.Json.JsonValueKind.True, tool.JsonSchema.GetProperty("properties").GetProperty("operations").ValueKind);
        Assert.Contains(sink.Events, runtimeEvent => runtimeEvent is ToolExecutionCompletedEvent { Success: true });
        Assert.Equal("ReadOperations", result.LastFunctionCall);
    }

    [Theory]
    [InlineData("bad/name", "read_other")]
    [InlineData("ReadState", "readstate")]
    public async Task ExecuteAsync_InvalidOrCaseCollidingNames_FailBeforeModel(string firstName, string secondName)
    {
        var llm = new ScriptedLlmExecutor();
        var first = AIFunctionFactory.Create(() => "unused", firstName, "Reads state.");
        var second = AIFunctionFactory.Create(() => "unused", secondName, "Reads other state.");
        var sink = new RecordingEventSink();
        await CreateEngine(llm).ExecuteAsync(CreateRequest() with { AvailableTools = [first, second] }, sink);
        Assert.Empty(llm.Requests);
        Assert.Contains(sink.Events, runtimeEvent => runtimeEvent is ErrorEvent { ErrorType: nameof(ArgumentException) });
    }

    [Fact]
    public async Task ExecuteAsync_NoTools_StreamsTextAndMapsTerminalMetadata()
    {
        var llm = new ScriptedLlmExecutor(TextStep("v2 answer"));
        var sink = new RecordingEventSink();
        var engine = CreateEngine(llm);

        var result = await engine.ExecuteAsync(CreateRequest(), sink, CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("v2 answer", result.FinalText);
        Assert.Equal(1, result.TotalRounds);
        Assert.Equal(0, result.TotalToolCalls);
        Assert.NotNull(result.LastPromptAssemblySnapshot);
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is AssistantDeltaEvent { Delta: "v2 answer" });
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is TerminatedEvent { TotalRounds: 1, TotalToolCalls: 0 });
    }

    [Fact]
    public async Task ExecuteAsync_TrailingUsageAfterFinish_RemainsProtocolValid()
    {
        var llm = new ScriptedLlmExecutor((object)new ChatResponseUpdate[]
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("usage answer")],
                FinishReason = ChatFinishReason.Stop
            },
            new UsageChatResponseUpdate
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 8,
                    OutputTokenCount = 3,
                    TotalTokenCount = 11
                }
            }
        });

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest(),
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal(8, result.TotalPromptTokens);
        Assert.Equal(3, result.TotalCompletionTokens);
    }

    private static ChatResponseUpdate[] AnswerWithUsage(string? text, UsageDetails usage)
        =>
        [
            new()
            {
                Role = ChatRole.Assistant,
                Contents = text == null ? [] : [new TextContent(text)],
                FinishReason = ChatFinishReason.Stop
            },
            new UsageChatResponseUpdate { Usage = usage }
        ];

    [Theory]
    [InlineData("cached_input_tokens")]
    [InlineData("prompt_tokens_details.cached_tokens")]
    [InlineData("cache_read_input_tokens")]
    public async Task ExecuteAsync_ReportsCachedPromptTokens_FromTheProviderCounts(string providerKey)
    {
        var llm = new ScriptedLlmExecutor((object)AnswerWithUsage("cached answer", new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 7,
            TotalTokenCount = 107,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { [providerKey] = 64 }
        }));

        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest(), new RecordingEventSink(), CancellationToken.None);

        Assert.Equal(100, result.TotalPromptTokens);
        Assert.Equal(64, result.TotalCachedPromptTokens);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsCachedPromptTokens_FromTheTypedUsageProperty()
    {
        var llm = new ScriptedLlmExecutor((object)AnswerWithUsage("cached answer", new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 7,
            CachedInputTokenCount = 40
        }));

        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest(), new RecordingEventSink(), CancellationToken.None);

        Assert.Equal(40, result.TotalCachedPromptTokens);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutCacheCounts_LeavesCachedPromptTokensUnknown()
    {
        var llm = new ScriptedLlmExecutor((object)AnswerWithUsage("plain answer", new UsageDetails
        {
            InputTokenCount = 8,
            OutputTokenCount = 3
        }));

        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest(), new RecordingEventSink(), CancellationToken.None);

        Assert.Null(result.TotalCachedPromptTokens);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFirstAttempt_StillCountsTheTokensItSpent()
    {
        // The model returns nothing, is asked once more, then answers. Both requests were billed.
        var llm = new ScriptedLlmExecutor(
            AnswerWithUsage(null, new UsageDetails { InputTokenCount = 50, OutputTokenCount = 2 }),
            AnswerWithUsage("second answer", new UsageDetails { InputTokenCount = 50, OutputTokenCount = 9 }));

        var result = await CreateEngine(llm).ExecuteAsync(CreateRequest(), new RecordingEventSink(), CancellationToken.None);

        Assert.Equal("second answer", result.FinalText);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(100, result.TotalPromptTokens);
        Assert.Equal(11, result.TotalCompletionTokens);
    }

    [Fact]
    public async Task ExecuteAsync_PromptSnapshot_EstimatesChineseTextByBytesNotCharacters()
    {
        var chinese = new string('汉', 400); // 400 characters, 1,200 UTF-8 bytes
        var llm = new ScriptedLlmExecutor(TextStep("ok"));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with { InitialMessages = [new ChatMessage(ChatRole.User, chinese)] },
            new RecordingEventSink(),
            CancellationToken.None);

        // Characters / 4 said 100; the engine's estimator, which the context budget uses, says about 300.
        Assert.InRange(result.LastPromptAssemblySnapshot!.EstimatedPromptTokens, 300, 320);
    }

    [Fact]
    public async Task ExecuteAsync_StopHookExhausted_ExposesTheHookReasonNotJustTheCode()
    {
        // Every answer still reads as unfinished repair work, so the stop hook keeps vetoing until its budget runs out.
        const string unfinished = "测试失败，我需要继续修复。";
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_state", new Dictionary<string, object?>())]),
            TextStep(unfinished), TextStep(unfinished), TextStep(unfinished), TextStep(unfinished), TextStep(unfinished));
        var tool = AIFunctionFactory.Create(() => "ok", "read_state", "Reads state.");
        var dispatcher = new RuntimeHookDispatcher(
            [new IncompleteVerificationStopRuntimeHook()],
            NullLogger<RuntimeHookDispatcher>.Instance);

        var result = await CreateEngine(llm, dispatcher).ExecuteAsync(
            CreateRequest() with { AvailableTools = [tool], MaxRounds = 12 },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminalDetailCodes.RecoveryExhausted, result.TerminalDetailCode);
        Assert.Equal("unfinished_repair_intent", result.TerminalDetailMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ReadonlyRequiredTool_RunsSingleToolAndPreservesMetadata()
    {
        var toolCalls = 0;
        var llm = new ScriptedLlmExecutor(
            InspectingStep(
                request =>
                {
                    var required = Assert.IsType<RequiredChatToolMode>(request.Options?.ToolMode);
                    Assert.Equal("read_state", required.RequiredFunctionName);
                },
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-read", "read_state", new Dictionary<string, object?>
                    {
                        ["path"] = "README.md"
                    })
                ])),
            TextStep("verified readonly answer"));
        var tool = AIFunctionFactory.Create(
            (string path) =>
            {
                toolCalls++;
                return $"read {path}";
            },
            new AIFunctionFactoryOptions
            {
                Name = "read_state",
                Description = "Reads one file state."
            });
        var sink = new RecordingEventSink();
        var engine = CreateEngine(llm);

        var result = await engine.ExecuteAsync(
            CreateRequest() with
            {
                AvailableTools = [tool],
                RequiredToolContract = new RequiredToolExecutionContract
                {
                    Name = "readonly_verification",
                    AnyOfToolNames = ["read_state"],
                    PreferredRecoveryToolName = "read_state",
                    MaxContinuationAttempts = 2
                }
            },
            sink,
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal("verified readonly answer", result.FinalText);
        Assert.Equal(1, toolCalls);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Equal(0, result.WriteToolCalls);
        Assert.Equal("read_state", result.LastFunctionCall);
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is ToolCallRequestedEvent { ToolName: "read_state" });
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is ToolExecutionCompletedEvent
        {
            ToolName: "read_state",
            Success: true
        });
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is TerminatedEvent { TotalToolCalls: 1 });
    }

    [Fact]
    public async Task ExecuteAsync_InterventionBlocksReadonlyToolBeforeExecution()
    {
        var toolCalls = 0;
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("call-read", "read_state", new Dictionary<string, object?>())
            ]),
            TextStep("blocked handled"));
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "should not run";
            },
            new AIFunctionFactoryOptions { Name = "read_state", Description = "Reads state." });
        var engine = CreateEngine(llm);

        var result = await engine.ExecuteAsync(
            CreateRequest() with
            {
                AvailableTools = [tool],
                InterventionHook = new BlockingInterventionHook()
            },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("blocked handled", result.FinalText);
        Assert.Equal(0, toolCalls);
        Assert.Equal(0, result.TotalToolCalls);
    }

    [Fact]
    public async Task ExecuteAsync_StopGateRequestsOneContinuationBeforeAccepting()
    {
        var llm = new ScriptedLlmExecutor(
            TextStep("draft answer"),
            InspectingStep(
                request => Assert.Contains(
                    request.Messages,
                    static message => message.Role == ChatRole.User &&
                        message.Text?.Contains("verify the draft", StringComparison.Ordinal) == true),
                TextStep("verified answer")));
        var dispatcher = new ScriptedHookDispatcher(
            new BeforeStopHookResult
            {
                Continue = true,
                Message = "verify the draft before stopping",
                Reason = "verification_incomplete"
            },
            BeforeStopHookResult.None);
        var engine = CreateEngine(llm, dispatcher);

        var result = await engine.ExecuteAsync(
            CreateRequest(),
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("verified answer", result.FinalText);
        Assert.Equal(2, result.TotalRounds);
        Assert.Equal(1, result.RecoveryCount);
        Assert.Equal(2, dispatcher.StopCalls.Count);
    }

    [Theory]
    [InlineData("apply_patch")]
    [InlineData("APPLY_PATCH")]
    public async Task ExecuteAsync_WriteToolWithoutHostApproval_CommitsDeniedObservationWithoutExecuting(string toolName)
    {
        var toolCalls = 0;
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("call-write", toolName, new Dictionary<string, object?>())
            ]),
            InspectingStep(
                request => Assert.Contains(
                    request.Messages.SelectMany(static message => message.Contents),
                    static content => content is FunctionResultContent { CallId: "call-write" }),
                TextStep("write denied safely")));
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "written";
            },
            new AIFunctionFactoryOptions { Name = toolName, Description = "Writes state." });
        var engine = CreateEngine(llm);

        var result = await engine.ExecuteAsync(
            CreateRequest() with { AvailableTools = [tool] },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("write denied safely", result.FinalText);
        Assert.Equal(0, toolCalls);
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_WriteToolWithInterventionApproval_UsesBoundC4Plan()
    {
        var toolCalls = 0;
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("call-write", "apply_patch", new Dictionary<string, object?>())
            ]),
            TextStep("write completed"));
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "written";
            },
            new AIFunctionFactoryOptions { Name = "apply_patch", Description = "Writes state." });
        var sink = new RecordingEventSink();
        var engine = CreateEngine(llm);

        var result = await engine.ExecuteAsync(
            CreateRequest() with
            {
                AvailableTools = [tool],
                InterventionHook = new AllowingInterventionHook()
            },
            sink,
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal(1, toolCalls);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Equal(1, result.WriteToolCalls);
        Assert.Contains(sink.Events, static runtimeEvent => runtimeEvent is ToolExecutionCompletedEvent
        {
            ToolName: "apply_patch",
            Success: true
        });
    }

    [Fact]
    public async Task ExecuteAsync_MultipleReadonlyTools_UseSingleFrozenRegistry()
    {
        var calls = new List<string>();
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("call-a", "read_a", new Dictionary<string, object?>()),
                new FunctionCallContent("call-b", "read_b", new Dictionary<string, object?>())
            ]),
            TextStep("both read"));
        var first = AIFunctionFactory.Create(
            () => { calls.Add("a"); return "a"; },
            new AIFunctionFactoryOptions { Name = "read_a", Description = "Read A." });
        var second = AIFunctionFactory.Create(
            () => { calls.Add("b"); return "b"; },
            new AIFunctionFactoryOptions { Name = "read_b", Description = "Read B." });

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with { AvailableTools = [first, second] },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal(["a", "b"], calls);
        Assert.Equal(2, result.TotalToolCalls);
    }

    [Fact]
    public async Task ExecuteAsync_C5InitialHistoryOverflowingWindow_FailsClosedBeforeModelInsteadOfSilentCompaction()
    {
        var initial = Enumerable.Range(0, 30)
            .Select(index => new ChatMessage(
                index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"history-{index} " + new string('x', 600)))
            .ToArray();
        var llm = new ScriptedLlmExecutor(TextStep("must never be requested"));
        var contextOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 2_048,
            AutoCompressionTriggerRatio = 1,
            RecentTurnsSoftLimit = 4,
            SingleTurnSoftLimitChars = 1_000
        };

        var result = await CreateEngine(llm, contextOptions: contextOptions).ExecuteAsync(
            CreateRequest() with { InitialMessages = initial },
            new RecordingEventSink(),
            CancellationToken.None);

        // Initial messages are never silently compacted/truncated: the turn is rejected as a hard context limit.
        Assert.Equal(QueryTerminationReason.ContextHardLimit, result.TerminationReason);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_C5LongSystemAndUserMessages_ReachExecutorUntruncatedIncludingTails()
    {
        // Far above the old history-storage mapping (6000 chars => 1500 item tokens) but inside the model window.
        var system = "SYSTEM-HEAD " + new string('s', 70_000) + " SYSTEM-TAIL-MARK";
        var user = "USER-HEAD " + new string('u', 50_000) + " USER-TAIL-MARK";
        var llm = new ScriptedLlmExecutor(
            InspectingStep(
                request =>
                {
                    Assert.Contains(request.Messages, message => message.Role == ChatRole.System && message.Text == system);
                    Assert.Contains(request.Messages, message => message.Role == ChatRole.User && message.Text == user);
                },
                TextStep("planned")));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                InitialMessages = [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)]
            },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal("planned", result.FinalText);
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_C5SmallHardWindow_FailsBeforeExecutorWithContextHardLimit()
    {
        var llm = new ScriptedLlmExecutor(TextStep("must never be requested"));
        var contextOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 2_048,
            AutoCompressionTriggerRatio = 1
        };

        var result = await CreateEngine(llm, contextOptions: contextOptions).ExecuteAsync(
            CreateRequest() with
            {
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, new string('s', 40_000)),
                    new ChatMessage(ChatRole.User, "short goal")
                ]
            },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.ContextHardLimit, result.TerminationReason);
        Assert.Empty(llm.Requests);
        Assert.False(string.IsNullOrWhiteSpace(result.TerminalDetailCode));
    }

    [Fact]
    public async Task ExecuteAsync_ContextOptionsMaxTokensUpdatedBetweenTurns_AppliesNewBudgetOnNextTurn()
    {
        var llm = new ScriptedLlmExecutor(TextStep("turn 1 ok"), TextStep("must not execute"));
        var contextOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 20_000,
            AutoCompressionTriggerRatio = 1
        };
        var engine = CreateEngine(llm, contextOptions: contextOptions);

        var firstResult = await engine.ExecuteAsync(
            CreateRequest() with
            {
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "short system"),
                    new ChatMessage(ChatRole.User, "short goal")
                ]
            },
            new RecordingEventSink(),
            CancellationToken.None);
        Assert.Equal("turn 1 ok", firstResult.FinalText);

        contextOptions.ModelMaxContextTokens = 2_048;

        var secondResult = await engine.ExecuteAsync(
            CreateRequest() with
            {
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, new string('s', 40_000)),
                    new ChatMessage(ChatRole.User, "short goal")
                ]
            },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.ContextHardLimit, secondResult.TerminationReason);
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MultiStepToolTurn_TracksLastStepUsageSeparatelyFromTotalTurnUsage()
    {
        var tool = ContractTool("echo_tool");
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate[]
            {
                new()
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("call-1", "echo_tool", new Dictionary<string, object?>())],
                    FinishReason = ChatFinishReason.ToolCalls
                },
                new UsageChatResponseUpdate
                {
                    Usage = new UsageDetails { InputTokenCount = 5000, OutputTokenCount = 120, TotalTokenCount = 5120 }
                }
            },
            AnswerWithUsage(
                "final reply",
                new UsageDetails { InputTokenCount = 6590, OutputTokenCount = 458, TotalTokenCount = 7048 }));

        var engine = CreateEngine(llm);
        var result = await engine.ExecuteAsync(
            CreateRequest() with { AvailableTools = [tool] },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.True(
            result.TerminationReason == QueryTerminationReason.Normal,
            $"Unexpected termination {result.TerminationReason}: {result.TerminalDetailCode} - {result.TerminalDetailMessage}");
        Assert.Equal(2, result.TotalRounds);
        Assert.Equal(5000 + 6590, result.TotalPromptTokens);
        Assert.Equal(120 + 458, result.TotalCompletionTokens);
        Assert.Equal(6590, result.LastStepPromptTokens);
        Assert.Equal(458, result.LastStepCompletionTokens);
    }

    [Fact]
    public async Task ExecuteAsync_C5DynamicContextIsEphemeralPreparedInput()
    {
        var llm = new ScriptedLlmExecutor(InspectingStep(
            request => Assert.Contains(
                request.Messages,
                static message => message.Text?.Contains("fresh workspace state", StringComparison.Ordinal) == true),
            TextStep("dynamic answer")));
        var providerCalls = 0;

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                DynamicContextProvider = (_, _) =>
                {
                    providerCalls++;
                    return ValueTask.FromResult<QueryRuntimeDynamicContextResult?>(new QueryRuntimeDynamicContextResult
                    {
                        Messages = [new ChatMessage(ChatRole.System, "fresh workspace state")]
                    });
                }
            },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal("dynamic answer", result.FinalText);
        Assert.Equal(1, providerCalls);
        Assert.DoesNotContain(
            result.FinalMessages ?? [],
            static message => message.Text?.Contains("fresh workspace state", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ExecuteAsync_C5RejectsUnboundedDynamicExecutableCatalog()
    {
        var llm = new ScriptedLlmExecutor(TextStep("must not run"));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with { AvailableToolsProvider = static () => [] },
            new RecordingEventSink(),
            CancellationToken.None);

        Assert.Equal(QueryTerminationReason.Exception, result.TerminationReason);
        Assert.Contains("qre_v2_c5_unbounded_dynamic_catalog_unsupported", result.TerminalDetailCode, StringComparison.Ordinal);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_C6ConsumesVersionedCausalAuditFromEngine()
    {
        var audit = new QreV2.InMemoryRuntimeAuditSink();
        var result = await CreateEngine(
            new ScriptedLlmExecutor(TextStep("audited answer")),
            auditSink: audit).ExecuteAsync(
                CreateRequest(),
                new RecordingEventSink(),
                CancellationToken.None);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal(
            [
                QreV2.RuntimeAuditEventKind.TurnStarted,
                QreV2.RuntimeAuditEventKind.ContextPrepared,
                QreV2.RuntimeAuditEventKind.ModelRequestPrepared,
                QreV2.RuntimeAuditEventKind.ModelResponseCommitted,
                QreV2.RuntimeAuditEventKind.TurnTerminal
            ],
            audit.Events.Select(static value => value.Kind));
        Assert.All(audit.Events, static value => Assert.Equal(QreV2.RuntimeAuditSchema.CurrentVersion, value.SchemaVersion));
        Assert.Null(audit.Events[0].CausationId);
        for (var index = 1; index < audit.Events.Count; index++)
        {
            Assert.Equal(audit.Events[index - 1].EventId, audit.Events[index].CausationId);
        }
    }

    [Fact]
    public void BackendOptions_AcceptsOnlyQreV2AndDefaultsToIt()
    {
        Assert.Equal("qre-v2", new QueryRuntimeBackendOptions().Backend);
        Assert.False(new QueryRuntimeBackendOptions { Backend = "core" }.IsSupportedBackend);
        Assert.False(new QueryRuntimeBackendOptions { Backend = "QRE" }.IsSupportedBackend);
        Assert.True(new QueryRuntimeBackendOptions { Backend = "QRE-V2" }.IsSupportedBackend);
        Assert.False(new QueryRuntimeBackendOptions { Backend = "qre-v3" }.IsSupportedBackend);
    }

    private static RequiredToolExecutionContract AnyOfContract(int maxContinuations = 2) => new()
    {
        Name = "forge_write_evidence",
        AnyOfToolNames = ["record_alpha", "record_beta"],
        PreferredRecoveryToolName = "record_alpha",
        MaxContinuationAttempts = maxContinuations,
        Feedback = "Write evidence required before stopping.",
        ExhaustionMessage = "Required write evidence was never produced."
    };

    private static AIFunction ContractTool(string name, Action? onCall = null, string result = "ok")
        => AIFunctionFactory.Create(
            () =>
            {
                onCall?.Invoke();
                return result;
            },
            new AIFunctionFactoryOptions { Name = name, Description = name });

    [Fact]
    public async Task RequiredContract_MultiToolAnyOf_DoesNotForcePreferredToolFromRoundOne_ReadThenAlternateWriteCompletes()
    {
        var alphaCalls = 0;
        var betaCalls = 0;
        var llm = new ScriptedLlmExecutor(
            InspectingStep(
                request => Assert.IsNotType<RequiredChatToolMode>(request.Options?.ToolMode),
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "inspect_state", new Dictionary<string, object?>())])),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c2", "record_beta", new Dictionary<string, object?>())]),
            TextStep("done"));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                MaxRounds = 8,
                AvailableTools =
                [
                    ContractTool("inspect_state"),
                    ContractTool("record_alpha", () => alphaCalls++),
                    ContractTool("record_beta", () => betaCalls++)
                ],
                RequiredToolContract = AnyOfContract()
            },
            new RecordingEventSink());

        Assert.Equal("done", result.FinalText);
        Assert.Equal(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal(0, alphaCalls);
        Assert.Equal(1, betaCalls);
        Assert.Equal(3, llm.Requests.Count);
    }

    [Fact]
    public async Task RequiredContract_MultiToolAnyOf_PrematureStopWithoutHookDispatcher_FailsClosedWithBoundedContinuations()
    {
        var llm = new ScriptedLlmExecutor(
            TextStep("done"), TextStep("done again"), TextStep("still done"), TextStep("never reached"), TextStep("never reached"));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                MaxRounds = 8,
                AvailableTools = [ContractTool("record_alpha"), ContractTool("record_beta")],
                RequiredToolContract = AnyOfContract(maxContinuations: 2)
            },
            new RecordingEventSink());

        Assert.NotEqual(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.NotEqual(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal(0, result.TotalToolCalls);
        // initial attempt + exactly the bounded continuations, never more.
        Assert.Equal(3, llm.Requests.Count);
        // Continuations suggest the preferred tool but never force a single one of a multi-tool contract.
        Assert.All(llm.Requests, request => Assert.IsNotType<RequiredChatToolMode>(request.Options?.ToolMode));
    }

    [Fact]
    public async Task RequiredContract_FailedWriteDoesNotSatisfyContract()
    {
        var llm = new ScriptedLlmExecutor(
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "record_alpha", new Dictionary<string, object?>())]),
            TextStep("done"), TextStep("done again"), TextStep("still done"), TextStep("never reached"));
        var failed = """{"__icodex_tool_result":true,"status":"Failed","output":"disk full"}""";

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                MaxRounds = 8,
                AvailableTools = [ContractTool("record_alpha", result: failed), ContractTool("record_beta")],
                RequiredToolContract = AnyOfContract(maxContinuations: 2)
            },
            new RecordingEventSink());

        Assert.NotEqual(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.NotEqual(QueryTerminationReason.NoToolCalls, result.TerminationReason);
    }

    [Fact]
    public async Task RequiredContract_SingleToolContract_KeepsLegacyInitialRequirement()
    {
        var llm = new ScriptedLlmExecutor(
            InspectingStep(
                request => Assert.Equal("record_alpha", Assert.IsType<RequiredChatToolMode>(request.Options?.ToolMode).RequiredFunctionName),
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "record_alpha", new Dictionary<string, object?>())])),
            TextStep("done"));

        var result = await CreateEngine(llm).ExecuteAsync(
            CreateRequest() with
            {
                AvailableTools = [ContractTool("record_alpha")],
                RequiredToolContract = new RequiredToolExecutionContract
                {
                    Name = "single",
                    AnyOfToolNames = ["record_alpha"],
                    PreferredRecoveryToolName = "record_alpha"
                }
            },
            new RecordingEventSink());

        Assert.Equal("done", result.FinalText);
    }

    private static QreV2BackedQueryRuntimeEngine CreateEngine(
        ILLMExecutor llm,
        IRuntimeHookDispatcher? dispatcher = null,
        ContextCompressionTriggerOptions? contextOptions = null,
        QreV2.IRuntimeAuditSink? auditSink = null,
        ILogger<QreV2BackedQueryRuntimeEngine>? logger = null)
        => new(
            llm,
            contextWindowManager: null,
            telemetry: null,
            logger ?? NullLogger<QreV2BackedQueryRuntimeEngine>.Instance,
            dispatcher,
            contextOptions: contextOptions,
            auditSink: auditSink);

    private static QueryRuntimeRequest CreateRequest()
        => new()
        {
            SessionId = "qre-v2-c5-session",
            EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
            InitialMessages = [new ChatMessage(ChatRole.User, "analyze readonly state")],
            MaxRounds = 4,
            EnableTools = true,
            Scenario = MemoryInjectionScenario.Execution
        };

    private static ChatResponseUpdate TextStep(string text)
        => new(ChatRole.Assistant, [new TextContent(text)]);

    private static Func<LLMExecutionRequest, ChatResponseUpdate> InspectingStep(
        Action<LLMExecutionRequest> inspect,
        ChatResponseUpdate update)
        => request =>
        {
            inspect(request);
            return update;
        };

    private sealed class TypedResultFunction(CodexToolResult result) : AIFunction
    {
        public override string Name => "read";
        public override string Description => "Read.";
        public override JsonElement JsonSchema { get; } = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new((object?)result);
    }

    private sealed class ScriptedLlmExecutor(params object[] steps) : ILLMExecutor
    {
        private readonly Queue<object> _steps = new(steps);

        public List<LLMExecutionRequest> Requests { get; } = [];

        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var step = _steps.Dequeue();
            if (step is IReadOnlyList<ChatResponseUpdate> updates)
            {
                foreach (var update in updates)
                {
                    yield return update;
                }
            }
            else
            {
                yield return step switch
                {
                    ChatResponseUpdate update => update,
                    Func<LLMExecutionRequest, ChatResponseUpdate> factory => factory(request),
                    _ => throw new InvalidOperationException("Unsupported scripted step.")
                };
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class BlockingInterventionHook : IQueryRuntimeInterventionHook
    {
        public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
            string toolName,
            IDictionary<string, object?> arguments,
            object? session,
            CancellationToken ct = default)
            => ValueTask.FromResult(QueryRuntimeIntervention.BlockWithMessage(
                new ChatMessage(ChatRole.User, "host blocked readonly tool"),
                "host blocked readonly tool"));

        public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
            string toolName,
            string result,
            bool success,
            object? session,
            CancellationToken ct = default)
            => ValueTask.FromResult(QueryRuntimeIntervention.None);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    private sealed class RejectingInterventionHook(string mode) : IQueryRuntimeInterventionHook
    {
        public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(string toolName, IDictionary<string, object?> arguments, object? session, CancellationToken ct = default)
            => ValueTask.FromResult(mode == "hookblock"
                ? QueryRuntimeIntervention.BlockWithMessage(new ChatMessage(ChatRole.User, "scope-block-marker"), "scope-block-marker")
                : QueryRuntimeIntervention.None);

        public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(string toolName, string result, bool success, object? session, CancellationToken ct = default)
            => ValueTask.FromResult(mode == "resultrejected"
                ? QueryRuntimeIntervention.SkipToolResultWithFeedback(new ChatMessage(ChatRole.User, "rejected-marker"), "rejected-marker")
                : QueryRuntimeIntervention.None);
    }

    private sealed class AllowingInterventionHook : IQueryRuntimeInterventionHook
    {
        public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
            string toolName,
            IDictionary<string, object?> arguments,
            object? session,
            CancellationToken ct = default)
            => ValueTask.FromResult(QueryRuntimeIntervention.None);

        public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
            string toolName,
            string result,
            bool success,
            object? session,
            CancellationToken ct = default)
            => ValueTask.FromResult(QueryRuntimeIntervention.None);
    }

    private sealed class ScriptedHookDispatcher(params BeforeStopHookResult[] decisions)
        : IRuntimeHookDispatcher
    {
        private readonly Queue<BeforeStopHookResult> _decisions = new(decisions);

        public List<BeforeStopContext> StopCalls { get; } = [];

        public ValueTask<AfterModelResponseContext> DispatchAfterModelResponseAsync(
            AfterModelResponseContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(context);

        public ValueTask<BeforeStopHookResult> DispatchBeforeStopAsync(
            BeforeStopContext context,
            CancellationToken ct = default)
        {
            StopCalls.Add(context);
            return ValueTask.FromResult(
                _decisions.Count == 0 ? BeforeStopHookResult.None : _decisions.Dequeue());
        }
    }

    private sealed class RecordingEventSink : IQueryRuntimeEventSink
    {
        public List<QueryRuntimeEvent> Events { get; } = [];

        public bool IsEnabled(QueryRuntimeEventType eventType) => true;

        public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
        {
            Events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }
}
