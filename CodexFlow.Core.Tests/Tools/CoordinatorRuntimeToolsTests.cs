// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class CoordinatorRuntimeToolsTests
{
    [Fact]
    public async Task WorkerOutputTool_ShouldFormatViewAndEvents()
    {
        var tool = new WorkerOutputTool(
            "worker_output",
            (request, _) => Task.FromResult(new WorkerOutputResult
            {
                Success = true,
                JobId = request.JobId,
                View = new WorkerOutputSnapshot
                {
                    JobId = request.JobId,
                    WorkerType = "forge",
                    Status = "Completed",
                    StateKind = "completed",
                    Summary = "implemented change",
                    LatestSeq = 42,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                Events =
                [
                    new WorkerOutputEvent
                    {
                        Seq = 41,
                        EventType = "ToolExecutionCompleted",
                        PayloadJson = "{\"tool\":\"run_tests\"}",
                        OccurredAtUtc = DateTime.UtcNow
                    }
                ]
            }),
            NullLogger<WorkerOutputTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["job_id"] = "job-1",
            ["after_seq"] = 40
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("status=Completed", result.Output, StringComparison.Ordinal);
        Assert.Contains("latest_seq=42", result.Output, StringComparison.Ordinal);
        Assert.Contains("#41 ToolExecutionCompleted", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskUserQuestionTool_ShouldRequireQuestionAndReturnStructuredMetadata()
    {
        var tool = new AskUserQuestionTool();

        var missing = await tool.ExecuteAsync([]);
        Assert.Equal(ToolResultStatus.Failed, missing.Status);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["header"] = "Confirm",
            ["question"] = "是否继续执行 forge worker?",
            ["options"] = new[] { "继续", "暂停" },
            ["preview"] = "将启动后台写入 worker。"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("USER_QUESTION_REQUEST", result.Output, StringComparison.Ordinal);
        Assert.Contains("继续", result.Output, StringComparison.Ordinal);
        Assert.NotNull(result.Metadata);
    }

    [Fact]
    public async Task PlanModeTools_ShouldReturnPlanBoundarySignals()
    {
        var session = new CodexSession { Id = "session-plan" };
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetSessionAsync("session-plan", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var memory = new Mock<IMemoryService>();
        memory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        memory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sessionManager = new CodexSessionManager(store.Object, memory.Object, NullLogger<CodexSessionManager>.Instance);

        var enter = new EnterPlanModeTool(sessionManager);
        var exit = new ExitPlanModeTool(sessionManager);

        var entered = await enter.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-plan",
            ["objective"] = "review coordinator plan"
        });
        Assert.Equal(ToolResultStatus.Success, entered.Status);
        Assert.Contains("PLAN_MODE_ENTERED", entered.Output, StringComparison.Ordinal);
        Assert.Equal(bool.TrueString, session.Metadata[EnterPlanModeTool.ActiveMetadataKey]);
        Assert.Equal("review coordinator plan", session.Metadata[EnterPlanModeTool.ObjectiveMetadataKey]);

        var missingApproval = await exit.ExecuteAsync([]);
        Assert.Equal(ToolResultStatus.Failed, missingApproval.Status);

        var approved = await exit.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-plan",
            ["approved"] = true,
            ["plan_summary"] = "Spawn forge, then verify."
        });
        Assert.Equal(ToolResultStatus.Success, approved.Status);
        Assert.Contains("PLAN_MODE_APPROVED", approved.Output, StringComparison.Ordinal);
        Assert.Equal(bool.FalseString, session.Metadata[EnterPlanModeTool.ActiveMetadataKey]);
        Assert.Equal(bool.TrueString, session.Metadata[EnterPlanModeTool.ApprovedMetadataKey]);
        Assert.Equal("Spawn forge, then verify.", session.Metadata[EnterPlanModeTool.PlanSummaryMetadataKey]);
    }

    [Fact]
    public async Task SyntheticOutputTool_ShouldOnlyEchoCoordinatorSummary()
    {
        var tool = new SyntheticOutputTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = "worker 已完成，等待验证。",
            ["summary"] = "worker completed"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("worker 已完成，等待验证。", result.Output);
        Assert.True(tool.Metadata.IsReadOnly);
    }

    [Fact]
    public async Task MonitorTool_ShouldSummarizeSessionAndWorkers()
    {
        var session = new CodexSession { Id = "session-monitor", CurrentStage = 3, ActiveTaskId = "TASK-001" };
        session.Plan.Add(new CodexTask { Id = "TASK-001", Title = "Implement monitor", Status = CodexTaskStatus.Executing });
        var sessionManager = CreateSessionManager(session);
        var tool = new MonitorTool(
            sessionManager,
            (sessionId, _) => Task.FromResult<IReadOnlyList<WorkerJobSummary>>(
            [
                new WorkerJobSummary
                {
                    JobId = "job-1",
                    WorkerType = "forge",
                    Status = "Running",
                    StateKind = "running",
                    TaskId = "TASK-001",
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]));

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "session-monitor"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("MONITOR_SNAPSHOT", result.Output, StringComparison.Ordinal);
        Assert.Contains("session_id: session-monitor", result.Output, StringComparison.Ordinal);
        Assert.Contains("workers_count: 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("job-1 status=Running worker=forge", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorTool_ShouldReadJobViewAndEvents()
    {
        var session = new CodexSession { Id = "session-monitor-job" };
        var sessionManager = CreateSessionManager(session);
        var tool = new MonitorTool(
            sessionManager,
            workerOutputFunc: (request, _) => Task.FromResult(new WorkerOutputResult
            {
                Success = true,
                JobId = request.JobId,
                View = new WorkerOutputSnapshot
                {
                    JobId = request.JobId,
                    SessionId = "session-monitor-job",
                    WorkerType = "verify",
                    Status = "Completed",
                    StateKind = "completed",
                    LatestSeq = 7,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                Events =
                [
                    new WorkerOutputEvent
                    {
                        Seq = 7,
                        EventType = "WorkerCompleted",
                        PayloadJson = "{\"ok\":true}",
                        OccurredAtUtc = DateTime.UtcNow
                    }
                ]
            }));

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["job_id"] = "job-verify",
            ["include_events"] = true
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("job_id: job-verify", result.Output, StringComparison.Ordinal);
        Assert.Contains("job_status: Completed", result.Output, StringComparison.Ordinal);
        Assert.Contains("#7 WorkerCompleted", result.Output, StringComparison.Ordinal);
    }

    private static CodexSessionManager CreateSessionManager(CodexSession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var memory = new Mock<IMemoryService>();
        memory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        memory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new CodexSessionManager(store.Object, memory.Object, NullLogger<CodexSessionManager>.Instance);
    }
}
