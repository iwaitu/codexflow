// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class WindowsLaunchAndToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p13-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public void WindowsCommandResolver_AdaptsNpmAndRefusesUnknownCmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fakeBin = Path.Combine(_root, "node-bin");
        var npmBin = Path.Combine(fakeBin, "node_modules", "npm", "bin");
        Directory.CreateDirectory(npmBin);
        File.WriteAllText(Path.Combine(fakeBin, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(fakeBin, "npm.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(npmBin, "npm-cli.js"), string.Empty);
        File.WriteAllText(Path.Combine(fakeBin, "evil.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(fakeBin, "custom.bat"), string.Empty);

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = fakeBin };

        var npmResolved = WindowsCommandResolver.Resolve("npm", ["--version"], env);
        Assert.True(npmResolved.Allowed);
        Assert.EndsWith("node.exe", npmResolved.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, npmResolved.Args.Count);
        Assert.EndsWith("npm-cli.js", npmResolved.Args[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("--version", npmResolved.Args[1]);

        var evilResolved = WindowsCommandResolver.Resolve("evil", ["arg1"], env);
        Assert.False(evilResolved.Allowed);
        Assert.Contains("adapter table", evilResolved.RejectionReason, StringComparison.OrdinalIgnoreCase);

        var batResolved = WindowsCommandResolver.Resolve("custom.bat", [], env);
        Assert.False(batResolved.Allowed);
    }

    [Fact]
    public async Task LocalExecution_PreservesSpacesQuotesAndShellMetacharacters()
    {
        var workspace = Path.Combine(_root, "ws-args");
        Directory.CreateDirectory(workspace);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(workspace, "echo.ps1"), "Write-Output $args[0]");
        }

        using var service = new LocalExecutionService(
            Options.Create(new IsolationOptions { StagingRoot = Path.Combine(_root, "staging") }),
            NullLogger<LocalExecutionService>.Instance);

        var ctx = new TrustedExecutionContext
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = workspace,
            Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
        };

        var open = await service.OpenSessionAsync(ctx, new IsolatedSessionOptions("local", StageWorkspace: true), CancellationToken.None);
        Assert.True(open.Decision.IsAllowed);
        await using var session = open.Session!;

        var trickyArg = "hello \"world\" & calc | echo ^ %OS%";
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "echo.ps1", trickyArg }
            : new[] { "sh", "-c", "printf '%s' \"$1\"", "_", trickyArg };

        var run = await session.RunAsync(new IsolatedCommandRequest(argv), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        Assert.Contains(trickyArg, run.Stdout, StringComparison.Ordinal);
        Assert.Contains("cmd=", run.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommandTool_LocalDotnetVersion_Succeeds()
    {
        var workspace = Path.Combine(_root, "ws-dotnet");
        Directory.CreateDirectory(workspace);

        using var service = new LocalExecutionService(
            Options.Create(new IsolationOptions { StagingRoot = Path.Combine(_root, "staging-dotnet") }),
            NullLogger<LocalExecutionService>.Instance);

        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(new TrustedExecutionContext
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = workspace,
            Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
        });

        var gate = new ToolIsolationGate(service, accessor, new IsolationOptions());
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["command"] = new[] { "dotnet", "--version" }
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task ExecCodeTool_JavaInLocalMode_WritesMainJavaWithoutSh()
    {
        var workspace = Path.Combine(_root, "ws-java");
        Directory.CreateDirectory(workspace);

        var fakeBackend = new LocalLayoutRecordingService(Path.Combine(_root, "local-layout"));
        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(new TrustedExecutionContext
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = workspace,
            Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
        });

        var gate = new ToolIsolationGate(fakeBackend, accessor, new IsolationOptions());
        var tool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["language"] = "java",
            ["code"] = "public class Main { public static void main(String[] a) { System.out.println(\"hi\"); } }"
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var recorded = Assert.Single(fakeBackend.RecordedRequests);
        Assert.Equal("java", recorded.Argv[0]);
        Assert.DoesNotContain("sh", recorded.Argv);
        Assert.Null(recorded.StdIn);
        Assert.True(File.Exists(Path.Combine(fakeBackend.Layout.TempDir, "Main.java")));
    }

    [Fact]
    public void EnvironmentNotesAndPrompts_ReflectLocalExecutionTruthfully()
    {
        var note = RunCommandTool.BuildEnvironmentNote("dotnet", "mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/sdk:10.0", ExecutionBackend.Local);
        Assert.NotNull(note);
        Assert.Contains("无网络隔离", note, StringComparison.Ordinal);
        Assert.Contains("无文件系统隔离", note, StringComparison.Ordinal);

        var prompt = CodexPrompts.BuildCommandExecutionEnvironmentBody(ExecutionBackend.Local);
        Assert.Contains("没有网络隔离，也没有文件系统隔离", prompt, StringComparison.Ordinal);
    }

    private sealed class LocalLayoutRecordingService : IIsolatedExecutionService
    {
        public ExecutionLayout Layout { get; }
        public List<IsolatedCommandRequest> RecordedRequests { get; } = [];

        public LocalLayoutRecordingService(string baseDir)
        {
            Layout = new ExecutionLayout(
                ExecutionBackend.Local,
                Path.Combine(baseDir, "workspace"),
                Path.Combine(baseDir, "home"),
                Path.Combine(baseDir, "tmp"),
                Path.Combine(baseDir, "inputs"),
                new Dictionary<string, string>());
        }

        public bool SupportsNetworkMode(ExecutionNetworkMode mode) => true;

        public Task<IsolatedSessionOpenResult> OpenSessionAsync(TrustedExecutionContext context, IsolatedSessionOptions options, CancellationToken ct)
            => Task.FromResult(new IsolatedSessionOpenResult(
                ExecutionDecision.Allow(),
                new RecordingSession(Layout, RecordedRequests)));

        private sealed class RecordingSession(ExecutionLayout layout, List<IsolatedCommandRequest> recorded) : IIsolatedExecutionSession
        {
            public string SessionHandle => "rec-1";
            public ExecutionLayout Layout => layout;
            public Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct)
            {
                recorded.Add(request);
                return Task.FromResult(new IsolatedCommandResult(
                    ExecutionDecision.Allow(), IsolatedCommandStatus.Completed, 0, "hi", string.Empty, false, TimeSpan.Zero, "local"));
            }
            public Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
                => Task.FromResult(new IsolatedExportResult(ExecutionDecision.Allow(), [], []));
            public Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<EvidenceFile>>([]);
            public Task AbortAsync() => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
