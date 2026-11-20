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

public sealed class TaskCrudToolsTests
{
    [Fact]
    public void TaskListText_PreservesJsonStringsAndNulls_AndRejectsOtherItems()
    {
        Assert.Collection(TaskCrudToolSupport.ParseStringList("[\"文档\",null,\" a \"]"),
            value => Assert.Equal("文档", value),
            value => Assert.Null(value),
            value => Assert.Equal(" a ", value));
        Assert.Empty(TaskCrudToolSupport.ParseStringList("[\"valid\",1]"));
        Assert.Empty(TaskCrudToolSupport.ParseStringList("[\"invalid\",]"));
    }

    [Fact]
    public async Task TaskCreate_ShouldAppendTask_AndSaveSnapshot()
    {
        var session = new CodexSession { Id = "session-task-create", CurrentStage = 2 };
        var (sessionManager, taskListService) = CreateHarness(session);
        var tool = new TaskCreateTool(sessionManager, taskListService.Object, NullLogger<TaskCreateTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["title"] = "Implement task CRUD",
            ["description"] = "Add task tools",
            ["inputs"] = new[] { "plan approved" },
            ["outputs"] = new[] { "task tools" },
            ["checklist_items"] = "Create tools\nAdd tests"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Single(session.Plan);
        Assert.Equal("TASK-001", session.Plan[0].Id);
        Assert.Equal("Implement task CRUD", session.Plan[0].Title);
        Assert.Equal(2, session.Plan[0].ChecklistItems.Count);
        taskListService.Verify(x => x.SaveSnapshotAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TaskUpdate_Status_ShouldUpdateSession_AndTaskListStatus()
    {
        var session = new CodexSession { Id = "session-task-update", CurrentStage = 2 };
        session.Plan.Add(new CodexTask { Id = "TASK-001", Title = "Task", Status = CodexTaskStatus.Pending });
        var (sessionManager, taskListService) = CreateHarness(session);
        var tool = new TaskUpdateTool(sessionManager, taskListService.Object, NullLogger<TaskUpdateTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["task_id"] = "TASK-001",
            ["status"] = "Executing",
            ["inputs"] = new[] { "worker:job-1" }
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(CodexTaskStatus.Executing, session.Plan[0].Status);
        Assert.NotNull(session.Plan[0].StartedAt);
        Assert.Equal("worker:job-1", Assert.Single(session.Plan[0].Inputs));
        taskListService.Verify(
            x => x.UpdateTaskStatusAsync(session, "TASK-001", CodexTaskStatus.Executing, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TaskGetAndList_ShouldReadSessionPlan()
    {
        var session = new CodexSession { Id = "session-task-read", PlanVersion = "v1" };
        session.Plan.Add(new CodexTask { Id = "TASK-001", Title = "Read task", Status = CodexTaskStatus.Pending });
        var (sessionManager, taskListService) = CreateHarness(session);
        var getTool = new TaskGetTool(sessionManager);
        var listTool = new TaskListTool(sessionManager, taskListService.Object);

        var get = await getTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["task_id"] = "TASK-001"
        });
        var list = await listTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id
        });

        Assert.Equal(ToolResultStatus.Success, get.Status);
        Assert.Contains("Read task", get.Output, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Success, list.Status);
        Assert.Contains("TASK-001", list.Output, StringComparison.Ordinal);
        taskListService.Verify(x => x.GetSnapshotAsync(session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (CodexSessionManager SessionManager, Mock<ITaskListService> TaskListService) CreateHarness(CodexSession session)
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(x => x.GetSessionAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessionStore
            .Setup(x => x.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var memoryService = new Mock<IMemoryService>();
        memoryService
            .Setup(x => x.RecallFactsAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        memoryService
            .Setup(x => x.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var taskListService = new Mock<ITaskListService>();
        taskListService
            .Setup(x => x.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");
        taskListService
            .Setup(x => x.UpdateTaskStatusAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CodexTaskStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");
        taskListService
            .Setup(x => x.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        var sessionManager = new CodexSessionManager(
            sessionStore.Object,
            memoryService.Object,
            NullLogger<CodexSessionManager>.Instance);

        return (sessionManager, taskListService);
    }
}
