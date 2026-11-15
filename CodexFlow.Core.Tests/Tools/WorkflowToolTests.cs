// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class WorkflowToolTests
{
    [Fact]
    public async Task Workflow_Run_ShouldExecuteScriptAndRecordAudit()
    {
        var runner = new Mock<ISkillScriptRunner>();
        runner
            .Setup(x => x.RunAsync(
                "workflow",
                "scripts/release.py",
                It.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "--dry-run" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("release ok");
        var auditStore = new InMemoryWorkflowAuditStore();
        var tool = new WorkflowTool(runner.Object, auditStore);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["name"] = "release",
            ["script_path"] = "scripts/release.py",
            ["args"] = new[] { "--dry-run" },
            ["session_id"] = "session-workflow",
            ["user_id"] = "user-1",
            ["workspace_path"] = "/workspace"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"completed\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"succeeded\"", result.Output, StringComparison.Ordinal);

        var run = Assert.Single(await auditStore.ListAsync("session-workflow", "release"));
        Assert.Equal("release ok", run.Output);
        Assert.Equal("succeeded", run.Status);
        Assert.Equal("scripts/release.py", run.ScriptPath);
    }

    [Fact]
    public async Task Workflow_ListAndGet_ShouldReturnAuditRecords()
    {
        var runner = new Mock<ISkillScriptRunner>();
        runner
            .Setup(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");
        var auditStore = new InMemoryWorkflowAuditStore();
        var tool = new WorkflowTool(runner.Object, auditStore);

        var runResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["name"] = "nightly",
            ["script_path"] = "scripts/nightly.py",
            ["session_id"] = "session-a"
        });

        var run = Assert.Single(await auditStore.ListAsync("session-a"));
        var listResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "list",
            ["session_id"] = "session-a"
        });
        var getResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["workflow_id"] = run.Id
        });

        Assert.Equal(ToolResultStatus.Success, runResult.Status);
        Assert.Equal(ToolResultStatus.Success, listResult.Status);
        Assert.Contains("\"count\":1", listResult.Output, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Success, getResult.Status);
        Assert.Contains($"\"id\":\"{run.Id}\"", getResult.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_Run_ShouldRejectUnsafeScriptPath()
    {
        var tool = new WorkflowTool(new Mock<ISkillScriptRunner>().Object, new InMemoryWorkflowAuditStore());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["script_path"] = "../escape.py"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("safe relative path", result.Output, StringComparison.OrdinalIgnoreCase);

        var absoluteResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["script_path"] = "/escape.py"
        });

        Assert.Equal(ToolResultStatus.Failed, absoluteResult.Status);
    }

    [Fact]
    public async Task Workflow_Run_ShouldRecordRunnerFailure()
    {
        var runner = new Mock<ISkillScriptRunner>();
        runner
            .Setup(x => x.RunAsync("workflow", "scripts/fail.py", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ExitCode: 1\nStderr:\nfailed");
        var auditStore = new InMemoryWorkflowAuditStore();
        var tool = new WorkflowTool(runner.Object, auditStore);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["name"] = "fail",
            ["script_path"] = "scripts/fail.py",
            ["session_id"] = "session-fail"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        var run = Assert.Single(await auditStore.ListAsync("session-fail", "fail"));
        Assert.Equal("failed", run.Status);
        Assert.Contains("ExitCode: 1", run.Error, StringComparison.Ordinal);
    }
}
