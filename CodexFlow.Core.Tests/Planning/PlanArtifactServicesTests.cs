// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Planning;

public sealed class PlanArtifactServicesTests
{
    [Fact]
    public async Task WorkspacePlanFileService_WriteAsync_InvalidatesProjectedPlan()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var session = new CodexSession { Id = "session-1", WorkspacePath = workspace };
            var service = new WorkspacePlanFileService(Options.Create(new PlanningOptions()));
            var artifact = new PlanArtifact
            {
                Id = "plan-1",
                SessionId = session.Id,
                Status = PlanArtifactStatus.ReadyToExecute,
                Projection = new PlanProjectionRecord { ProjectedMarkdownHash = "old" }
            };
            artifact.ReplaceTasks([CreateCodeTask("T1")]);

            await service.WriteAsync(session, artifact, "# Updated plan");
            var read = await service.ReadAsync(session, artifact);

            Assert.Equal("# Updated plan", read);
            Assert.Equal(PlanArtifactStatus.Draft, artifact.Status);
            Assert.Null(artifact.Projection);
            Assert.Null(artifact.PreviewProjectedMarkdownHash);
            Assert.Empty(artifact.Tasks);
            Assert.EndsWith(".md", artifact.PlanFilePath, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workspace, artifact.PlanFilePath!.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ApprovalService_ApproveAsync_StoresApprovedMarkdownHash()
    {
        var store = new InMemoryPlanArtifactStore();
        var artifact = new PlanArtifact
        {
            Id = "plan-approve",
            SessionId = "session-1",
            Status = PlanArtifactStatus.ReadyForApproval,
            Markdown = "# Approved plan"
        };
        await store.SaveAsync(artifact);

        var service = new DefaultPlanApprovalService(store);
        var approved = await service.ApproveAsync(artifact.Id, "user-1", "looks good");

        Assert.Equal(PlanArtifactStatus.Approved, approved.Status);
        Assert.Equal("user-1", approved.Approval?.ApprovedByUserId);
        Assert.Equal("looks good", approved.Approval?.Feedback);
        Assert.Equal(PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown), approved.Approval?.ApprovedMarkdownHash);
        Assert.Empty(approved.Tasks);
        Assert.Null(approved.Projection);
    }

    [Fact]
    public async Task ApprovalService_ApproveAsync_RecordsDependencyDebtAcceptance_OnlyWhenAsked()
    {
        var store = new InMemoryPlanArtifactStore();
        foreach (var id in new[] { "plan-plain", "plan-debt" })
        {
            await store.SaveAsync(new PlanArtifact { Id = id, SessionId = "session-1", Status = PlanArtifactStatus.ReadyForApproval, Markdown = "# Plan " + id });
        }

        var service = new DefaultPlanApprovalService(store);
        var plain = await service.ApproveAsync("plan-plain", "user-1");
        var accepted = await service.ApproveAsync("plan-debt", "user-1", acceptExistingDependencyDebt: true);

        Assert.False(plain.Approval!.AcceptsExistingDependencyDebt);
        Assert.True(accepted.Approval!.AcceptsExistingDependencyDebt);
        // The acceptance lives inside the approval record, so it is bound to exactly the plan content that was approved.
        Assert.Equal(PlanArtifactSupport.ComputeMarkdownHash(accepted.Markdown), accepted.Approval.ApprovedMarkdownHash);
    }

    [Fact]
    public async Task ApprovalService_ApproveAsync_PreservesPreviewTasks_WhenHashMatches()
    {
        var store = new InMemoryPlanArtifactStore();
        var markdown = "# Approved plan";
        var artifact = new PlanArtifact
        {
            Id = "plan-preview-approve",
            SessionId = "session-1",
            Status = PlanArtifactStatus.ReadyForApproval,
            Markdown = markdown,
            PreviewProjectedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(markdown)
        };
        artifact.ReplaceTasks([CreateCodeTask("T1")]);
        await store.SaveAsync(artifact);

        var service = new DefaultPlanApprovalService(store);
        var approved = await service.ApproveAsync(artifact.Id, "user-1");

        Assert.Equal(PlanArtifactStatus.Approved, approved.Status);
        Assert.Single(approved.Tasks);
        Assert.Equal("T1", approved.Tasks[0].Id);
        Assert.Equal(PlanArtifactSupport.ComputeMarkdownHash(markdown), approved.PreviewProjectedMarkdownHash);
        Assert.Null(approved.Projection);
    }

