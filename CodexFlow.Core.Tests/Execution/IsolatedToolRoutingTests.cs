// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.TDD.Adapters;
using CodexFlow.Core.Tests.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

internal sealed class FakeIsolationService : IIsolatedExecutionService
{
    public List<TrustedExecutionContext> Contexts { get; } = [];
    public List<IsolatedSessionOptions> Options { get; } = [];
    public List<IsolatedCommandRequest> Requests { get; } = [];
    public int ExportCalls { get; private set; }
    public bool SawCancellation { get; private set; }
    public ExecutionDecision? OpenDecision { get; set; }
    public IsolatedExportResult Export { get; set; } = new(ExecutionDecision.Allow(), [], []);

    public Func<IsolatedCommandRequest, CancellationToken, Task<IsolatedCommandResult>> OnRun { get; set; } = (req, _) =>
    {
        req.OnOutput?.Invoke(new CommandOutputLine("stdout", "fake-output"));
        return Task.FromResult(new IsolatedCommandResult(ExecutionDecision.Allow(), IsolatedCommandStatus.Completed, 0, "fake-output", string.Empty, false, TimeSpan.Zero, "fake"));
    };

    public Task<IsolatedSessionOpenResult> OpenSessionAsync(TrustedExecutionContext context, IsolatedSessionOptions options, CancellationToken ct)
    {
        Contexts.Add(context);
        Options.Add(options);
        return Task.FromResult(OpenDecision is { IsAllowed: false } d
            ? new IsolatedSessionOpenResult(d, null)
            : new IsolatedSessionOpenResult(ExecutionDecision.Allow(), new Session(this)));
    }

    private sealed class Session(FakeIsolationService owner) : IIsolatedExecutionSession
    {
        public string SessionHandle => "fake";

        public async Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct)
        {
            owner.Requests.Add(request);
            try
            {
                return await owner.OnRun(request, ct);
            }
            catch (OperationCanceledException)
            {
                owner.SawCancellation = true;
                return new IsolatedCommandResult(ExecutionDecision.Deny(ExecutionReasonCodes.Canceled, "canceled"), IsolatedCommandStatus.Canceled, null, string.Empty, string.Empty, false, TimeSpan.Zero, "fake");
            }
        }

        public Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
        {
            owner.ExportCalls++;
            return Task.FromResult(owner.Export);
        }

