// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Integration;

/// <summary>
/// 验证 TaskList 快照在不同调用路径下均能正确生成和推送。
/// 路径 A: GenerateDevPlanTool (Chat LLM 直接调用)
/// 路径 B: CodexOrchestrator.ExecuteCodeTaskAsync (编排器执行)
/// 路径 C: CodexOrchestrator.RunNextStepAsync (Legacy 入口，包含计划生成)
/// </summary>
public class TaskListIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GenerateDevPlanTool_ShouldAllowReplan_WhenPlanAlreadyCompleted()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockPlanner = new Mock<ICodexPlanner>();
        var mockTaskList = new Mock<ITaskListService>();

        var completedSession = new CodexSession
        {
            Id = "tasklist-plan-completed",
            CurrentStage = 3,
            WorkspacePath = "/tmp/test",
            Plan = {
                new CodexTask { Id = "T1", Title = "Done1", Status = CodexTaskStatus.Success },
                new CodexTask { Id = "T2", Title = "Done2", Status = CodexTaskStatus.CompletedWithWarnings },
                new CodexTask { Id = "T3", Title = "Done3", Status = CodexTaskStatus.Skipped }
            }
        };

        // Planner returns a fresh plan when called
        mockPlanner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CodexTask> { new() { Id = "T4", Title = "New Fix", Status = CodexTaskStatus.Pending } });

        mockStore.Setup(s => s.GetSessionAsync("tasklist-plan-completed", It.IsAny<CancellationToken>())).ReturnsAsync(completedSession);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            Mock.Of<ICodexValidator>(),
            CreateMockGit().Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            mockTaskList.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var tool = new GenerateDevPlanTool(
            mockPlanner.Object,
            sessionManager,
            orchestrator,
            NullLogger<GenerateDevPlanTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "tasklist-plan-completed",
            ["requirement"] = "Fix remaining architecture issues"
        });

        // Should allow re-planning — old completed plan gets cleared and new plan generated
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        // [Fix] Low-prio: Stronger semantic assertion
        Assert.Matches(@"共计\s*\d+\s*个任务", result.Output);
        Assert.Contains("任务规划已生成", result.Output);

        mockPlanner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 路径 A: GenerateDevPlanTool.ExecuteAsync() 直接调用 ICodexPlanner，
    /// 验证计划生成后 ITaskListService.SaveSnapshotAsync 被调用。
    /// </summary>
    [Fact]
    public async Task GenerateDevPlanTool_Should_PublishTaskListSnapshot_AfterPlanCreation()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockPlanner = new Mock<ICodexPlanner>();
        var mockTaskList = new Mock<ITaskListService>();

        var session = new CodexSession
        {
            Id = "tasklist-plan-test",
            CurrentStage = 1,
            WorkspacePath = "/tmp/test"
        };

        var generatedPlan = new List<CodexTask>
        {
            new() { Id = "T1", Title = "Init project", Description = "Initialize project structure", Status = CodexTaskStatus.Pending },
            new() { Id = "T2", Title = "Implement core", Description = "Implement core logic", Status = CodexTaskStatus.Pending },
            new() { Id = "T3", Title = "Write tests", Description = "Write unit tests", Status = CodexTaskStatus.Pending }
        };

        mockStore.Setup(s => s.GetSessionAsync("tasklist-plan-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        mockPlanner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(generatedPlan);

        mockTaskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"sessionId\":\"tasklist-plan-test\",\"items\":[]}");

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        // Instantiate CodexOrchestrator to pass to GenerateDevPlanTool
        // Using mostly default/mocked dependencies as we only need PublishTaskListAsync -> taskList.SaveSnapshotAsync functionality
        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            Mock.Of<ICodexValidator>(),
            CreateMockGit().Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            mockTaskList.Object, // <--- Important: Pass the mock task list service here
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var tool = new GenerateDevPlanTool(
            mockPlanner.Object,
            sessionManager,
            orchestrator,
            NullLogger<GenerateDevPlanTool>.Instance);

        // Act
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = "tasklist-plan-test",
            ["requirement"] = "Build a calculator app"
        });

        output.WriteLine($"Tool result: {result.Status} - {result.Output}");

        // Assert
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("3 个任务", result.Output);

        // 关键验证：验证最终的任务清单状态被发布
        mockTaskList.Verify(
            t => t.SaveSnapshotAsync(
                It.Is<CodexSession>(s => s.Id == "tasklist-plan-test" && s.Plan.Count == 3),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce, // 改为 AtLeastOnce，因为现在会先发一次占位符快照
            "GenerateDevPlanTool 必须在计划生成后调用 SaveSnapshotAsync 发布 Redis 快照");
    }

    [Fact]
    public async Task GenerateDevPlanTool_PlanArtifactModeOn_ShouldPreviewProjectAndPublishPlanningTasks()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var session = new CodexSession
            {
                Id = "artifact-preview-session",
                CurrentStage = 1,
                WorkspacePath = workspace
            };
            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var planner = new Mock<ICodexPlanner>();
            planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([CreateValidCodeTask("T1")]);

            string? snapshot = null;
            var taskList = new Mock<ITaskListService>();
            taskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Callback<CodexSession, CancellationToken>((s, _) =>
                {
                    snapshot = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = s.Id,
                        items = s.Plan.Select(task => new { id = task.Id, status = task.Status.ToString() }).ToArray()
                    });
                })
                .ReturnsAsync(() => snapshot ?? "{}");
            taskList.Setup(t => t.GetSnapshotAsync(session.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => snapshot);

            var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
            var orchestrator = CreateOrchestrator(sessionManager, taskList.Object);
            var artifactStore = new InMemoryPlanArtifactStore();
            var tool = new GenerateDevPlanTool(
                planner.Object,
                sessionManager,
                orchestrator,
                NullLogger<GenerateDevPlanTool>.Instance,
                taskList.Object,
                Options.Create(new PlanningOptions { PlanArtifactMode = PlanningMode.On, PlanApprovalRequired = true }),
                artifactStore,
                new WorkspacePlanFileService(Options.Create(new PlanningOptions())),
                new DefaultPlanBlueprintGenerator(),
                new DefaultPlanApprovalService(artifactStore));

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = session.Id,
                ["requirement"] = "Fix projection"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("PLAN_ARTIFACT_READY_FOR_APPROVAL", result.Output);
            Assert.Single(session.Plan);
            Assert.Equal(CodexTaskStatus.Planning, session.Plan[0].Status);
            Assert.NotNull(snapshot);
            Assert.Contains("\"id\":\"T1\"", snapshot);
            Assert.Contains("\"status\":\"Planning\"", snapshot);

            var artifact = await artifactStore.GetCurrentAsync(session.Id);
            Assert.NotNull(artifact);
            Assert.Equal(PlanArtifactStatus.ReadyForApproval, artifact!.Status);
            Assert.Single(artifact.Tasks);
            Assert.NotNull(artifact.PreviewProjectedMarkdownHash);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateDevPlanTool_PlanArtifactModeOn_ShouldFallbackToMarkdownPreview_WhenPreviewProjectionReturnsEmpty()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var session = new CodexSession
            {
                Id = "artifact-preview-empty-session",
                CurrentStage = 1,
                WorkspacePath = workspace
            };
            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var planner = new Mock<ICodexPlanner>();
            planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            string? snapshot = null;
            var taskList = new Mock<ITaskListService>();
            taskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Callback<CodexSession, CancellationToken>((s, _) =>
                {
                    snapshot = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = s.Id,
                        items = s.Plan.Select(task => new { id = task.Id, status = task.Status.ToString() }).ToArray()
                    });
                })
                .ReturnsAsync(() => snapshot ?? "{}");
            var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
            var orchestrator = CreateOrchestrator(sessionManager, taskList.Object);
            var artifactStore = new InMemoryPlanArtifactStore();
            var tool = new GenerateDevPlanTool(
                planner.Object,
                sessionManager,
                orchestrator,
                NullLogger<GenerateDevPlanTool>.Instance,
                taskList.Object,
                Options.Create(new PlanningOptions { PlanArtifactMode = PlanningMode.On, PlanApprovalRequired = true }),
                artifactStore,
                new WorkspacePlanFileService(Options.Create(new PlanningOptions())),
                new DefaultPlanBlueprintGenerator(),
                new DefaultPlanApprovalService(artifactStore));

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = session.Id,
                ["requirement"] = "Fix projection"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("PLAN_ARTIFACT_READY_FOR_APPROVAL", result.Output);
            Assert.NotEmpty(session.Plan);
            Assert.All(session.Plan, task => Assert.Equal(CodexTaskStatus.Planning, task.Status));
            Assert.Contains(session.Plan, task => task.Id == "TASK-PREVIEW-001");

            var artifact = await artifactStore.GetCurrentAsync(session.Id);
            Assert.NotNull(artifact);
            Assert.Equal(PlanArtifactStatus.ReadyForApproval, artifact!.Status);
            Assert.NotEmpty(artifact.Tasks);
            Assert.NotNull(artifact.PreviewProjectedMarkdownHash);
            Assert.NotNull(snapshot);
            Assert.Contains("TASK-PREVIEW-001", snapshot);
            taskList.Verify(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task WritePlanFileTool_ShouldRepreviewAndPublishPlanningTasks_WhenMarkdownChanges()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var session = new CodexSession
            {
                Id = "artifact-write-preview-session",
                CurrentStage = 1,
                WorkspacePath = workspace,
                ProjectSummary = "CleanApp"
            };

            var artifact = new PlanArtifact
            {
                Id = "plan-write-preview",
                SessionId = session.Id,
                Goal = "Fix upload bugs",
                Markdown = "# Old plan",
                Status = PlanArtifactStatus.ReadyForApproval,
                PreviewProjectedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash("# Old plan")
            };
            artifact.ReplaceTasks([CreateValidCodeTask("OLD")]);
            session.CurrentPlanArtifactId = artifact.Id;

            var artifactStore = new InMemoryPlanArtifactStore();
            await artifactStore.SaveAsync(artifact);
            await artifactStore.SetCurrentAsync(session.Id, artifact.Id);

            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var planner = new Mock<ICodexPlanner>();
            planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.Is<string>(prompt => prompt.Contains("# Updated plan", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
                .ReturnsAsync([CreateValidCodeTask("T2")]);

            string? snapshot = null;
            var taskList = new Mock<ITaskListService>();
            taskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Callback<CodexSession, CancellationToken>((s, _) =>
                {
                    snapshot = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = s.Id,
                        items = s.Plan.Select(task => new { id = task.Id, status = task.Status.ToString() }).ToArray()
                    });
                })
                .ReturnsAsync(() => snapshot ?? "{}");

            var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
            var orchestrator = CreateOrchestrator(sessionManager, taskList.Object);
            var tool = new WritePlanFileTool(
                sessionManager,
                artifactStore,
                new WorkspacePlanFileService(Options.Create(new PlanningOptions())),
                orchestrator,
                planner.Object,
                NullLogger<WritePlanFileTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = session.Id,
                ["markdown"] = "# Updated plan\n\n- Fix upload bugs"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            var task = Assert.Single(session.Plan);
            Assert.Equal("T2", task.Id);
            Assert.Equal(CodexTaskStatus.Planning, task.Status);

            var persisted = await artifactStore.GetAsync(artifact.Id);
            Assert.NotNull(persisted);
            Assert.Single(persisted!.Tasks);
            Assert.Equal("T2", persisted.Tasks[0].Id);
            Assert.Equal(
                PlanArtifactSupport.ComputeMarkdownHash("# Updated plan\n\n- Fix upload bugs"),
                persisted.PreviewProjectedMarkdownHash);
            Assert.NotNull(snapshot);
            Assert.Contains("\"id\":\"T2\"", snapshot);
            Assert.Contains("\"status\":\"Planning\"", snapshot);
            taskList.Verify(t => t.SaveSnapshotAsync(It.Is<CodexSession>(s => s.Plan.Count == 1 && s.Plan[0].Id == "T2"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task WritePlanFileTool_ShouldFallbackToMarkdownPreview_WhenPreviewProjectionReturnsEmpty()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var session = new CodexSession
            {
                Id = "artifact-write-preview-fallback-session",
                CurrentStage = 1,
                WorkspacePath = workspace,
                ProjectSummary = "CleanApp"
            };

            var artifact = new PlanArtifact
            {
                Id = "plan-write-preview-fallback",
                SessionId = session.Id,
                Goal = "Fix upload bugs",
                Markdown = "# Old plan",
                Status = PlanArtifactStatus.ReadyForApproval
            };
            session.CurrentPlanArtifactId = artifact.Id;

            var artifactStore = new InMemoryPlanArtifactStore();
            await artifactStore.SaveAsync(artifact);
            await artifactStore.SetCurrentAsync(session.Id, artifact.Id);

            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var planner = new Mock<ICodexPlanner>();
            planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            string? snapshot = null;
            var taskList = new Mock<ITaskListService>();
            taskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .Callback<CodexSession, CancellationToken>((s, _) =>
                {
                    snapshot = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sessionId = s.Id,
                        items = s.Plan.Select(task => new { id = task.Id, title = task.Title, status = task.Status.ToString() }).ToArray()
                    });
                })
                .ReturnsAsync(() => snapshot ?? "{}");

            var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
            var orchestrator = CreateOrchestrator(sessionManager, taskList.Object);
            var tool = new WritePlanFileTool(
                sessionManager,
                artifactStore,
                new WorkspacePlanFileService(Options.Create(new PlanningOptions())),
                orchestrator,
                planner.Object,
                NullLogger<WritePlanFileTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = session.Id,
                ["markdown"] = """
# Updated plan

Step 1: Bug1 - 修改 IFileService.UploadAsync
Step 2: Bug2 - 更新 FileServiceTests
"""
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal(2, session.Plan.Count);
            Assert.Equal("TASK-PREVIEW-001", session.Plan[0].Id);
            Assert.Equal("Bug1 - 修改 IFileService.UploadAsync", session.Plan[0].Title);
            Assert.All(session.Plan, task => Assert.Equal(CodexTaskStatus.Planning, task.Status));

            var persisted = await artifactStore.GetAsync(artifact.Id);
            Assert.NotNull(persisted);
            Assert.Equal(2, persisted!.Tasks.Count);
            Assert.NotNull(persisted.PreviewProjectedMarkdownHash);
            Assert.NotNull(snapshot);
            Assert.Contains("IFileService.UploadAsync", snapshot);
            taskList.Verify(t => t.SaveSnapshotAsync(It.Is<CodexSession>(s => s.Plan.Count == 2), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// 路径 B: CodexOrchestrator.ExecuteCodeTaskAsync() 执行任务，
    /// 验证任务开始和完成时 ITaskListService.UpdateTaskStatusAsync 均被调用。
    /// </summary>
    [Fact]
    public async Task Orchestrator_ExecuteTask_Should_PublishTaskListUpdates_OnStartAndComplete()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = CreateMockGit();
        var mockTaskList = new Mock<ITaskListService>();

        var session = new CodexSession
        {
            Id = "tasklist-exec-test",
            CurrentStage = 3,
            WorkspacePath = "/tmp/test",
            Plan = {
                new CodexTask { Id = "T1", Title = "Task 1", Status = CodexTaskStatus.Pending, ComplexityLevel = 1 },
                new CodexTask { Id = "T2", Title = "Task 2", Status = CodexTaskStatus.Pending, ComplexityLevel = 1 }
            }
        };

        mockStore.Setup(s => s.GetSessionAsync("tasklist-exec-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.LearnFactAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);



        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("Done.", true, TotalToolCalls: 1, WriteToolCalls: 1));

        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "All green", []));

        // 记录所有 UpdateTaskStatusAsync 调用的 status 参数
        var statusUpdates = new List<CodexTaskStatus>();
        mockTaskList
            .Setup(t => t.UpdateTaskStatusAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CodexTaskStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<CodexSession, string, CodexTaskStatus, string?, CancellationToken>((_, _, status, _, _) => statusUpdates.Add(status))
            .ReturnsAsync("{}");
        mockTaskList
            .Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,

            mockValidator.Object,
            mockGit.Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            mockTaskList.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // 捕获事件
        var capturedEvents = new List<CodexEvent>();
        orchestrator.OnEvent += e =>
        {
            capturedEvents.Add(e);
            output.WriteLine($"[{e.Type}] {e.Message}");
        };

        // Act
        var result = await orchestrator.ExecuteCodeTaskAsync("tasklist-exec-test", "T1");

        // Assert
        Assert.True(result.Success, $"Expected success but got: {result.Message}");

        // 验证状态转换：Executing → Success
        Assert.Contains(CodexTaskStatus.Executing, statusUpdates);
        Assert.Contains(CodexTaskStatus.Success, statusUpdates);
        output.WriteLine($"Status transitions captured: {string.Join(" → ", statusUpdates)}");

        // 验证 TaskListUpdated 事件被触发
        var taskListEvents = capturedEvents.Where(e => e.Type == CodexEventType.TaskListUpdated).ToList();
        Assert.True(taskListEvents.Count >= 2, $"Expected at least 2 TaskListUpdated events (start + complete), got {taskListEvents.Count}");
        output.WriteLine($"TaskListUpdated events fired: {taskListEvents.Count}");
    }

    /// <summary>
    /// 路径 B (失败分支): 任务执行失败时 TaskList 同样应更新为 Failed。
    /// </summary>
    [Fact]
    public async Task Orchestrator_ExecuteTask_Should_PublishTaskListUpdate_OnFailure()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = CreateMockGit();
        var mockTaskList = new Mock<ITaskListService>();

        var session = new CodexSession
        {
            Id = "tasklist-fail-test",
            CurrentStage = 3,
            WorkspacePath = "/tmp/test",
            Plan = { new CodexTask { Id = "T1", Title = "Failing task", Status = CodexTaskStatus.Pending, ComplexityLevel = 1 } }
        };

        mockStore.Setup(s => s.GetSessionAsync("tasklist-fail-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.LearnFactAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);



        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("Done.", true, TotalToolCalls: 1, WriteToolCalls: 1));

        // 验证失败
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(false, "Build errors found", ["CS1002: Missing semicolon"]));

        var failedStatusCaptured = false;
        mockTaskList
            .Setup(t => t.UpdateTaskStatusAsync(It.IsAny<CodexSession>(), "T1", CodexTaskStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => failedStatusCaptured = true)
            .ReturnsAsync("{}");
        mockTaskList
            .Setup(t => t.UpdateTaskStatusAsync(It.IsAny<CodexSession>(), "T1", CodexTaskStatus.Executing, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,

            mockValidator.Object,
            mockGit.Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            mockTaskList.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        orchestrator.OnEvent += e => output.WriteLine($"[{e.Type}] {e.Message}");

        // Act
        var result = await orchestrator.ExecuteCodeTaskAsync("tasklist-fail-test", "T1");

        // Assert
        Assert.False(result.Success);
        Assert.True(failedStatusCaptured, "TaskListService.UpdateTaskStatusAsync must be called with Failed status");

        // 验证 Failed 事件被推送
        mockTaskList.Verify(
            t => t.UpdateTaskStatusAsync(
                It.IsAny<CodexSession>(), "T1", CodexTaskStatus.Failed,
                It.Is<string?>(msg => msg != null && msg.Contains("Build errors")),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "失败原因应传递给 UpdateTaskStatusAsync 的 errorMessage 参数");
    }

    /// <summary>
    /// 路径 C: RunNextStepAsync 走 Legacy 路径（Stage 1 → 自动规划），
    /// 验证计划生成后 TaskList 快照被发布。
    /// </summary>
    [Fact]
    public async Task Orchestrator_RunNextStep_Should_PublishTaskList_WhenPlanGenerated()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));
        var mockGit = CreateMockGit();
        var mockTaskList = new Mock<ITaskListService>();

        var tempWorkspace = Path.Combine(Path.GetTempPath(), "CodexTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempWorkspace);

        var session = new CodexSession
        {
            Id = "tasklist-legacy-test",
            CurrentStage = 1,
            WorkspacePath = tempWorkspace
        };

        mockStore.Setup(s => s.GetSessionAsync("tasklist-legacy-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.LearnFactAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        mockArchitect.Setup(k => k.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Project analyzed.");

        mockArchitect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new CodexTask { Id = "T1", Title = "Setup", Status = CodexTaskStatus.Pending },
                new CodexTask { Id = "T2", Title = "Implement", Status = CodexTaskStatus.Pending }
            ]);

        mockTaskList
            .Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            mockArchitect.Object,
            mockSecurity.Object,
            Mock.Of<ICodexValidator>(),
            mockGit.Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            mockTaskList.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var taskListEvents = new List<CodexEvent>();
        orchestrator.OnEvent += e =>
        {
            if (e.Type == CodexEventType.TaskListUpdated) taskListEvents.Add(e);
            output.WriteLine($"[{e.Type}] {e.Message}");
        };

        // Act
        var result = await orchestrator.RunNextStepAsync("tasklist-legacy-test", "Build a todo app");

        // Assert — 计划已生成
        Assert.Equal(2, session.Plan.Count);

        // 关键验证：SaveSnapshotAsync 在 Legacy 路径的计划生成阶段被调用
        mockTaskList.Verify(
            t => t.SaveSnapshotAsync(
                It.Is<CodexSession>(s => s.Plan.Count == 2),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "RunNextStepAsync Legacy 路径生成计划后必须调用 SaveSnapshotAsync");

        // 验证 TaskListUpdated 事件
        Assert.NotEmpty(taskListEvents);
        output.WriteLine($"TaskListUpdated events: {taskListEvents.Count}");
    }

    // A model that plans eagerly while only answering a question leaves an unapproved draft behind. The user's real plan request must then replace
    // that draft; before, "请先完成当前计划再重新规划" blocked it and the user had no way to clear the draft. An approved plan stays protected.
    private async Task<(GenerateDevPlanTool Tool, CodexSession Session, InMemoryPlanArtifactStore Store, Mock<ICodexPlanner> Planner)> CreatePlanToolAsync(string sessionId, string workspace)
    {
        var session = new CodexSession { Id = sessionId, CurrentStage = 1, WorkspacePath = workspace };
        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var planner = new Mock<ICodexPlanner>();
        var planCalls = 0;
        planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [CreateValidCodeTask("T" + (++planCalls))]);

        string? snapshot = null;
        var taskList = new Mock<ITaskListService>();
        taskList.Setup(t => t.SaveSnapshotAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Callback<CodexSession, CancellationToken>((s, _) => snapshot = System.Text.Json.JsonSerializer.Serialize(new { sessionId = s.Id, items = s.Plan.Select(task => new { id = task.Id }).ToArray() }))
            .ReturnsAsync(() => snapshot ?? "{}");
        taskList.Setup(t => t.GetSnapshotAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => snapshot);

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        var artifactStore = new InMemoryPlanArtifactStore();
        var tool = new GenerateDevPlanTool(
            planner.Object,
            sessionManager,
            CreateOrchestrator(sessionManager, taskList.Object),
            NullLogger<GenerateDevPlanTool>.Instance,
            taskList.Object,
            Options.Create(new PlanningOptions { PlanArtifactMode = PlanningMode.On, PlanApprovalRequired = true }),
            artifactStore,
            new WorkspacePlanFileService(Options.Create(new PlanningOptions())),
            new DefaultPlanBlueprintGenerator(),
            new DefaultPlanApprovalService(artifactStore));
        await Task.CompletedTask;
        return (tool, session, artifactStore, planner);
    }

    [Fact]
    public async Task GenerateDevPlanTool_ReplacesAnUnapprovedDraft_WithTheNewRequirement()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var (tool, session, store, _) = await CreatePlanToolAsync("draft-replace-session", workspace);

            var first = await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = session.Id, ["requirement"] = "just a question" });
            var draft = await store.GetCurrentAsync(session.Id);
            Assert.Contains("PLAN_ARTIFACT_READY_FOR_APPROVAL", first.Output);
            Assert.Equal(PlanArtifactStatus.ReadyForApproval, draft!.Status);

            var second = await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = session.Id, ["requirement"] = "the real plan: upgrade 7 packages" });

            Assert.Contains("PLAN_ARTIFACT_READY_FOR_APPROVAL", second.Output);
            Assert.DoesNotContain("请先完成当前计划再重新规划", second.Output, StringComparison.Ordinal);
            var current = await store.GetCurrentAsync(session.Id);
            Assert.NotEqual(draft.Id, current!.Id);
            Assert.Equal("the real plan: upgrade 7 packages", current.Goal);
            Assert.Equal(draft.Id, current.ParentPlanId);
            var old = await store.GetAsync(draft.Id);
            Assert.Equal(PlanArtifactStatus.Superseded, old!.Status);
            Assert.Equal(current.Id, old.SupersededByPlanId);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateDevPlanTool_StillProtectsAnApprovedPlan()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var (tool, session, store, planner) = await CreatePlanToolAsync("approved-protect-session", workspace);
            await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = session.Id, ["requirement"] = "first" });
            var approved = await store.GetCurrentAsync(session.Id);
            approved!.Status = PlanArtifactStatus.ReadyToExecute;
            await store.SaveAsync(approved);
            session.Plan[0].Status = CodexTaskStatus.Pending; // an approved plan's tasks are ready to run

            var again = await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = session.Id, ["requirement"] = "second" });

            Assert.Contains("请先完成当前计划再重新规划", again.Output, StringComparison.Ordinal);
            planner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(approved.Id, (await store.GetCurrentAsync(session.Id))!.Id);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(PlanArtifactStatus.ReadyForApproval, CodexTaskStatus.Planning, true)]
    [InlineData(PlanArtifactStatus.Draft, CodexTaskStatus.Planning, true)]
    [InlineData(PlanArtifactStatus.NeedsClarification, CodexTaskStatus.Planning, true)]
    [InlineData(PlanArtifactStatus.ReadyForApproval, CodexTaskStatus.Pending, false)]
    [InlineData(PlanArtifactStatus.Approved, CodexTaskStatus.Planning, false)]
    [InlineData(PlanArtifactStatus.ReadyToExecute, CodexTaskStatus.Pending, false)]
    [InlineData(PlanArtifactStatus.Executing, CodexTaskStatus.Executing, false)]
    public void IsUnapprovedDraft_DependsOnTheArtifactAndTaskStatuses(PlanArtifactStatus artifact, CodexTaskStatus task, bool expected)
    {
        var plan = new[] { new CodexTask { Id = "T1", Status = task } };

        Assert.Equal(expected, CodexPlanStateGuards.IsUnapprovedDraft(plan, artifact));
    }

    [Fact]
    public void IsUnapprovedDraft_IsFalse_WithoutAnArtifact_OrWhileAPlanIsBeingGenerated()
    {
        var preview = new[] { new CodexTask { Id = "T1", Status = CodexTaskStatus.Planning } };
        var generating = new[] { new CodexTask { Id = CodexPlanStateGuards.PlanGenerationPlaceholderTaskId, Status = CodexTaskStatus.Planning } };

        Assert.False(CodexPlanStateGuards.IsUnapprovedDraft(preview, null));
        Assert.False(CodexPlanStateGuards.IsUnapprovedDraft(generating, PlanArtifactStatus.ReadyForApproval));
        Assert.False(CodexPlanStateGuards.IsUnapprovedDraft([], PlanArtifactStatus.ReadyForApproval));
    }

    private CodexOrchestrator CreateOrchestrator(CodexSessionManager sessionManager, ITaskListService taskListService)
    {
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        return new CodexOrchestrator(
            sessionManager,
            Mock.Of<ICodexAgentKernel>(),
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            Mock.Of<ICodexValidator>(),
            CreateMockGit().Object,
            CreateMockScanner(),
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            CreateMockSemanticDiff(),
            taskListService,
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());
    }

    private static CodexTask CreateValidCodeTask(string id)
    {
        var task = new CodexTask
        {
            Id = id,
            Title = "Implement change",
            Description = "Apply the requested change.",
            TaskType = CodexTaskClassifier.CodeTaskType,
            Status = CodexTaskStatus.Pending,
            RiskLevel = "Low",
            UnsafeIfDependencyFallbackPassed = true
        };
        task.ChecklistItems.Add(new TaskChecklistItem { Id = "CHK_001", Text = "Run build" });
        return task;
    }

    private static string CreateTempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexflow-tasklist-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private ProjectScanner CreateMockScanner()
    {
        var mock = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        mock.Setup(s => s.GenerateFileIndexAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<FileIndexEntry>());
        return mock.Object;
    }


    private ISemanticDiffService CreateMockSemanticDiff()
    {
        var mock = new Mock<ISemanticDiffService>();
        mock.Setup(s => s.AnalyzeDiffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticDiffResult { HasChanges = false });
        // Also mock other methods if necessary, but AnalyzeDiffAsync is the one causing NRE.
        return mock.Object;
    }


    private Mock<IGitService> CreateMockGit()
    {
        var mock = new Mock<IGitService>();
        mock.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("/tmp/test/shadow/T1");
        mock.Setup(g => g.InitRepositoryAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        mock.Setup(g => g.CommitAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        mock.Setup(g => g.MergeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        mock.Setup(g => g.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<GitFileChange>());
        mock.Setup(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        mock.Setup(g => g.RemoveShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        return mock;
    }
}