    [Fact]
    public async Task ProjectionService_ProjectAsync_RequiresApprovedHash()
    {
        var store = new InMemoryPlanArtifactStore();
        var planner = new Mock<ICodexPlanner>();
        var service = new DefaultPlanProjectionService(planner.Object, store, NullLogger<DefaultPlanProjectionService>.Instance);
        var artifact = new PlanArtifact
        {
            Id = "plan-mismatch",
            SessionId = "session-1",
            Status = PlanArtifactStatus.Approved,
            Markdown = "# Changed"
        };
        artifact.Approval = new PlanApprovalRecord { ApprovedMarkdownHash = "different" };

        var result = await service.ProjectAsync(artifact, new CodexSession { Id = "session-1" });

        Assert.False(result.Success);
        Assert.Contains("Approved markdown hash does not match current markdown.", result.Errors);
        planner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProjectionService_ProjectAsync_ReusesPreviewTasks_WhenHashMatches()
    {
        var store = new InMemoryPlanArtifactStore();
        var planner = new Mock<ICodexPlanner>();
        var markdown = "# Ready plan";
        var hash = PlanArtifactSupport.ComputeMarkdownHash(markdown);
        var artifact = new PlanArtifact
        {
            Id = "plan-preview-ready",
            SessionId = "session-1",
            Status = PlanArtifactStatus.Approved,
            Markdown = markdown,
            PreviewProjectedMarkdownHash = hash,
            Approval = new PlanApprovalRecord { ApprovedMarkdownHash = hash }
        };
        var previewTask = CreateCodeTask("T1");
        previewTask.Status = CodexTaskStatus.Planning;
        artifact.ReplaceTasks([previewTask]);
        await store.SaveAsync(artifact);

        var service = new DefaultPlanProjectionService(planner.Object, store, NullLogger<DefaultPlanProjectionService>.Instance);
        var result = await service.ProjectAsync(artifact, new CodexSession { Id = "session-1" });

        Assert.True(result.Success);
        Assert.Equal(PlanArtifactStatus.ReadyToExecute, result.Artifact.Status);
        Assert.Single(result.Tasks);
        Assert.Equal(CodexTaskStatus.Pending, result.Tasks[0].Status);
        Assert.Equal(hash, result.Artifact.Projection?.ProjectedMarkdownHash);
        planner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProjectionService_ProjectAsync_PersistsReadyToExecuteTasks()
    {
        var store = new InMemoryPlanArtifactStore();
        var planner = new Mock<ICodexPlanner>();
        var artifact = new PlanArtifact
        {
            Id = "plan-ready",
            SessionId = "session-1",
            Status = PlanArtifactStatus.ReadyForApproval,
            Markdown = "# Ready plan"
        };
        await store.SaveAsync(artifact);

        var approval = new DefaultPlanApprovalService(store);
        var approved = await approval.ApproveAsync(artifact.Id, "user-1");
        planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateCodeTask("T1")]);

        var service = new DefaultPlanProjectionService(planner.Object, store, NullLogger<DefaultPlanProjectionService>.Instance);
        var result = await service.ProjectAsync(approved, new CodexSession { Id = "session-1" });

        Assert.True(result.Success);
        Assert.Equal(PlanArtifactStatus.ReadyToExecute, result.Artifact.Status);
        Assert.Single(result.Tasks);
        Assert.Equal("T1", result.Artifact.Projection?.TaskIds.Single());
        Assert.Equal(PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown), result.Artifact.Projection?.ProjectedMarkdownHash);