        public Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct) => Task.FromResult<IReadOnlyList<EvidenceFile>>([]);

        public Task AbortAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class IsolatedToolRoutingTests
{
    private static ToolIsolationGate Gate(FakeIsolationService fake)
        => new(fake, new TrustedExecutionContextAccessor(), new IsolationOptions { CommandImage = "img-default", LanguageImages = { ["python"] = "img-python" } });

    private static TrustedExecutionContext Ctx(string root = "C:/trusted/root") => IsolationTestSupport.Context(Path.GetFullPath(root), ExportScope.From(["a.txt"]));

    private static Dictionary<string, object?> Cmd(params string[] argv) => new() { ["command"] = argv };

    [Fact]
    public async Task RunCommand_WithoutHostContext_ShouldDeny_AndNeverReachTheBackend()
    {
        var fake = new FakeIsolationService();
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, Gate(fake));
        var result = await tool.ExecuteAsync(Cmd("dotnet", "--version"));
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(ExecutionReasonCodes.MissingTrustedContext, result.Output, StringComparison.Ordinal);
        Assert.Empty(fake.Contexts);
    }

    [Fact]
    public async Task RunCommand_WithoutBackend_ShouldBeUnavailable_NotAHostFallback()
    {
        var marker = Path.Combine(Path.GetTempPath(), "cf-host-fallback-" + Guid.NewGuid().ToString("N"));
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance);                       // no gate at all
        var viaUnconfigured = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(null, accessor, null));
        foreach (var t in new[] { tool, viaUnconfigured })
        {
            var result = await t.ExecuteAsync(Cmd("mkdir", marker));
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("unavailable", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ExecutionReasonCodes.IsolationNotConfigured, result.Output, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(marker), "the command must not have run on the host");
    }

    [Fact]
    public async Task RunCommand_ShouldRouteArgvThroughTrustedContext_IgnoringModelSuppliedWorkspaceArguments()
    {
        var fake = new FakeIsolationService();
        var trusted = Ctx();
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(trusted);
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(fake, accessor, new IsolationOptions { CommandImage = "img-default" }));

        var args = Cmd("dotnet", "build", "src/App.csproj");
        args["workspace_path"] = "C:/Windows";
        args["project_root"] = "C:/Users/victim";
        args["session_id"] = "someone-elses-session";
        args["cwd"] = "src";
        var result = await tool.ExecuteAsync(args);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Same(trusted, fake.Contexts.Single());
        Assert.Equal("img-default", fake.Options.Single().Image);
        var req = fake.Requests.Single();
        Assert.Equal(["dotnet", "build", "src/App.csproj"], req.Argv.ToArray());
        Assert.Equal("src", req.Cwd);
        Assert.Equal(1, fake.ExportCalls);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("a/../../b")]
    public async Task RunCommand_ShouldRejectEscapingCwd(string cwd)
    {
        var fake = new FakeIsolationService();
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(fake, accessor, null));
        var args = Cmd("ls");
        args["cwd"] = cwd;
        var result = await tool.ExecuteAsync(args);
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(fake.Contexts);
    }

    [Fact]
    public async Task RunCommand_ExportRejection_ShouldFailTheCommandAndStateNothingWasApplied()
    {
        var fake = new FakeIsolationService
        {
            Export = new IsolatedExportResult(
                ExecutionDecision.Deny(ExecutionReasonCodes.ExportRejectedOutOfScope, "export refused"),
                [], [new ExportRejection("evil.cs", ExecutionReasonCodes.ExportRejectedOutOfScope, "outside authorized scope")])
        };
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(fake, accessor, null));
        var result = await tool.ExecuteAsync(Cmd("cp", "a.txt", "evil.cs"));
        Assert.Contains("ExportRejectedOutOfScope", result.Output, StringComparison.Ordinal);
        Assert.Contains("No source changes were applied", result.Output, StringComparison.Ordinal);
        Assert.Contains("evil.cs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_SynchronousCancel_ShouldPropagateAndCancelTheBackend()
    {
        var fake = new FakeIsolationService { OnRun = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(fake, accessor, null));
        using var cts = new CancellationTokenSource();
        var running = tool.ExecuteAsync(Cmd("ls"), cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await WaitUntil(() => fake.SawCancellation);
        Assert.Equal(0, fake.ExportCalls);
    }

    [Fact]
    public async Task BackgroundCommand_ShouldStreamOutputAndBeStoppableByTaskStop()
    {
        var fake = new FakeIsolationService
        {
            OnRun = async (req, ct) =>
            {
                req.OnOutput?.Invoke(new CommandOutputLine("stdout", "line-1"));
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            }
        };
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, new ToolIsolationGate(fake, accessor, null));
        var args = Cmd("ls");
        args["background"] = true;
        var started = await tool.ExecuteAsync(args);
        Assert.Equal(ToolResultStatus.Success, started.Status);
        var id = System.Text.RegularExpressions.Regex.Match(started.Output, @"cmd_[0-9a-f]{32}").Value;

        await WaitUntil(() => CommandTaskRegistry.TryGet(id, null, 50, out var s) && s.Events.Any(e => e.Text == "line-1"));
        CommandTaskRegistry.TryGet(id, null, 50, out var running);
        Assert.Equal(CommandTaskStatus.Running, running.Status);

        var stopTool = new TaskStopTool(null, NullLogger<TaskStopTool>.Instance);
        var stopped = await stopTool.ExecuteAsync(new Dictionary<string, object?> { ["job_id"] = id });
        Assert.Equal(ToolResultStatus.Success, stopped.Status);
        await WaitUntil(() => fake.SawCancellation);
        CommandTaskRegistry.TryGet(id, null, 50, out var final);
        Assert.Equal(CommandTaskStatus.Stopped, final.Status);
        Assert.Equal(0, fake.ExportCalls);
    }

    [Fact]
    public async Task ExecCode_ShouldRunInTheLanguageImageViaStdin_AndNeverExport()
    {
        var fake = new FakeIsolationService();
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(Ctx());
        var tool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, Gate(fake));
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(1+1)", ["language"] = "python", ["workspace_path"] = "C:/Windows" });
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("img-python", fake.Options.Single().Image);
        Assert.Equal("print(1+1)", fake.Requests.Single().StdIn);
        Assert.Equal(["python3", "-"], fake.Requests.Single().Argv.ToArray());
        Assert.Equal(0, fake.ExportCalls);
    }

    [Fact]
    public async Task ExecCode_WithoutHostContext_ShouldDeny()
    {
        var fake = new FakeIsolationService();
        var tool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, Gate(fake));
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(1)" });
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(ExecutionReasonCodes.MissingTrustedContext, result.Output, StringComparison.Ordinal);
        Assert.Empty(fake.Contexts);
    }

    [Fact]
    public async Task RunTests_WithoutHostContext_ShouldDeny_AndCommandLineSplitterKeepsQuotedArguments()
    {
        var tool = new RunTestsTool(Array.Empty<CodexFlow.Core.TDD.ITestFrameworkAdapter>(), NullLogger<RunTestsTool>.Instance, Gate(new FakeIsolationService()));
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["test_file_path"] = "Foo.Tests/FooTests.cs" });
        Assert.Contains(ExecutionReasonCodes.MissingTrustedContext, result.Output, StringComparison.Ordinal);

        Assert.Equal(
            ["dotnet", "test", "my proj/x.csproj", "--filter", "FullyQualifiedName~A"],
            RunTestsTool.SplitCommandLine("dotnet", "test \"my proj/x.csproj\" --filter \"FullyQualifiedName~A\""));
    }

    [Theory]
    [InlineData("javascript", "tests/sample test.js", "tests/sample test.js", true)]
    [InlineData("JS", "tests/sample.test.cjs", "tests/sample.test.cjs", false)]
    [InlineData(null, "tests/sample.test.mjs", "tests/sample.test.mjs", false)]
    [InlineData("javascript", "tests", "tests/**/*.{test,spec}.{js,mjs,cjs}", false)]
    public async Task RunTests_JavaScriptUsesNodeInsideTheTrustedWorkspace(
        string? language, string testPath, string expectedTarget, bool lockedProject)
    {
        var workspace = IsolationTestSupport.NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "tests"));
            if (lockedProject) await File.WriteAllTextAsync(Path.Combine(workspace, "package-lock.json"), "{}");
            var fake = new FakeIsolationService
            {
                OnRun = (_, _) => Task.FromResult(new IsolatedCommandResult(ExecutionDecision.Allow(),
                    IsolatedCommandStatus.Completed, 0, "TAP version 13\n# pass 3\n# fail 0\n", "", false, TimeSpan.Zero, "fake"))
            };
            var tool = new RunTestsTool([new NodeJestAdapter(), new NodeTestAdapter()], NullLogger<RunTestsTool>.Instance, Gate(fake));
            using var scope = new TrustedExecutionContextAccessor().Begin(Ctx(workspace));

            var result = await tool.ExecuteAsync(new() { ["test_file_path"] = testPath, ["language"] = language });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            var payload = Assert.IsType<RunTestsPayload>(PlanToolJson.Read<RunTestsPayload>(result.Output));
            Assert.True(payload.Success);
            Assert.Equal(3, payload.PassedCount);
            Assert.Equal(0, payload.FailedCount);
            Assert.Equal(lockedProject ? 2 : 1, fake.Requests.Count);
            if (lockedProject)
            {
                Assert.Equal(["sh", "-c", OfflineToolchainPreparation.NodeScript], fake.Requests[0].Argv);
            }
            Assert.Equal(["node", "--test", "--test-reporter=tap", "--", expectedTarget], fake.Requests[^1].Argv);
            Assert.Equal(0, fake.ExportCalls);
        }
        finally
        {
            IsolationTestSupport.Cleanup(workspace);
        }
    }

    [Theory]
    [InlineData("typescript", "tests/sample.test.ts")]
    [InlineData(null, "tests/sample.test.js")]
    public async Task RunTests_PreservesExistingJestRouting(string? language, string testPath)
    {
        var fake = new FakeIsolationService();
        var tool = new RunTestsTool([new NodeJestAdapter(), new NodeTestAdapter()], NullLogger<RunTestsTool>.Instance, Gate(fake));
        using var scope = new TrustedExecutionContextAccessor().Begin(Ctx());

        await tool.ExecuteAsync(new() { ["test_file_path"] = testPath, ["language"] = language });

        Assert.Equal(["npx", "jest", testPath.Replace('/', Path.DirectorySeparatorChar), "--json"], Assert.Single(fake.Requests).Argv);
    }

    [Fact]
    public async Task RunTests_JavaScriptRejectsPathsOutsideTheTrustedWorkspace()
    {
        var fake = new FakeIsolationService();
        var tool = new RunTestsTool([new NodeTestAdapter()], NullLogger<RunTestsTool>.Instance, Gate(fake));
        using var scope = new TrustedExecutionContextAccessor().Begin(Ctx());

        var result = await tool.ExecuteAsync(new() { ["test_file_path"] = "../foreign.test.mjs", ["language"] = "javascript" });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData(1, "not ok 2 - assertion failed\r\n# pass 1\r\n# fail 1\r\n", 1, 1)]
    [InlineData(1, "Cannot find test file\n", 0, 0)]
    [InlineData(0, "# pass 0\n# fail 0\n", 0, 0)]
    public void NodeTests_ReportFailureForFailedOrMissingTests(int exitCode, string output, int passed, int failed)
    {
        var result = new NodeTestAdapter().ParseOutput(output, exitCode);

        Assert.False(result.Success);
        Assert.Equal(passed, result.PassedCount);
        Assert.Equal(failed, result.FailedCount);
        Assert.NotEmpty(result.FailureDetails);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "condition not reached in time");
    }
}

