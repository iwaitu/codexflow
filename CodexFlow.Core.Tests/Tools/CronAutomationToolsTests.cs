// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class CronAutomationToolsTests
{
    [Fact]
    public async Task CronCreate_ShouldPersistSchedule()
    {
        var scheduler = new InMemoryCronSchedulerService();
        var tool = new CronCreateTool(scheduler, NullLogger<CronCreateTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-cron",
            ["cron"] = "*/15 * * * *",
            ["prompt"] = "检查 worker 状态",
            ["worker_type"] = "plan",
            ["name"] = "status-check",
            ["max_rounds"] = 2
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"created\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"status-check\"", result.Output, StringComparison.Ordinal);

        var schedules = await scheduler.ListAsync("session-cron");
        var schedule = Assert.Single(schedules);
        Assert.Equal("*/15 * * * *", schedule.Cron);
        Assert.Equal("plan", schedule.WorkerType);
        Assert.Equal(2, schedule.MaxRounds);
    }

    [Fact]
    public async Task CronCreate_InvalidExpression_ShouldFail()
    {
        var scheduler = new InMemoryCronSchedulerService();
        var tool = new CronCreateTool(scheduler, NullLogger<CronCreateTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-cron",
            ["cron"] = "not a cron expression",
            ["prompt"] = "检查 worker 状态"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
    }

    [Fact]
    public async Task CronListAndDelete_ShouldFilterAndRemoveSchedule()
    {
        var scheduler = new InMemoryCronSchedulerService();
        var create = new CronCreateTool(scheduler, NullLogger<CronCreateTool>.Instance);
        var list = new CronListTool(scheduler);
        var delete = new CronDeleteTool(scheduler);

        await create.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-a",
            ["cron"] = "@hourly",
            ["prompt"] = "run a"
        });
        await create.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-b",
            ["cron"] = "@daily",
            ["prompt"] = "run b"
        });

        var listed = await list.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-a"
        });

        Assert.Equal(ToolResultStatus.Success, listed.Status);
        Assert.Contains("\"count\":1", listed.Output, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-a\"", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sessionId\":\"session-b\"", listed.Output, StringComparison.Ordinal);

        var schedule = Assert.Single(await scheduler.ListAsync("session-a"));
        var deleted = await delete.ExecuteAsync(new Dictionary<string, object?>
        {
            ["cron_id"] = schedule.Id
        });

        Assert.Equal(ToolResultStatus.Success, deleted.Status);
        Assert.Empty(await scheduler.ListAsync("session-a"));
    }
}
