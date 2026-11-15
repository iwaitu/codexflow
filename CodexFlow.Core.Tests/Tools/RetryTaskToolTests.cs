// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// The coordinator calls <c>retry_failed_task</c> with a job id and is told "queued" or "cannot be retried: does not exist or wrong state".
/// It was never told why the job had failed, could not tell the worker anything, and was not told when a retry was pointless. It is now
/// told what the worker will be told, can pass guidance, and is told - with the reason - when the retry is refused.
/// </summary>
public class RetryTaskToolTests
{
    private static async Task<CodexToolResult> RunAsync(RetryTaskTool tool, params (string Key, object? Value)[] args)
    {
        var arguments = new Dictionary<string, object?> { ["job_id"] = "job-1" };
        foreach (var (key, value) in args) arguments[key] = value;
        return await tool.ExecuteAsync(arguments);
    }

    private static RetryTaskTool WithDetailed(Func<string, string?, Task<RetryJobResult>> detailed) =>
        new(_ => Task.FromResult(true), NullLogger<RetryTaskTool>.Instance) { DetailedRetry = detailed };

    [Fact]
    public async Task AQueuedRetry_TellsTheCoordinatorWhatTheWorkerWillBeToldAboutTheLastFailure()
    {
        var tool = WithDetailed((_, _) => Task.FromResult(new RetryJobResult(true, FailureSummary: "worker_reported_blocked：写入被策略拒绝")));

        var result = await RunAsync(tool);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("worker_reported_blocked：写入被策略拒绝", result.Output);
        Assert.Contains("上次失败的原因", result.Output);
        Assert.Contains("不要再次调用 start_next_task 或 retry_failed_task", result.Output);
    }

    [Fact]
    public async Task Guidance_ReachesTheRetry()
    {
        string? seen = null;
        var tool = WithDetailed((_, guidance) => { seen = guidance; return Task.FromResult(new RetryJobResult(true)); });

        await RunAsync(tool, ("guidance", "先用 dotnet list package --include-transitive 确认依赖从哪个项目进来。"));

        Assert.Equal("先用 dotnet list package --include-transitive 确认依赖从哪个项目进来。", seen);
    }

    [Fact]
    public async Task ARefusedRetry_IsAnErrorThatSaysWhy_AndPointsAtWhatToDoInstead()
    {
        var tool = WithDetailed((_, _) => Task.FromResult(new RetryJobResult(false, "retry_would_repeat_the_failure",
            "这个作业失败的原因是 offline_restore_unavailable：再试一次会得到同样的结果。")));

        var result = await RunAsync(tool);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("offline_restore_unavailable", result.Output);
        Assert.Contains("retry_would_repeat_the_failure", result.Output);
        Assert.Contains("不要再次调用 retry_failed_task", result.Output);
        Assert.Contains("continue_worker", result.Output);      // a new instruction is what can change the outcome
    }

    [Fact]
    public async Task WithoutTheDetailedFunction_TheToolBehavesAsItDid()
    {
        var ok = new RetryTaskTool(_ => Task.FromResult(true), NullLogger<RetryTaskTool>.Instance);
        var no = new RetryTaskTool(_ => Task.FromResult(false), NullLogger<RetryTaskTool>.Instance);

        Assert.Equal(ToolResultStatus.Success, (await RunAsync(ok)).Status);
        Assert.Equal(ToolResultStatus.Failed, (await RunAsync(no)).Status);
    }

    [Fact]
    public void TheDescription_SaysWhatARetryCarries_WhatItRefuses_AndWhenToUseContinueWorkerInstead()
    {
        var description = new RetryTaskTool(_ => Task.FromResult(true), NullLogger<RetryTaskTool>.Instance).Description;

        Assert.Contains("guidance", description);
        Assert.Contains("上次失败", description);
        Assert.Contains("拒绝", description);
        Assert.Contains("continue_worker", description);
    }

    [Fact]
    public void ContinueWorkerDescription_SaysItAcceptsAFailedJob_AndThatTheScopeStaysTheSame()
    {
        var description = new ContinueWorkerTool(
            (_, _) => Task.FromResult(new ContinueWorkerResult { Success = true, Mode = ContinueWorkerMode.SpawnedFollowUp, JobId = "j" }),
            NullLogger<ContinueWorkerTool>.Instance).Description;

        Assert.Contains("失败", description);
        Assert.Contains("写入范围", description);
        Assert.Contains("失败原因", description);
    }
}
