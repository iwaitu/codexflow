// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class WorkerDelegationLintTests
{
    [Fact]
    public async Task SpawnWorkerTool_AppendsLintWarnings_ForVaguePrompt()
    {
        var tool = new SpawnWorkerTool(
            (_, _) => Task.FromResult(new SpawnWorkerResult
            {
                Success = true,
                JobId = "job-123",
                Status = "queued"
            }),
            NullLogger<SpawnWorkerTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-1",
            ["user_id"] = "user-1",
            ["workspace_path"] = "/repo",
            ["worker_type"] = "explore",
            ["prompt"] = "继续"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("[委派预检提醒]", result.Display);
        Assert.NotNull(result.SystemHint);
        var metadata = Assert.IsType<Dictionary<string, object?>>(result.Metadata);
        var warnings = Assert.IsType<string[]>(metadata["lintWarnings"]);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public async Task ContinueWorkerTool_DoesNotAppendLintWarnings_ForScopedVerificationPrompt()
    {
        var tool = new ContinueWorkerTool(
            (_, _) => Task.FromResult(new ContinueWorkerResult
            {
                Success = true,
                JobId = "job-123",
                Mode = ContinueWorkerMode.Resumed
            }),
            NullLogger<ContinueWorkerTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["job_id"] = "job-123",
            ["prompt"] = "请继续验证 CodexFlow.Core/Runtime/QueryRuntimeEngine.cs 的终止详情码映射，并返回测试证据摘要。"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Null(result.Display);
        Assert.Null(result.SystemHint);
    }

    [Fact]
    public void WorkerDelegationLint_Decorate_PreservesSummaryAndAddsWarnings()
    {
        var result = CodexToolResult.Succeeded(
            "已启动 worker。",
            summary: "已启动 worker。");

        var decorated = WorkerDelegationLint.Decorate(result, ["缺少范围", "缺少验收"]);

        Assert.Equal("已启动 worker。", decorated.Summary);
        Assert.Contains("[委派预检提醒]", decorated.Display);
        Assert.Contains("缺少范围", decorated.SystemHint);
    }

    [Fact]
    public void WorkerDelegationLint_Analyze_WarnsWhenReadOnlyWorkerPromptContainsWriteIntent()
    {
        var warnings = WorkerDelegationLint.Analyze(
            workerType: "explore",
            prompt: "先研究缓存模块，再直接修改 MongoFileService.cs 修复 bug，并告诉我结果。",
            description: "based on your findings, fix the bug");

        Assert.Contains(
            warnings,
            warning => warning.Contains("只读 worker", StringComparison.Ordinal));
        Assert.Contains(
            warnings,
            warning => warning.Contains("把理解责任外包", StringComparison.Ordinal));
    }
}
