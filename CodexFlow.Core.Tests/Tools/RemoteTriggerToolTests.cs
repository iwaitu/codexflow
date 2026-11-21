// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Services;
using Moq;
using System.Text.Json;
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
            ["session_id"] = "session-a",
            ["payload"] = new { UnsupportedAndUnused = true }
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
            async (request, ct) =>
            {
                Assert.Empty(await service.ListAsync("session-remote", ct: ct));
                capturedRequest = request;
                return new SpawnWorkerResult
                {
                    Success = true,
                    JobId = "job-remote",
                    JobType = "ForgeWorker",
                    Status = "queued"
                };
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

    [Theory]
    [InlineData(null, "{}")]
    [InlineData(" \t\n ", "{}")]
    [InlineData("  中文 raw <event>\r\nline  ", "中文 raw <event>\r\nline")]
    [InlineData(" { \"count\": 1,  \"enabled\": true } ", "{ \"count\": 1,  \"enabled\": true }")]
    public async Task RemoteTrigger_PreservesNormalizedPayloadText(string? payload, string expected)
    {
        var service = new InMemoryRemoteTriggerService();
        var tool = new RemoteTriggerTool(service);

        var result = await tool.ExecuteAsync(new() { ["source"] = "fixture", ["event_type"] = "test", ["payload"] = payload });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(expected, Assert.Single(await service.ListAsync()).PayloadJson);
    }

    [Fact]
    public async Task RemoteTrigger_PreservesNestedJsonNormalizationAndAllowsSharedAcyclicLists()
    {
        using var json = JsonDocument.Parse("""
            {"Event":{"operations":[{"Line":2}],"Single":["item"],"Flags":[true,null],"Value":1234567890.23456789}}
            """);
        var service = new InMemoryRemoteTriggerService();
        var tool = new RemoteTriggerTool(service);
        var shared = new List<object?> { 7, "second" };

        var nested = await tool.ExecuteAsync(new()
        {
            ["source"] = "fixture", ["event_type"] = "nested", ["payload"] = json.RootElement
        });
        var repeated = await tool.ExecuteAsync(new()
        {
            ["parameters"] = new Dictionary<string, object?>
            {
                ["source"] = "fixture", ["event_type"] = "repeated", ["payload"] = new List<object?> { shared, shared }
            }
        });

        Assert.Equal(ToolResultStatus.Success, nested.Status);
        Assert.Equal(ToolResultStatus.Success, repeated.Status);
        Assert.Equal("""{"Event":{"operations":[{"Line":2}],"Single":"item","Flags":[true,null],"Value":1234567890.23456789}}""",
            Assert.Single(await service.ListAsync(eventType: "nested")).PayloadJson);
        Assert.Equal("""[[7,"second"],[7,"second"]]""",
            Assert.Single(await service.ListAsync(eventType: "repeated")).PayloadJson);
    }

    [Fact]
    public async Task RemoteTrigger_UndefinedTopLevelJsonPreservesExistingEmptyPayloadDefault()
    {
        var service = new InMemoryRemoteTriggerService();
        var tool = new RemoteTriggerTool(service);

        var result = await tool.ExecuteAsync(new()
        {
            ["source"] = "fixture", ["event_type"] = "test", ["payload"] = default(JsonElement)
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("{}", Assert.Single(await service.ListAsync()).PayloadJson);
    }

    [Theory]
    [InlineData("custom-object")]
    [InlineData("nested-custom-object")]
    [InlineData("disposed-json")]
    [InlineData("nested-disposed-json")]
    [InlineData("nested-undefined-json")]
    [InlineData("nonfinite-number")]
    [InlineData("cyclic-list")]
    [InlineData("wrapped-cyclic-list")]
    public async Task RemoteTrigger_RejectsInvalidPayloadBeforeDispatchOrStorage(string scenario)
    {
        const string hiddenValue = "fixture-private-value";
        var unsupported = new { Secret = hiddenValue };
        var cycle = new List<object?>();
        cycle.Add(cycle);
        var payload = scenario switch
        {
            "custom-object" => (object)unsupported,
            "nested-custom-object" => new Dictionary<string, object?> { ["item"] = new List<object?> { unsupported } },
            "disposed-json" => DisposedJson(hiddenValue),
            "nested-disposed-json" => new Dictionary<string, object?> { ["item"] = DisposedJson(hiddenValue) },
            "nested-undefined-json" => new Dictionary<string, object?> { ["item"] = default(JsonElement) },
            "nonfinite-number" => double.NaN,
            "cyclic-list" or "wrapped-cyclic-list" => cycle,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var service = new Mock<IRemoteTriggerService>(MockBehavior.Strict);
        var dispatches = 0;
        var tool = new RemoteTriggerTool(service.Object, (_, _) =>
        {
            dispatches++;
            return Task.FromResult(new SpawnWorkerResult { Success = true, JobId = "unexpected" });
        });
        var arguments = new Dictionary<string, object?>
        {
            ["source"] = "fixture", ["event_type"] = "invalid", ["session_id"] = "session",
            ["user_id"] = "user", ["workspace_path"] = "workspace", ["prompt"] = "offline fixture",
            ["dispatch_worker"] = true, ["payload"] = payload
        };
        if (scenario == "wrapped-cyclic-list")
        {
            arguments = new() { ["parameters"] = arguments };
        }

        var result = await tool.ExecuteAsync(arguments);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("payload", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hiddenValue, result.Output, StringComparison.Ordinal);
        Assert.Equal(0, dispatches);
        service.VerifyNoOtherCalls();
    }

    private static JsonElement DisposedJson(string hiddenValue)
    {
        using var document = JsonDocument.Parse($$"""{"secret":"{{hiddenValue}}"}""");
        return document.RootElement;
    }
}
