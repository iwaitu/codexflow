// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class ChatContextSimulationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testWorkspace;

    public ChatContextSimulationTests(ITestOutputHelper output)
    {
        _output = output;
        _testWorkspace = Path.Combine(Path.GetTempPath(), $"codex_sim_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testWorkspace);
        _output.WriteLine($"Simulation workspace created: {_testWorkspace}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testWorkspace)) Directory.Delete(_testWorkspace, true); } catch { }
    }

    [Fact]
    public async Task Orchestrator_ShouldProcessBookSystemConversation_Context()
    {
        _output.WriteLine("=== [Simulation] CodexFlow Level 7 Integrated Flow Test ===");

        // 1. Mock Infrastructure
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockTestDesigner = new Mock<ICodexTestDesigner>();

        // 2. Setup Session based on Database Content
        var sessionId = "f84d14ef900246219058b08bbab53f71";
        var session = new CodexSession
        {
            Id = sessionId,
            UserId = "ed7177e4-5cd2-40b0-95a5-782782c461db",
            WorkspacePath = _testWorkspace,
            CurrentStage = 1 // Start from Stage 1 as it would in a clean run
        };

        mockStore.Setup(s => s.GetSessionAsync(sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(sessionId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<MemoryEntry>());

        // 3. Inject Conversation Context (Short-term memory)
        var messages = new List<ChatTurn>
        {
            new ChatTurn("User", "用.net 开发一个图书管理系统的后端api", DateTime.UtcNow.AddMinutes(-10)),
            new ChatTurn("Assistant", "用户意图已明确... 1.项目结构... 2.功能需求...", DateTime.UtcNow.AddMinutes(-9)),
            new ChatTurn("User", "1、新建项目；2、全选；3、dotnet 8/ASP.NET Core Web API /PostgreSQL/使用 Entity Framework Core；4.本地", DateTime.UtcNow.AddMinutes(-5)),
            new ChatTurn("Assistant", "### ✅ 任务摘要... 请回复 “确认”", DateTime.UtcNow.AddMinutes(-4)),
            new ChatTurn("User", "确认", DateTime.UtcNow.AddMinutes(-1))
        };

        // Mock AppendTurn and Recall to simulate persistent chat history
        var history = new List<ChatTurn>();
        mockMemory.Setup(m => m.AppendTurnAsync(sessionId, It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>()))
                  .Callback<string, ChatTurn, CancellationToken>((id, turn, ct) => history.Add(turn));

        // When GetFullContext is called, Orchestrator expects facts and history
        // Here we simulate the state after "确认"
        session.ProjectSummary = "Book Management System: .NET 8, Web API, PostgreSQL, EF Core.";
        
        // 4. Mock Orchestrator Steps
        
        // Stage 1 (Analysis) Mock
        // Stage 1 (Analysis) Mock
        mockArchitect.Setup(k => k.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Summary confirmed. Moving to planning.");

        // Stage 2 (Planning) Mock
        var plannedTasks = new List<CodexTask>
        {
            new CodexTask { Id = "T1", Title = "Init Project", Description = "Create .NET WebAPI project", ComplexityLevel = 1, Status = CodexTaskStatus.Pending },
            new CodexTask { Id = "T2", Title = "Config DB", Description = "Add PG connection and EF context", ComplexityLevel = 2, Status = CodexTaskStatus.Pending }
        };
        // Mock DevPlan for Level 7 Hard Gates - Must return concrete List<CodexTask>
        // Mock DevPlan for Level 7 Hard Gates - Must return concrete List<CodexTask>
        mockArchitect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(plannedTasks);

        // Stage 3 (Execution) Mock - Task T1
        mockGit.Setup(g => g.InitRepositoryAsync(It.IsAny<string>())).ReturnsAsync(true);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                  .ReturnsAsync(new CodexResponse("Task T1 executed successfully.", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new ValidationResult(true, "Validation passed", new List<string>()));

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            mockArchitect.Object,
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            mockTestDesigner.Object,
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // 5. Execute RunNextStep with the final prompt
        _output.WriteLine("--- Running Orchestrator.RunNextStepAsync ---");
        var result = await orchestrator.RunNextStepAsync(sessionId, "确认", session.UserId, _testWorkspace);

        // 6. Assertions & Analysis
        _output.WriteLine($"Orchestrator Result Success: {result.Success}");
        _output.WriteLine($"Orchestrator Result Message: {result.Message}");
        _output.WriteLine($"Current Session Stage: {session.CurrentStage}");
        
        // Verify flow: Stage 1 Analysis -> Stage 2 Planning -> Stage 3 Execution (Task T1)
        Assert.True(result.Success);
        Assert.Equal(3, session.CurrentStage); // Should be in Stage 3 or finished Task 1
        
        _output.WriteLine("Analysis: The flow successfully transitioned from confirmation to execution.");
        _output.WriteLine("Recommendation: Level 7 orchestration requires persistent context of tool outputs to avoid re-analysing on every turn.");
    }
}
