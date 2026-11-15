// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Reflection;

namespace CodexFlow.Core.Tests.Agents;

public class OrchestratorTests
{
    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldBlockAllTasks_WhenPlanPatchIsPending()
    {
        var session = new CodexSession
        {
            Id = "s-patch-pending",
            CurrentStage = 3,
            WorkspacePath = TestConstants.GetWorkspacePath(),
            Plan =
            {
                new CodexTask { Id = "t-patch", Title = "Patch task", Status = CodexTaskStatus.Failed },
                new CodexTask { Id = "t-other", Title = "Other task", Status = CodexTaskStatus.Pending }
            },
            Metadata =
            {
                [PlanGuardMetadataKeys.PlanPatchPending] = bool.TrueString,
                [PlanGuardMetadataKeys.PlanPatchTaskId] = "t-patch",
                [PlanGuardMetadataKeys.PlanPatchReason] = "scope drift"
            }
        };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockGit = new Mock<IGitService>();
        mockStore.Setup(s => s.GetSessionAsync("s-patch-pending", It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            Mock.Of<ICodexAgentKernel>(),
            Mock.Of<ICodexArchitect>(),
            Mock.Of<ICodexSecurityAuditor>(),
            Mock.Of<ICodexValidator>(),
            mockGit.Object,
            new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance).Object,
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("s-patch-pending", "t-other");

        Assert.False(result.Success);
        Assert.Contains("已暂停所有任务执行", result.Message, StringComparison.Ordinal);
        mockGit.Verify(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldNotPush_WhenConsentIsNotGranted()
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var task = new CodexTask { Id = "t-no-push", Title = "Task", Status = CodexTaskStatus.Pending };
        var session = new CodexSession
        {
            Id = "s-no-push",
            CurrentStage = 3,
            WorkspacePath = tempWorkspace,
            Plan = { task },
            Metadata = { ["GitPushConsentOnce"] = "false" }
        };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        mockStore.Setup(s => s.GetSessionAsync("s-no-push", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("ok", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "ok", []));

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:AutoPush"] = "false"
            })
            .Build();

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            configuration,
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("s-no-push", "t-no-push");

        Assert.True(result.Success, result.Message);
        mockGit.Verify(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldPushToIcodexBranch_WhenConsentIsGranted()
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var task = new CodexTask { Id = "T1", Title = "Task", Status = CodexTaskStatus.Pending };
        var session = new CodexSession
        {
            Id = "session-abc",
            CurrentStage = 3,
            WorkspacePath = tempWorkspace,
            Plan = { task },
            Metadata = { ["GitPushConsentOnce"] = "true" }
        };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        mockStore.Setup(s => s.GetSessionAsync("session-abc", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        mockGit.Setup(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("ok", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "ok", []));

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:AutoPush"] = "false"
            })
            .Build();

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            configuration,
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("session-abc", "T1");

        Assert.True(result.Success, result.Message);
        mockGit.Verify(g => g.PushAsync(It.IsAny<string>(), It.Is<string>(b => b.StartsWith("icodex-", StringComparison.OrdinalIgnoreCase))), Times.Once);
        Assert.True(result.Session.Metadata.TryGetValue("GitPushConsentOnce", out var consumed) && consumed == "false");
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldNotPush_WhenConsentGrantedButPlanNotFullyCompleted()
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var task1 = new CodexTask { Id = "T1", Title = "Task 1", Status = CodexTaskStatus.Pending };
        var task2 = new CodexTask { Id = "T2", Title = "Task 2", Status = CodexTaskStatus.Pending };
        var session = new CodexSession
        {
            Id = "session-not-complete",
            CurrentStage = 3,
            WorkspacePath = tempWorkspace,
            Plan = { task1, task2 },
            Metadata = { ["GitPushConsentOnce"] = "true" }
        };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        mockStore.Setup(s => s.GetSessionAsync("session-not-complete", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("ok", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "ok", []));

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("session-not-complete", "T1");

        Assert.True(result.Success, result.Message);
        mockGit.Verify(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.True(result.Session.Metadata.TryGetValue("GitPushConsentOnce", out var stillGranted) && stillGranted == "true");
    }

    [Fact]
    public async Task RunNextStepAsync_ShouldTransitionToPlanning_WhenInStage1()
    {
        // Arrange
        var tempWorkspace = TestConstants.GetWorkspacePath();
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
        var mockPolicy = new Mock<IPolicyValidator>();

        var session = new CodexSession { Id = "s1", CurrentStage = 1, WorkspacePath = tempWorkspace };
        mockStore.Setup(s => s.GetSessionAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        
        mockArchitect.Setup(a => a.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Project Summary Text");

        // Mock DevPlan for Level 7 Hard Gates
        mockArchitect.Setup(a => a.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<CodexTask> 
                   { 
                       new CodexTask { Id = "t1", Title = "Task 1", Status = CodexTaskStatus.Pending } 
                   });

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
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
            Mock.Of<ICodexTestDesigner>(), 
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act
        var result = await orchestrator.RunNextStepAsync("s1", "Analyze requirements");

        // Assert
        // Compatibility mode pushes straight to stage 3 if tasks are pending
        Assert.True(result.Session.CurrentStage >= 2); 
        // Assert.StartsWith("[项目模式]", result.Session.ProjectSummary);
        Assert.Contains("Project Summary Text", result.Session.ProjectSummary);
        mockStore.Verify(s => s.SaveSessionAsync(It.Is<CodexSession>(ss => ss.CurrentStage >= 2), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunNextStepAsync_ShouldExecuteTask_AndValidate_WhenInStage3()
    {
        // Arrange
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        var task = new CodexTask { Id = "t1", Title = "Task 1", Status = CodexTaskStatus.Pending };
        var session = new CodexSession { Id = "s1", CurrentStage = 3, Plan = { task }, WorkspacePath = tempWorkspace };
        
        mockStore.Setup(s => s.GetSessionAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        // Mock Forge for execution
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                  .ReturnsAsync(new CodexResponse("Execution Result", true, TotalToolCalls: 1, WriteToolCalls: 1));
                  
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new ValidationResult(true, "All Good", []));
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync("/tmp/shadow");
         mockGit.Setup(g => g.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
             .ReturnsAsync(Array.Empty<GitFileChange>());
         mockGit.Setup(g => g.MergeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
             .ReturnsAsync(true);
         mockGit.Setup(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>()))
             .ReturnsAsync(true);

        // Mock Policy to pass
        mockPolicy.Setup(p => p.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new PolicyResult(true, "Policy OK", []));

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
            mockScanner.Object, 
            mockAnalysis.Object, 
            mockArch.Object, 
            Mock.Of<ICodexTestDesigner>(), 
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act
        // Pass taskId explicitly to avoid ambiguity and test Execution stage specifically
        var result = await orchestrator.RunNextStepAsync("s1", "Execute next", taskId: "t1");

        // Assert
        // In Level 7, when a specific TaskId is provided, the Orchestrator executes it directly.
        // It does not rely on "Success" property of the result object alone if the session context matters.
        // However, ExecuteCodeTaskAsync returns OrchestratorResult(true, "...", session) on success.
        
        // Ensure mock returns successful Execution
        Assert.True(result.Success, $"Orchestrator failed: {result.Message}");
        Assert.Equal(CodexTaskStatus.Success, result.Session.Plan.First().Status);
        mockValidator.Verify(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldMarkTaskCompletedWithWarnings_WhenValidatorReportsWarnings()
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var task = new CodexTask { Id = "t-warning", Title = "Task Warning", Status = CodexTaskStatus.Pending };
        var session = new CodexSession { Id = "s-warning", CurrentStage = 3, Plan = { task }, WorkspacePath = tempWorkspace };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        mockStore.Setup(s => s.GetSessionAsync("s-warning", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        mockGit.Setup(g => g.CommitAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("ok", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "ok", [], HasQualityWarnings: true, WarningDetails: ["warning CS0108"]));
        mockGit.Setup(g => g.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<GitFileChange>());
        mockGit.Setup(g => g.PushAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("s-warning", "t-warning");

        Assert.True(result.Success, result.Message);
        Assert.Equal(CodexTaskStatus.CompletedWithWarnings, result.Session.Plan.First().Status);
        Assert.Contains("warning CS0108", result.Session.Plan.First().ResultNotes ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldTrackPendingDocumentationSync_WhenArchitectureFilesChanged()
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var task = new CodexTask { Id = "t-docs", Title = "Task Docs", Status = CodexTaskStatus.Pending };
        var session = new CodexSession { Id = "s-docs", CurrentStage = 3, Plan = { task }, WorkspacePath = tempWorkspace };

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        mockStore.Setup(s => s.GetSessionAsync("s-docs", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockGit.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        mockGit.Setup(g => g.GetHeadHashAsync(It.IsAny<string>())).ReturnsAsync("base");
        mockGit.Setup(g => g.CommitAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        mockGit.Setup(g => g.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(
            [
                new GitFileChange("CodexFlow.Core/Services/NewService.cs", "M")
            ]);
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("ok", true, TotalToolCalls: 1, WriteToolCalls: 1));
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "ok", []));

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        var result = await orchestrator.ExecuteCodeTaskAsync("s-docs", "t-docs");

        Assert.True(result.Success, result.Message);
        Assert.Equal("pending", result.Session.Metadata["DocumentationSyncStatus"]);
        Assert.Contains("README.md", result.Session.Metadata["DocumentationSyncPending"], StringComparison.Ordinal);
        Assert.Contains(".github/copilot-instructions.md", result.Session.Metadata["DocumentationSyncPending"], StringComparison.Ordinal);
        Assert.Contains("PROJECT_SUMMARY.md", result.Session.Metadata["DocumentationSyncPending"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVerificationSummary_ShouldIncludePendingDocumentationSync()
    {
        var session = new CodexSession
        {
            Id = "s-summary",
            CurrentStage = 4
        };
        session.ReplacePlan([new CodexTask { Id = "T1", Status = CodexTaskStatus.Success }]);
        session.Metadata["DocumentationSyncStatus"] = "pending";
        session.Metadata["DocumentationSyncPending"] = "README.md;PROJECT_SUMMARY.md";

        var method = typeof(CodexOrchestrator).GetMethod("BuildVerificationSummary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var summary = (string)method!.Invoke(null, [session])!;

        Assert.Contains("文档同步：待同步 README.md, PROJECT_SUMMARY.md", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCodeTaskAsync_ShouldInjectProjectFileIndexIntoForgePrompt()
    {
        // Arrange
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var projectPath = Path.Combine(tempWorkspace, "SampleProject");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(Path.Combine(projectPath, "Program.cs"), "class Program {}\n");

        var facts = new List<MemoryEntry>();

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockPolicy = new Mock<IPolicyValidator>();

        var task = new CodexTask { Id = "t-index", Title = "Index Task", Status = CodexTaskStatus.Pending };
        var session = new CodexSession { Id = "s-index", CurrentStage = 3, Plan = { task }, WorkspacePath = tempWorkspace };

        mockStore.Setup(s => s.GetSessionAsync("s-index", It.IsAny<CancellationToken>())).ReturnsAsync(session);

        mockMemory.Setup(m => m.RecallFactsAsync("s-index", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => facts.ToList());
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
        mockMemory.Setup(m => m.LearnFactAsync("s-index", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string?, CancellationToken>((_, key, value, category, _, _) =>
            {
                facts.RemoveAll(f => f.Key == key);
                facts.Add(new MemoryEntry(key, value, category, DateTime.UtcNow));
            })
            .Returns(Task.CompletedTask);

        mockScanner.Setup(s => s.GenerateFileIndexAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<FileIndexEntry>
            {
                new FileIndexEntry { Path = "Program.cs", Type = "C# Source", Size = 12 }
            });

        mockKernel.Setup(k => k.RunLoopAsync(
                It.IsAny<CodexSession>(),
                It.Is<string>(prompt => prompt.Contains("ProjectFileIndex")),
                CodexAgentRole.Forge,
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>(),
            It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse("Execution Result", true, TotalToolCalls: 1, WriteToolCalls: 1));

        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(true, "All Good", []));

        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var orchestrator = new CodexOrchestrator(
            sessionManager,
            mockKernel.Object,
            Mock.Of<ICodexArchitect>(),
            mockSecurity.Object,
            mockValidator.Object,
            mockGit.Object,
            mockScanner.Object,
            mockAnalysis.Object,
            mockArch.Object,
            Mock.Of<ICodexTestDesigner>(),
            mockPolicy.Object,
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act
        var result = await orchestrator.ExecuteCodeTaskAsync("s-index", "t-index");

        // Assert
        Assert.True(result.Success, result.Message);
        mockKernel.Verify(k => k.RunLoopAsync(
            It.IsAny<CodexSession>(),
            It.Is<string>(prompt => prompt.Contains("ProjectFileIndex")),
            CodexAgentRole.Forge,
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>(),
            It.IsAny<TaskFileScopeDescriptor?>()), Times.AtLeastOnce);
    }
}
