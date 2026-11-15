// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class RemoteTriggerToolTests
{
    [Fact]
    public async Task RemoteTrigger_ShouldRecordExternalEvent()
    {
        var service = new InMemoryRemoteTriggerService();
        var tool = new RemoteTriggerTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["source"] = "github",
            ["event_type"] = "pull_request",
            ["session_id"] = "session-remote",
            ["payload"] = "{\"action\":\"opened\"}"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"recorded\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"github\"", result.Output, StringComparison.Ordinal);

        var events = await service.ListAsync("session-remote", "github", "pull_request");
        var triggerEvent = Assert.Single(events);
        Assert.Equal("pull_request", triggerEvent.EventType);
        Assert.False(triggerEvent.DispatchWorker);
    }

    [Fact]
    public async Task RemoteTrigger_List_ShouldReturnFilteredEvents()
    {
        var service = new InMemoryRemoteTriggerService();
        var tool = new RemoteTriggerTool(service);

        await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["source"] = "github",
            ["event_type"] = "push",
            ["session_id"] = "session-a"
        });
        await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["source"] = "slack",
            ["event_type"] = "mention",
            ["session_id"] = "session-b"
        });

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "list",
            ["session_id"] = "session-a"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"count\":1", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-a\"", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sessionId\":\"session-b\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteTrigger_DispatchWorker_ShouldSpawnWorkerAndRecordJobId()
    {
        var service = new InMemoryRemoteTriggerService();
        SpawnWorkerRequest? capturedRequest = null;
        var tool = new RemoteTriggerTool(
            service,
            (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new SpawnWorkerResult
                {
                    Success = true,
                    JobId = "job-remote",
                    JobType = "ForgeWorker",
                    Status = "queued"
                });
            });

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["source"] = "github",
            ["event_type"] = "issue_comment",
            ["session_id"] = "session-remote",
            ["user_id"] = "user-1",
            ["workspace_path"] = "/workspace",
            ["dispatch_worker"] = true,
            ["worker_type"] = "forge",
            ["prompt"] = "处理 issue comment",
            ["task_id"] = "TASK-REMOTE"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.NotNull(capturedRequest);
        Assert.Equal(WorkerType.Forge, capturedRequest.WorkerType);
        Assert.Equal("TASK-REMOTE", capturedRequest.TaskId);
        Assert.Contains("\"worker_job_id\":\"job-remote\"", result.Output, StringComparison.Ordinal);

        var triggerEvent = Assert.Single(await service.ListAsync("session-remote"));
        Assert.True(triggerEvent.DispatchWorker);
        Assert.Equal("job-remote", triggerEvent.WorkerJobId);
    }
}
