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

public class Stage1_5PerceptionTests
{
    [Fact]
    public async Task RunNextStepAsync_ShouldExecuteScanner_InStage1_5()
    {
        // Arrange: a unique, really existing workspace. Stage 1 writes implementation_plan.md through the identity-checked CAS
        // writer, so a shared hard-coded /tmp (or a missing directory) makes the result depend on other tests and on leftovers.
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "cf-stage15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        using var cleanup = new TempRoot(workspaceRoot);
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockArch = new Mock<IArchitectureService>();

        var sessionId = "demo-session";
        var session = new CodexSession 
        { 
            Id = sessionId, 
            CurrentStage = 1, 
            WorkspacePath = workspaceRoot,
            ProjectUrl = new Uri("https://github.com/test/repo")
        };
        
        mockStore.Setup(s => s.GetSessionAsync(sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        
        mockArchitect.Setup(a => a.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Project Summary Text");

        mockScanner.Setup(s => s.ScanAndSummarizeAsync(It.IsAny<string>()))
                   .ReturnsAsync("Project technical fingerprint report content");

        // Mock DevPlan for Level 7 Hard Gates
        mockArchitect.Setup(a => a.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<CodexTask> 
                   { 
                       new CodexTask { Id = "1", Title = "Task 1", Status = CodexTaskStatus.Pending } 
                   });

        var semanticScanner = new SemanticDependencyScanner(NullLogger<SemanticDependencyScanner>.Instance);
        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        var orchestrator = new CodexOrchestrator(
            sessionManager, 
            mockKernel.Object, 
            mockArchitect.Object,
            mockSecurity.Object,
            mockValidator.Object, 
            mockGit.Object, 
            mockScanner.Object, 
            semanticScanner, 
            mockArch.Object, 
            Mock.Of<ICodexTestDesigner>(), 
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act
        var result = await orchestrator.RunNextStepAsync(sessionId, "Analyze requirements");

        // Assert
        // Verify Scanner was called
        mockScanner.Verify(s => s.ScanAndSummarizeAsync(It.IsAny<string>()), Times.Once);

        // the plan artifact really reached this test's own workspace through the CAS writer
        var planFile = Path.Combine(workspaceRoot, "implementation_plan.md");
        Assert.True(File.Exists(planFile), "implementation_plan.md was not written into the test workspace");
        Assert.Equal("Project Summary Text", File.ReadAllText(planFile));
        Assert.Empty(Directory.EnumerateFiles(workspaceRoot, ".cfx-*", SearchOption.AllDirectories));

        // Verify fingerprint was saved as a fact
        // Phase 2: category is now "project" (was "general") and metadata is always populated
        mockMemory.Verify(m => m.LearnFactAsync(sessionId, "ProjectFingerprint", "Project technical fingerprint report content", "project", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
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
