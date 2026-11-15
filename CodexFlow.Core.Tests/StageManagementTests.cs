// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using CodexFlow.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests;

/// <summary>
/// Tests to prevent stage-related regressions:
/// - Stage should advance to 3 during execution
/// - Stage should advance to 4 (or stay at 4) after all tasks complete
/// - generate_dev_plan should refuse to regenerate when plan is fully completed
/// - execute_code_task should detect already-completed tasks and not re-execute
/// - Stage prompt content should match expected behavior for each stage
/// </summary>
public class StageManagementTests
{
    private readonly ITestOutputHelper _output;

    public StageManagementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldSetStage3_DuringExecution()
    {
        // Arrange: session starts at Stage 1 (analysis/planning)
        var tempPath = Path.Combine(Path.GetTempPath(), "codex-stage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            var task = new CodexTask { Id = "T1", Title = "Test Task", Status = CodexTaskStatus.Pending };
            var session = new CodexSession
            {
                Id = "stage-test-session",
                CurrentStage = 1, // <-- starts at 1
                WorkspacePath = tempPath,
                Plan = { task }
            };

            int capturedStage = -1;
            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync("stage-test-session", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Callback<CodexSession, CancellationToken>((s, _) =>
                {
                    // Capture stage when session is saved during execution
                    if (s.ActiveTaskId == "T1")
                        capturedStage = s.CurrentStage;
                })
                .Returns(Task.CompletedTask);

            var mockKernel = new Mock<ICodexAgentKernel>();
            mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                .ReturnsAsync(new CodexResponse("done", true, TotalToolCalls: 1, WriteToolCalls: 1));

            var mockValidator = new Mock<ICodexValidator>();
            mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ValidationResult(true, "ok", []));

            var mockSecurity = new Mock<ICodexSecurityAuditor>();
            mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

            var orchestrator = CreateOrchestrator(mockStore, mockKernel, mockValidator, mockSecurity);

            // Act
            var result = await orchestrator.ExecuteCodeTaskAsync("stage-test-session", "T1", workspacePath: tempPath);

            // Assert
            _output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
            _output.WriteLine($"Captured stage during execution: {capturedStage}");
            Assert.True(result.Success, result.Message);
            Assert.Equal(3, capturedStage); // Stage must be 3 during task execution
        }
        finally
        {
            try { Directory.Delete(tempPath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteCodeTaskTool_ShouldBlockReexecution_WhenPlanFullyCompleted()
    {
        // Arrange: all tasks already completed
        var session = new CodexSession
        {
            Id = "completed-session",
            CurrentStage = 4,
            WorkspacePath = "/tmp/completed",
            Plan = {
                new CodexTask { Id = "T1", Title = "Task 1", Status = CodexTaskStatus.Success },
                new CodexTask { Id = "T2", Title = "Task 2", Status = CodexTaskStatus.Success },
                new CodexTask { Id = "T3", Title = "Task 3", Status = CodexTaskStatus.Skipped }
            }
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("completed-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var sessionManager = new CodexSessionManager(mockStore.Object, Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);
        var orchestrator = CreateOrchestrator(mockStore);

        var tool = new ExecuteCodeTaskTool(orchestrator, NullLogger<ExecuteCodeTaskTool>.Instance);

        // Act
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "completed-session",
            ["task_id"] = "T1",
            ["workspace_path"] = "/tmp/completed"
        });

        // Assert
        _output.WriteLine($"Status: {result.Status}");
        _output.WriteLine($"Output: {result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("已处于完成态", result.Output);
        Assert.Contains("全计划已完成", result.Output);
    }

    [Fact]
    public async Task GenerateDevPlanTool_ShouldAllowReplan_WhenPlanFullyCompleted()
    {
        // Arrange: plan is already fully completed in Redis session
        var session = new CodexSession
        {
            Id = "plan-done-session",
            CurrentStage = 4,
            WorkspacePath = "/tmp/plandone",
            Plan = {
                new CodexTask { Id = "T1", Title = "Task 1", Status = CodexTaskStatus.Success },
                new CodexTask { Id = "T2", Title = "Task 2", Status = CodexTaskStatus.Success }
            }
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("plan-done-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var mockPlanner = new Mock<ICodexPlanner>();
        mockPlanner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CodexTask> { new() { Id = "T3", Title = "New Task", Status = CodexTaskStatus.Pending } });

        var sessionManager = new CodexSessionManager(mockStore.Object, Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);
        var orchestrator = CreateOrchestrator(mockStore);

        var tool = new GenerateDevPlanTool(mockPlanner.Object, sessionManager, orchestrator, NullLogger<GenerateDevPlanTool>.Instance);

        // Act
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "plan-done-session",
            ["requirement"] = "Fix remaining issues"
        });

        // Assert: should allow re-planning, NOT block
        _output.WriteLine($"Status: {result.Status}");
        _output.WriteLine($"Output: {result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        // Use semantic assertion instead of fuzzy Contains("1")
        Assert.Matches(@"共计\s*\d+\s*个任务", result.Output); 
        Assert.Contains("任务规划已生成", result.Output);
        
        // Verify planner was indeed called once (behavioral assertion)
        mockPlanner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateDevPlanTool_ShouldRollback_WhenTaskListSnapshotIsMissingAfterPublish()
    {
        var session = new CodexSession
        {
            Id = "plan-snapshot-missing-session",
            CurrentStage = 4,
            WorkspacePath = "/tmp/plan-snapshot-missing",
            Plan =
            {
                new CodexTask { Id = "OLD-1", Title = "Old Task", Status = CodexTaskStatus.Success }
            },
            PlanVersion = "old-plan",
            PlanGeneratedAtUtc = DateTime.UtcNow.AddHours(-1)
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockPlanner = new Mock<ICodexPlanner>();
        mockPlanner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CodexTask>
            {
                new() { Id = "NEW-1", Title = "New Task", Status = CodexTaskStatus.Pending }
            });

        var mockTaskListService = new Mock<ITaskListService>();
        mockTaskListService.Setup(s => s.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");
        mockTaskListService.Setup(s => s.GetSnapshotAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var sessionManager = new CodexSessionManager(mockStore.Object, Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);
        var orchestrator = CreateOrchestrator(mockStore, taskListService: mockTaskListService.Object);
        var tool = new GenerateDevPlanTool(
            mockPlanner.Object,
            sessionManager,
            orchestrator,
            NullLogger<GenerateDevPlanTool>.Instance,
            mockTaskListService.Object);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["requirement"] = "Fix remaining issues"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("task list snapshot 未成功持久化", result.Output);
        Assert.Single(session.Plan);
        Assert.Equal("OLD-1", session.Plan[0].Id);
        Assert.Equal(4, session.CurrentStage);
        Assert.Equal("old-plan", session.PlanVersion);
        Assert.NotNull(session.PlanGeneratedAtUtc);
    }

    [Theory]
    [InlineData(0, "入口守卫")]
    [InlineData(1, "深度感知")]
    [InlineData(2, "任务派发")]
    [InlineData(3, "任务执行中")]
    [InlineData(4, "执行完毕")]
    public void GetOpenSpecPromptForStage_ShouldReturnCorrectPromptContent(int stage, string expectedKeyword)
    {
        // All stages should include the central StageControl guide
        var stagePrompt = stage switch
        {
            1 => CodexPromptGuide.OpenSpecGuideStage1,
            2 => CodexPromptGuide.OpenSpecGuideStage2,
            3 => CodexPromptGuide.OpenSpecGuideStage3,
            4 => CodexPromptGuide.OpenSpecGuideStage4,
            5 => CodexPromptGuide.OpenSpecGuideStage5,
            _ => CodexPromptGuide.OpenSpecGuideStage0
        };

        var fullPrompt = CodexPromptGuide.OpenSpecGuideStageControl + "\n" + stagePrompt;

        _output.WriteLine($"Stage {stage} prompt length: {fullPrompt.Length}");
        _output.WriteLine($"Stage prompt snippet: {stagePrompt[..Math.Min(100, stagePrompt.Length)]}...");

        Assert.Contains("中央指挥中心", fullPrompt); // OpenSpecGuideStageControl always present
        Assert.Contains(expectedKeyword, fullPrompt);
    }

    [Fact]
    public void Stage4Prompt_ShouldProhibitReexecution()
    {
        var prompt = CodexPromptGuide.OpenSpecGuideStage4;

        _output.WriteLine($"Stage 4 prompt:\n{prompt}");

        Assert.Contains("禁止", prompt);
        Assert.Contains("generate_dev_plan", prompt);
        Assert.Contains("execute_code_task", prompt);
    }

    // --- Helper to create orchestrator with minimal mocking ---
    private static CodexOrchestrator CreateOrchestrator(
        Mock<ISessionStore>? mockStore = null,
        Mock<ICodexAgentKernel>? mockKernel = null,
        Mock<ICodexValidator>? mockValidator = null,
        Mock<ICodexSecurityAuditor>? mockSecurity = null,
        ITaskListService? taskListService = null)
    {
        mockStore ??= new Mock<ISessionStore>();
        mockKernel ??= new Mock<ICodexAgentKernel>();
        mockValidator ??= new Mock<ICodexValidator>();
        mockSecurity ??= new Mock<ICodexSecurityAuditor>();

        return new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            Mock.Of<IGitService>(),
            new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance).Object,
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            taskListService ?? Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());
    }
}