public class CapabilityEnforcementTests
{
    private sealed class UndeclaredTool : ICodexTool
    {
        public string Name => "totally_new_tool";
        public string Description => "x";
        public ToolCategory Category => ToolCategory.Read;
        public IReadOnlyList<int> AllowedStages => [];
        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default) => Task.FromResult(CodexToolResult.Succeeded("ran"));
    }

    private sealed class DeclaredTool(ToolCapability capability) : ICodexTool
    {
        public string Name => "totally_new_declared_tool";
        public string Description => "x";
        public ToolCategory Category => ToolCategory.Read;
        public IReadOnlyList<int> AllowedStages => [];
        public ToolCapability Capability => capability;
        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default) => Task.FromResult(CodexToolResult.Succeeded("ran"));
    }

    [Fact]
    public void EveryToolTheBootstrapperRegisters_ShouldHaveATrustedCapabilityDeclaration()
    {
        var registry = new ToolSurfaceSnapshotTests.RecordingToolRegistry();
        ToolSurfaceSnapshotTests.CreateBootstrapper().EnsureRegistered(registry);
        var tools = registry.GetActiveTools().Concat(registry.SearchTools(string.Empty)).DistinctBy(t => t.Name).ToList();
        Assert.True(tools.Count > 40, "bootstrapper unexpectedly registered few tools");
        var undeclared = tools.Where(t => t.Capability == ToolCapability.Unknown).Select(t => t.Name).ToList();
        Assert.True(undeclared.Count == 0, "tools without a capability declaration: " + string.Join(", ", undeclared));

        foreach (var name in new[] { "exec_cmd", "exec_code", "run_tests", "skill" })
        {
            Assert.Equal(ToolCapability.ExecuteUntrustedCode, tools.Single(t => t.Name == name).Capability);
        }

        foreach (var name in new[] { "write_file", "edit_file", "delete_file", "hs_write", "apply_patch" })
        {
            Assert.Equal(ToolCapability.WorkspaceWrite, ToolCapabilityCatalog.Resolve(name));
        }
    }

    [Fact]
    public void DefaultToolRegistry_ShouldRejectUndeclaredTools_ByDefault()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        registry.RegisterTool(new UndeclaredTool());
        registry.RegisterTool(new DeclaredTool(ToolCapability.ReadOnly));
        Assert.DoesNotContain("totally_new_tool", registry.GetActiveToolNames());
        Assert.Contains("totally_new_declared_tool", registry.GetActiveToolNames());
        Assert.Contains("totally_new_tool", registry.RejectedUndeclaredTools);
    }

    [Fact]
    public void DefaultToolRegistry_LegacyOptOut_ShouldBeExplicit()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance, denyUndeclaredCapabilities: false);
        registry.RegisterTool(new UndeclaredTool());
        Assert.Contains("totally_new_tool", registry.GetActiveToolNames());
    }

    [Fact]
    public void ModelInput_CannotChangeACapability()
    {
        Assert.Equal(ToolCapability.Unknown, ToolCapabilityCatalog.Resolve("EXEC_CMD_but_readonly"));
        Assert.False(ToolCapabilityCatalog.Evaluate("anything_new").IsAllowed);
        Assert.Equal(ExecutionReasonCodes.UnknownToolCapability, ToolCapabilityCatalog.Evaluate("anything_new").ReasonCode);
    }
}

