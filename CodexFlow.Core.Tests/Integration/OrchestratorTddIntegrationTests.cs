// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.Core.Tests.Integration;

public class OrchestratorTddIntegrationTests
{
    [Fact]
    public async Task Orchestrator_Should_Trigger_TDD_For_Level2_Task()
    {
        // Arrange: a REAL, unique temp workspace + shadow worktree. The TDD stage writes the generated test through the
        // identity-checked CAS writer, which (correctly) requires an existing root; a hard-coded /tmp path only passed on hosts
        // where an earlier run had left the directory behind.
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "cf-tdd-" + Guid.NewGuid().ToString("N"));
        var shadowRoot = Path.Combine(workspaceRoot, "shadow", "TASK-001");
        Directory.CreateDirectory(shadowRoot);
        using var cleanup = new TempRoot(workspaceRoot);

        // Arrange: Mocks
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var kernel = new Mock<ICodexAgentKernel>();
        var architect = new Mock<ICodexArchitect>();
        var security = new Mock<ICodexSecurityAuditor>();
        security.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));
        var validator = new Mock<ICodexValidator>();
        var gitService = new Mock<IGitService>();
        var scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance); 
        var semanticScanner = new Mock<ICodeAnalysisService>();
        var archService = new Mock<IArchitectureService>();
        var testDesigner = new Mock<ICodexTestDesigner>();
        var logger = NullLogger<CodexOrchestrator>.Instance;

        // Arrange: Session State (Stage 3 ready)
        var session = new CodexSession 
        { 
            Id = "session-tdd-demo",
            CurrentStage = 3,
            WorkspacePath = workspaceRoot,
            Plan = {
                new CodexTask 
                { 
                    Id = "TASK-001", 
                    Title = "Impl Calculator", 
                    Description = "Implement Add method and add Tests/CalculatorTests.cs",
                    ComplexityLevel = 2, // Level 2 triggers TDD
                    Status = CodexTaskStatus.Pending 
                }
            }
        };

        mockStore.Setup(s => s.GetSessionAsync("session-tdd-demo", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync("session-tdd-demo", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<MemoryEntry>());

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Mock Git Shadow
        gitService.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), "TASK-001", It.IsAny<string>()))
            .ReturnsAsync(shadowRoot); // Return valid path (exists, see above)
        gitService.Setup(g => g.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<GitFileChange>());
        gitService.Setup(g => g.MergeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        gitService.Setup(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        gitService.Setup(g => g.RemoveShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Mock DevPlan for Level 7 Hard Gates - Must return concrete List<CodexTask>
        architect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new List<CodexTask> 
               { 
                   new CodexTask { Id = "TASK-001", Title = "Impl Calculator", Status = CodexTaskStatus.Pending } 
               });

        // Mock TDD Designer Response
        testDesigner.Setup(t => t.DesignTestsAsync(It.IsAny<CodexTask>(), It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestPlan 
            { 
                TaskId = "TASK-001",
                Framework = "xUnit",
                TestFiles = {
                    new TestFile { FilePath = "Tests/CalculatorTests.cs", Content = "public class CalculatorTests {}" } 
                }
            });

        // Mock Kernel (Worker)
        kernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("Code implemented.", true, TotalToolCalls: 1, WriteToolCalls: 1));

        // Mock Validator
        validator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "All green", new List<string>()));

        // Mock Policy Validator
        var mockPolicy = new Mock<IPolicyValidator>();
        mockPolicy.Setup(p => p.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new PolicyResult(true, "Policy Passed", new List<RuleResult>()));

        var orchestrator = new CodexOrchestrator(
            sessionManager, 
            kernel.Object, 
            architect.Object,
            security.Object,
            validator.Object, 
            gitService.Object, 
            scanner.Object, 
            semanticScanner.Object, 
            archService.Object, 
            testDesigner.Object, 
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            logger,
            Enumerable.Empty<ICodexEventSink>());

        // Capture Events for Observability
        var capturedEvents = new List<CodexEvent>();
        orchestrator.OnEvent += e => 
        {
            capturedEvents.Add(e);
            Console.WriteLine($"[OBSERVED] {e.Timestamp:HH:mm:ss} [{e.Type}] {e.Message}");
            if (e.Payload != null)
            {
                Console.WriteLine($"   Payload: {e.Payload}");
            }
        };

        // Act
        var result = await orchestrator.RunNextStepAsync("session-tdd-demo", "Go", taskId: "TASK-001");

        // Assert
        Assert.True(result.Success, $"Orchestrator failed: {result.Message}");
        
        // 验证 TestDesigner 被调用
        testDesigner.Verify(t => t.DesignTestsAsync(It.Is<CodexTask>(task => task.Id == "TASK-001"), It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()), Times.Once);

        // 验证 TDD 相关事件被触发
        Assert.Contains(capturedEvents, e => e.Message.Contains("[TDD] 任务复杂度为 2"));
        Assert.Contains(capturedEvents, e => e.Message.Contains("生成了 1 个测试文件"));

        // the generated test really reached the shadow worktree through the CAS writer, with its exact content
        var written = Path.Combine(shadowRoot, "Tests", "CalculatorTests.cs");
        Assert.True(File.Exists(written), "the TDD test file was not written into the shadow worktree");
        Assert.Equal("public class CalculatorTests {}", File.ReadAllText(written));
        Assert.Empty(Directory.EnumerateFiles(shadowRoot, ".cfx-*", SearchOption.AllDirectories));   // no temp/backup leftovers
    }

    private sealed class TempRoot(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
