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
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class CausalHealingTests
{
    [Fact]
    public async Task RunNextStepAsync_ShouldProvideImpactedFiles_WhenValidationFails()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();

        var task = new CodexTask { Id = "t1", Title = "Update API", Status = CodexTaskStatus.Pending };
        var session = new CodexSession { Id = "s1", CurrentStage = 3, Plan = { task }, WorkspacePath = "/tmp" };
        
        mockStore.Setup(s => s.GetSessionAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        // Mock Architect for Plan
        mockArchitect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<CodexTask> { task });
        
        // Mock Forge for execution
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                  .ReturnsAsync(new CodexResponse("Done", true, TotalToolCalls: 1, WriteToolCalls: 1));
        
        // 模拟验证失败，且指出了错误文件
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new ValidationResult(false, "Build error in BaseService.cs", new List<string> { "BaseService.cs" }));
        
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync("/tmp/shadow");

        // 模拟语义扫描，发现 BaseService.cs 影响了下游
        var graph = new DependencyGraph();
        mockAnalysis.Setup(a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(graph);
        mockAnalysis.Setup(a => a.GetImpactedFiles(graph, "BaseService.cs"))
                    .Returns(new List<string> { "AuthService.cs", "UserController.cs" });

        // Mock TestDesigner to return empty plan
        var mockTestDesigner = new Mock<ICodexTestDesigner>();
        mockTestDesigner.Setup(d => d.DesignTestsAsync(It.IsAny<CodexTask>(), It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new TestPlan());

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        
        // Mock Policy & SemanticDiff
        var mockPolicy = new Mock<IPolicyValidator>();
        mockPolicy.Setup(p => p.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new PolicyResult(true, "OK", []));

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
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act
        var result = await orchestrator.RunNextStepAsync("s1", "Execute");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("验证失败", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