public class ScopeHookAndFactoryTests
{
    private static TaskScopeInterventionHook Hook(TaskFileScopeDescriptor scope, bool requireScope = true)
        => new(scope, NullLogger.Instance, enforceCapabilities: true, requireScopeForWrites: requireScope);

    private static TaskFileScopeDescriptor Scope()
    {
        var s = new TaskFileScopeDescriptor();
        s.AllowedFiles.Add("src/Allowed.cs");
        return s;
    }

    private static async Task<bool> Blocked(TaskScopeInterventionHook hook, string tool, Dictionary<string, object?> args)
    {
        var r = await hook.OnToolCallRequestedAsync(tool, args, null);
        return !ReferenceEquals(r, QueryRuntimeIntervention.None) && r != QueryRuntimeIntervention.None;
    }

    [Fact]
    public async Task UnknownTool_ShouldBeBlocked_EvenWithoutScope()
        => Assert.True(await Blocked(Hook(new TaskFileScopeDescriptor()), "brand_new_tool", new()));

    [Fact]
    public async Task WriteTool_WithoutScope_ShouldBeBlocked_ButIsolatedExecutionIsExempt()
    {
        var hook = Hook(new TaskFileScopeDescriptor());
        Assert.True(await Blocked(hook, "write_file", new() { ["path"] = "src/Allowed.cs" }));
        Assert.False(await Blocked(hook, "exec_cmd", new() { ["command"] = new[] { "dotnet", "build" } }));
        Assert.False(await Blocked(hook, "exec_code", new() { ["code"] = "print(1)" }));
        Assert.False(await Blocked(hook, "ivilson_read", new() { ["path"] = "anything" }));
    }