        var persisted = await store.GetAsync(artifact.Id);
        Assert.Equal(PlanArtifactStatus.ReadyToExecute, persisted?.Status);
        Assert.Single(persisted?.Tasks ?? []);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public async Task ProjectionService_InvalidPreview_IsReprojectedWithoutWeakeningValidation(int successfulAttempt)
    {
        var store = new InMemoryPlanArtifactStore();
        var planner = new Mock<ICodexPlanner>();
        var hash = PlanArtifactSupport.ComputeMarkdownHash("# Fix one bug");
        var artifact = new PlanArtifact
        {
            Id = "invalid-preview", SessionId = "session", Markdown = "# Fix one bug",
            Status = PlanArtifactStatus.Approved, PreviewProjectedMarkdownHash = hash,
            Approval = new PlanApprovalRecord { ApprovedMarkdownHash = hash }
        };
        var corrected = successfulAttempt > 0;
        var attempts = 0;
        var incomplete = CreateCodeTask("T1");
        incomplete.ChecklistItems.Clear();
        artifact.ReplaceTasks([incomplete]);
        planner.Setup(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(),
                It.Is<string>(prompt => prompt.Contains("ChecklistItems")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == successfulAttempt ? new List<CodexTask> { CreateCodeTask("T1") } : new List<CodexTask> { incomplete });
        var service = new DefaultPlanProjectionService(planner.Object, store, NullLogger<DefaultPlanProjectionService>.Instance);
        var result = await service.ProjectAsync(artifact, new CodexSession { Id = "session" });
        Assert.Equal(corrected, result.Success);
        Assert.Equal(corrected ? PlanArtifactStatus.ReadyToExecute : PlanArtifactStatus.Failed, result.Artifact.Status);
        if (corrected) Assert.NotEmpty(Assert.Single(result.Tasks).ChecklistItems);
        else Assert.Contains(result.Errors, error => error.Contains("ChecklistItems"));
        planner.Verify(p => p.GeneratePlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(successfulAttempt == 1 ? 1 : 2));
    }

    [Fact]
    public void ToolRegistry_PlanPermissionMode_IsFeatureFlagged()
    {
        var session = new CodexSession { Id = "session-1", CurrentStage = 1 };
        session.Metadata["PlanModeActive"] = bool.TrueString;

        var disabledRegistry = new DefaultToolRegistry(
            NullLogger<DefaultToolRegistry>.Instance,
            planningOptions: Options.Create(new PlanningOptions { PlanPermissionModeEnabled = false }));
        disabledRegistry.RegisterTool(CreateForgeTool("write_code"));

        Assert.Contains(disabledRegistry.GetAvailableTools(session), tool => tool.Name == "write_code");

        var enabledRegistry = new DefaultToolRegistry(
            NullLogger<DefaultToolRegistry>.Instance,
            planningOptions: Options.Create(new PlanningOptions { PlanPermissionModeEnabled = true }));
        enabledRegistry.RegisterTool(CreateForgeTool("write_code"));
        enabledRegistry.RegisterTool(CreateReadTool("read_code"));

        var available = enabledRegistry.GetAvailableTools(session).Select(tool => tool.Name).ToList();
        Assert.DoesNotContain("write_code", available);
        Assert.Contains("read_code", available);
    }

    [Fact]
    public void FrozenCatalog_IncludesDeferredWithoutActivation_AndPreservesStageAndPlanPolicy()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance,
            planningOptions: Options.Create(new PlanningOptions { PlanPermissionModeEnabled = true }));
        registry.RegisterTool(CreateReadTool("deferred_read"), ToolLoading.Deferred);
        registry.RegisterTool(CreateForgeTool("deferred_write"), ToolLoading.Deferred);
        var session = new CodexSession { Id = "frozen", CurrentStage = 1 };
        session.Metadata["PlanModeActive"] = bool.TrueString;
        Assert.Empty(registry.GetAvailableTools(session));
        Assert.Equal("deferred_read", Assert.Single(registry.GetAvailableTools(session, true)).Name);
        Assert.Empty(registry.GetAvailableTools(session));
        session.CurrentStage = 5;
        Assert.Empty(registry.GetAvailableTools(session, true));
    }

    private static CodexTask CreateCodeTask(string id)
    {
        var task = new CodexTask
        {
            Id = id,
            TaskType = CodexTaskClassifier.CodeTaskType,
            Title = "Implement change",
            Description = "Make the requested code change.",
            RiskLevel = "Low",
            UnsafeIfDependencyFallbackPassed = true
        };
        task.ChecklistItems.Add(new TaskChecklistItem { Id = "CHK_001", Text = "Run dotnet build" });
        return task;
    }

    // Fake tools declare their trusted capability explicitly (write vs read); undeclared tools would be rejected.
    private static ICodexTool CreateForgeTool(string name)
        => CodexFlow.Core.Tests.Support.DeclaredCapabilityTool.Declare(
            new DelegateCodexTool(
                name,
                "write files",
                ToolCategory.Forge,
                [1],
                new ToolExecutionMetadata(false, false, true, ToolInterruptBehavior.RequiresConfirmation, 1_024),
                (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))),
            CodexFlow.Core.Execution.ToolCapability.WorkspaceWrite);

    private static ICodexTool CreateReadTool(string name)
        => CodexFlow.Core.Tests.Support.DeclaredCapabilityTool.Declare(
            new DelegateCodexTool(
                name,
                "read files",
                ToolCategory.Read,
                [1],
                (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))),
            CodexFlow.Core.Execution.ToolCapability.ReadOnly);

    private static string CreateTempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexflow-plan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
