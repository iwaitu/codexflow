// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Tests.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class CommandTaskToolsTests
{
    [Fact]
    public async Task RunCommandTool_Background_ShouldReturnCommandTaskId_AndTaskOutputShouldReadEvents()
    {
        var workspacePath = CreateWorkspace();

        try
        {
            var (runCommand, fake, scope) = Isolated(workspacePath, (req, _) => Done(req, "10.0.401"));
            using var _ = scope;
            var started = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "dotnet --version",
                ["background"] = true
            });

            Assert.Equal(ToolResultStatus.Success, started.Status);
            var commandTaskId = ExtractCommandTaskId(started.Output);
            Assert.StartsWith("cmd_", commandTaskId, StringComparison.Ordinal);

            var outputTool = new WorkerOutputTool(
                "task_output",
                (_, _) => throw new InvalidOperationException("worker output callback should not be used for command task ids"),
                NullLogger<WorkerOutputTool>.Instance);

            var output = await WaitForCommandOutputAsync(outputTool, commandTaskId);

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Contains("command output:", output.Output, StringComparison.Ordinal);
            Assert.Contains(commandTaskId, output.Output, StringComparison.Ordinal);
            Assert.Contains("stdout", output.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("10.0.401", output.Output, StringComparison.Ordinal);
            Assert.Equal(["dotnet", "--version"], fake.Requests.Single().Argv.ToArray());
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task TaskStopTool_ShouldStopKnownCommandTask()
    {
        var workspacePath = CreateWorkspace();

        try
        {
            var (runCommand, fake, scope) = Isolated(workspacePath, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
            using var _ = scope;
            var started = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "dotnet --help",
                ["background"] = true
            });
            var commandTaskId = ExtractCommandTaskId(started.Output);

            var stopTool = new TaskStopTool(
                (_, _) => throw new InvalidOperationException("worker stop callback should not be used for command task ids"),
                NullLogger<TaskStopTool>.Instance);

            var stopped = await stopTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["job_id"] = commandTaskId
            });

            Assert.Equal(ToolResultStatus.Success, stopped.Status);
            Assert.Contains(commandTaskId, stopped.Output, StringComparison.Ordinal);

            // Stopping must reach the isolation backend (that is what kills the container).
            for (var i = 0; i < 100 && !fake.SawCancellation; i++)
            {
                await Task.Delay(50);
            }

            Assert.True(fake.SawCancellation);
            Assert.True(CommandTaskRegistry.TryGet(commandTaskId, null, 10, out var snapshot));
            Assert.Equal(CommandTaskStatus.Stopped, snapshot.Status);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task RunCommandTool_VerifyPolicy_ShouldDenyBackgroundAndMutatingSubcommands()
    {
        var workspacePath = CreateWorkspace();

        try
        {
            var runCommand = new RunCommandTool(NullLogger<RunCommandTool>.Instance)
                .WithPolicy(CommandExecutionPolicy.VerifyWorker);

            var background = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "dotnet --version",
                ["background"] = true
            });
            var gitCommit = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "git commit --allow-empty -m test"
            });
            var mkdir = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "mkdir generated"
            });

            Assert.Equal(ToolResultStatus.Failed, background.Status);
            Assert.Contains("does not allow background", background.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ToolResultStatus.Failed, gitCommit.Status);
            Assert.Contains("denied by command policy", gitCommit.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ToolResultStatus.Failed, mkdir.Status);
            Assert.Contains("not allowed by command policy", mkdir.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task RunCommandTool_VerifyPolicy_ShouldAllowReadOnlyCommand()
    {
        var workspacePath = CreateWorkspace();

        try
        {
            var (runCommand, fake, scope) = Isolated(workspacePath, (req, _) => Done(req, "10.0.401"), CommandExecutionPolicy.VerifyWorker);
            using var _ = scope;

            var result = await runCommand.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["command"] = "dotnet --version",
                ["timeout_seconds"] = 3600
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("command_task_id:", result.Output, StringComparison.Ordinal);
            // the verify policy caps the timeout (300s) before it reaches the backend
            Assert.Equal(TimeSpan.FromSeconds(300), fake.Requests.Single().Timeout);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Command tasks only ever run in the isolation backend. These contract tests use a fake backend plus a host-bound trusted
    /// context; the real-Docker layer lives in DockerIsolationAttackTests / IsolatedToolsDockerTests.
    /// </summary>
    private static (RunCommandTool Tool, FakeIsolationService Fake, IDisposable Scope) Isolated(
        string workspacePath,
        Func<IsolatedCommandRequest, CancellationToken, Task<IsolatedCommandResult>>? onRun = null,
        CommandExecutionPolicy? policy = null)
    {
        var fake = new FakeIsolationService();
        if (onRun is not null)
        {
            fake.OnRun = onRun;
        }

        var accessor = new TrustedExecutionContextAccessor();
        var scope = accessor.Begin(IsolationTestSupport.Context(workspacePath, ExportScope.Empty));
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, policy, new ToolIsolationGate(fake, accessor, null));
        return (tool, fake, scope);
    }

    private static Task<IsolatedCommandResult> Done(IsolatedCommandRequest req, string stdout)
    {
        foreach (var line in stdout.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
        {
            req.OnOutput?.Invoke(new CommandOutputLine("stdout", line));
        }

        return Task.FromResult(new IsolatedCommandResult(ExecutionDecision.Allow(), IsolatedCommandStatus.Completed, 0, stdout, string.Empty, false, TimeSpan.Zero, "fake"));
    }

    private static async Task<CodexToolResult> WaitForCommandOutputAsync(WorkerOutputTool outputTool, string commandTaskId)
    {
        CodexToolResult? latest = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            latest = await outputTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["job_id"] = commandTaskId,
                ["max_events"] = 50
            });

            if (latest.Output.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                latest.Output.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return latest;
            }

            await Task.Delay(100);
        }

        return latest ?? throw new InvalidOperationException("No command output was returned.");
    }

    private static string ExtractCommandTaskId(string output)
    {
        var match = Regex.Match(output, @"command_task_id:\s*(cmd_[a-f0-9]+)", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "command_task_id was not found in output: " + output);
        return match.Groups[1].Value;
    }

    private static string CreateWorkspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-command-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        return workspacePath;
    }
}