    [Fact]
    public async Task WriteTool_WithoutAPath_ShouldBeBlocked()
    {
        var hook = Hook(Scope());
        Assert.True(await Blocked(hook, "write_file", new() { ["content"] = "x" }));
        Assert.True(await Blocked(hook, "edit_file", new()));
        Assert.True(await Blocked(hook, "hs_write", new() { ["unrelated"] = "x" }));
    }

    [Theory]
    [InlineData("src/Allowed.cs", false)]
    [InlineData("/src/Allowed.cs", false)]
    [InlineData("../src/Allowed.cs", true)]
    [InlineData("src/../../etc/passwd", true)]
    [InlineData("C:/Windows/System32/x.dll", true)]
    [InlineData("C:\\Windows\\x.dll", true)]
    [InlineData("\\\\server\\share\\x.cs", true)]
    [InlineData("//server/share/x.cs", true)]
    [InlineData(".git/config", true)]
    [InlineData("src/.git/hooks/pre-commit", true)]
    [InlineData("src/Allowed.cs:stream", true)]
    [InlineData("src/Other.cs", true)]
    public async Task WriteTool_PathShapeAndScope(string path, bool blocked)
    {
        var hook = Hook(Scope());
        foreach (var tool in new[] { "write_file", "edit_file", "delete_file", "hs_write" })
        {
            Assert.Equal(blocked, await Blocked(hook, tool, new() { ["path"] = path }));
        }
    }

    [Fact]
    public void ScopeMapping_ShouldCarryEveryAuthorizationList_AndHashIsCaseAndListSensitive()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/Foo.cs");
        scope.AllowedFileNames.Add("Bar.cs");
        scope.AllowedDirectories.Add("src/gen");
        scope.DisallowedDirectories.Add("src/gen/secret");
        var export = TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.Contains("src/Foo.cs", export.AllowedFiles);
        Assert.Contains("Bar.cs", export.AllowedFileNames);
        Assert.Contains("src/gen/secret", export.DisallowedDirectories);
        Assert.True(export.Allows("src/Foo.cs"));
        Assert.True(export.Allows("anywhere/Bar.cs"));
        Assert.True(export.Allows("src/gen/x.cs"));
        Assert.False(export.Allows("src/gen/secret/key.cs"));    // disallowed wins
        Assert.False(export.Allows("src/foo.cs"));               // case-sensitive: a different file on Linux

        var upper = ExportScope.From(["src/Foo.cs"]);
        var lower = ExportScope.From(["src/foo.cs"]);
        Assert.NotEqual(TrustedExecutionContextFactory.ComputeScopeHash(upper), TrustedExecutionContextFactory.ComputeScopeHash(lower));
        Assert.NotEqual(
            TrustedExecutionContextFactory.ComputeScopeHash(ExportScope.From(["x"])),
            TrustedExecutionContextFactory.ComputeScopeHash(ExportScope.From(null, ["x"])));
        Assert.Equal(
            TrustedExecutionContextFactory.ComputeScopeHash(ExportScope.From(["a", "b"])),
            TrustedExecutionContextFactory.ComputeScopeHash(ExportScope.From(["b", "a"])));
    }
}

public class WritePointHardeningTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("wp");
    private readonly string _outside = IsolationTestSupport.NewTempDir("wpout");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_outside);
    }

    private static bool TryDirLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    [Fact]
    public void IsWithinRoot_ShouldNotAcceptASiblingWithTheSamePrefix()
    {
        var sibling = _root + "-evil";
        Assert.True(ToolPathResolver.IsWithinRoot(Path.Combine(_root, "a.txt"), _root));
        Assert.True(ToolPathResolver.IsWithinRoot(_root, _root));
        Assert.False(ToolPathResolver.IsWithinRoot(Path.Combine(sibling, "a.txt"), _root));
    }

    [Fact]
    public async Task WriteFile_ShouldRefuseGitMetadataAndLinkedDirectories_AtTheRealWritePoint()
    {
        Assert.True(TryDirLink(Path.Combine(_root, "linked"), _outside), "could not create a directory link on this host");
        var tool = new WriteFileTool(NullLogger<WriteFileTool>.Instance, null);

        async Task<CodexToolResult> Write(string path) => await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root, ["path"] = path, ["content"] = "pwn"
        });

        var viaLink = await Write("linked/pwn.txt");
        Assert.Equal(ToolResultStatus.Failed, viaLink.Status);
        Assert.Contains("Write refused", viaLink.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));

        var git = await Write(".git/config");
        Assert.Equal(ToolResultStatus.Failed, git.Status);
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));

        var ok = await Write("src/ok.txt");
        Assert.Equal(ToolResultStatus.Success, ok.Status);
        Assert.Equal("pwn", File.ReadAllText(Path.Combine(_root, "src", "ok.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfw-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EditAndDeleteFile_ShouldRefuseLinkedPaths()
    {
        File.WriteAllText(Path.Combine(_outside, "victim.txt"), "VICTIM");
        Assert.True(TryDirLink(Path.Combine(_root, "linked"), _outside), "could not create a directory link on this host");
        var edit = new EditFileTool(NullLogger<EditFileTool>.Instance, null);
        var delete = new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null);

        var e = await edit.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _root, ["path"] = "linked/victim.txt", ["old_string"] = "VICTIM", ["new_string"] = "PWNED" });
        var d = await delete.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _root, ["path"] = "linked/victim.txt" });

        Assert.Equal(ToolResultStatus.Failed, e.Status);
        Assert.Equal(ToolResultStatus.Failed, d.Status);
        Assert.Equal("VICTIM", File.ReadAllText(Path.Combine(_outside, "victim.txt")));
    }

    [Fact]
    public async Task WriteAllTextSafe_ShouldDetectALinkSwappedInBetweenTheChecks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var target = Path.Combine(_root, "src", "a.txt");
        File.WriteAllText(target, "old");
        // The directory is replaced by a link right after the first validation can have passed: the second check must catch it.
        Directory.Move(Path.Combine(_root, "src"), Path.Combine(_root, "src_real"));
        Assert.True(TryDirLink(Path.Combine(_root, "src"), _outside), "could not create a directory link on this host");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ToolPathResolver.WriteAllTextSafeAsync(target, _root, "pwn", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }
}
