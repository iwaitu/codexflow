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

namespace CodexFlow.Core.Tests.Agents;

public class ToolRegistryTests
{
    [Fact]
    public void RegisterTool_ShouldStoreTool_WhenValid()
    {
        // Arrange
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockTool = new Mock<ICodexTool>();
        mockTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        mockTool.Setup(t => t.Name).Returns("test_tool");

        // Act
        registry.RegisterTool(mockTool.Object);
        var tools = registry.GetAvailableTools(new CodexSession());

        // Assert
        Assert.Single(tools);
        Assert.Equal("test_tool", tools.First().Name);
    }

    [Fact]
    public async Task RoslynCodeAnalysisTool_ShouldReturnDiagnostics_WhenCodeIsInvalid()
    {
        // Arrange
        var mockAnalysisService = new Mock<ICodeAnalysisService>();
        mockAnalysisService.Setup(s => s.AnalyzeCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<CodeDiagnostic> { 
                new CodeDiagnostic("CS0103", "The name 'x' does not exist", "Error", 1, 1) 
            });

        var tool = new RoslynCodeAnalysisTool(mockAnalysisService.Object);

        // Act
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "invalid code" });

        // Assert
        Assert.Contains("CS0103", result.Output);
        Assert.Contains("Error", result.Output);
    }

    [Fact]
    public void SearchTools_ShouldReturnAlwaysOnAndDeferredMatches()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var alwaysOn = CreateTool("run_tests", "Run the project test suite", ToolCategory.Sentry);
        var deferred = CreateTool("git_test_helper", "Git helper for test workflows", ToolCategory.System);

        registry.RegisterTool(alwaysOn.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        registry.RegisterTool(deferred.Object, CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        var matches = registry.SearchTools("test").Select(t => t.Name).ToList();

        Assert.Contains("run_tests", matches);
        Assert.Contains("git_test_helper", matches);
    }

    [Fact]
    public void SearchTools_ShouldFallbackToTokenMatches_ForMultiKeywordQueries()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var patchTool = CreateTool("apply_patch", "Apply a focused source patch", ToolCategory.Forge);
        var worktreeTool = CreateTool("enter_worktree", "Switch the session workspace to a repo worktree", ToolCategory.System);

        registry.RegisterTool(patchTool.Object, CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        registry.RegisterTool(worktreeTool.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);

        var matches = registry.SearchTools("patch apply merge worktree")
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("apply_patch", matches);
        Assert.Contains("enter_worktree", matches);
    }

    [Fact]
    public async Task ToolSearchMetaTool_ShouldReportAvailableState_AndActivateDeferredMatches()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var alwaysOn = CreateTool("run_tests", "Run the project test suite", ToolCategory.Sentry);
        var deferred = CreateTool("git_test_helper", "Git helper for test workflows", ToolCategory.System);

        registry.RegisterTool(alwaysOn.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        registry.RegisterTool(deferred.Object, CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        var tool = new CodexFlow.Core.Agents.Tools.ToolSearchMetaTool(
            registry,
            NullLogger<CodexFlow.Core.Agents.Tools.ToolSearchMetaTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "test" });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"run_tests\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"git_test_helper\"", result.Output, StringComparison.Ordinal);
        // run_tests is AlwaysOn → activated:false, available_now:true
        // git_test_helper is Deferred → activated:true, available_now:true
        Assert.Contains("\"activated\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"available_now\":true", result.Output, StringComparison.Ordinal);

        var activeTools = registry.GetActiveTools().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_tests", activeTools);
        Assert.Contains("git_test_helper", activeTools);
    }

    [Fact]
    public async Task ToolSearchMetaTool_ShouldReturnSelectionMetadata()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var notebook = CreateTool("notebook_edit", "Edit .ipynb notebook cells with diff preview", ToolCategory.Forge);
        notebook.Setup(t => t.Metadata).Returns(new ToolExecutionMetadata(
            IsConcurrencySafe: false,
            IsReadOnly: false,
            IsDestructive: false,
            InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
            ResultSizeSoftLimitChars: 12_288));
        registry.RegisterTool(notebook.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);

        var tool = new CodexFlow.Core.Agents.Tools.ToolSearchMetaTool(
            registry,
            NullLogger<CodexFlow.Core.Agents.Tools.ToolSearchMetaTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "notebook" });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"description\":\"Edit .ipynb notebook cells with diff preview\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"category\":\"Forge\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"ipynb\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"surface\":[\"main\",\"forge-worker\"]", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"risk\":\"medium\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"examples\":[", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"activation_reason\":\"already_active:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GetCatalog_ShouldUseExplicitToolMetadata_WhenProvided()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var planningTool = CreateTool("generate_dev_plan", "Generate a session plan", ToolCategory.Planning);
        planningTool.Setup(t => t.Metadata).Returns(new ToolExecutionMetadata(
            IsConcurrencySafe: false,
            IsReadOnly: false,
            IsDestructive: false,
            InterruptBehavior: ToolInterruptBehavior.CancelSafe,
            ResultSizeSoftLimitChars: 16384));

        registry.RegisterTool(planningTool.Object);

        var catalog = registry.GetCatalog();

        Assert.Contains("| generate_dev_plan | Generate a session plan | no | no | no | CancelSafe | 16384 |", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void GetCatalog_ShouldFallbackToCategoryMetadata_WhenToolReturnsNullMetadata()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var systemTool = CreateTool("system_tool", "System helper", ToolCategory.System);
        systemTool.Setup(t => t.Metadata).Returns((ToolExecutionMetadata)null!);

        registry.RegisterTool(systemTool.Object);

        var catalog = registry.GetCatalog();

        Assert.Contains("| system_tool | System helper | no | no | no | RequiresConfirmation | 12288 |", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCategory_ShouldReturnCachedMetadataInstance()
    {
        var first = ToolExecutionMetadata.ForCategory(ToolCategory.Read);
        var second = ToolExecutionMetadata.ForCategory(ToolCategory.Read);

        Assert.Same(first, second);
    }

    [Fact]
    public void ToolSearchMetaTool_Metadata_ShouldNotRequireConfirmation()
    {
        var tool = new CodexFlow.Core.Agents.Tools.ToolSearchMetaTool(
            new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance),
            NullLogger<CodexFlow.Core.Agents.Tools.ToolSearchMetaTool>.Instance);

        Assert.False(tool.Metadata.IsReadOnly);
        Assert.False(tool.Metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.CancelSafe, tool.Metadata.InterruptBehavior);
    }

    [Fact]
    public void SystemTool_Metadata_ShouldReflectPerToolSemantics()
    {
        var gitService = new Mock<IGitService>();
        var sessionManager = new CodexSessionManager(
            new Mock<ISessionStore>().Object,
            new Mock<IMemoryService>().Object,
            NullLogger<CodexSessionManager>.Instance);
        var continueTool = new CodexFlow.Core.Agents.Tools.ContinueWorkerTool(
            (_, _) => Task.FromResult(new ContinueWorkerResult { Success = true, JobId = "job-1", Mode = ContinueWorkerMode.Resumed }),
            NullLogger<CodexFlow.Core.Agents.Tools.ContinueWorkerTool>.Instance);
        var listTool = new CodexFlow.Core.Agents.Tools.ListWorkersTool(
            (_, _) => Task.FromResult<IReadOnlyList<WorkerJobSummary>>(Array.Empty<WorkerJobSummary>()),
            NullLogger<CodexFlow.Core.Agents.Tools.ListWorkersTool>.Instance);
        var cleanupTool = new CodexFlow.Core.Agents.Tools.CleanupWorkerWorktreeTool(
            (_, _) => Task.FromResult(new CleanupWorkerWorktreeResult { Success = true, JobId = "job-1", Cleaned = true }),
            NullLogger<CodexFlow.Core.Agents.Tools.CleanupWorkerWorktreeTool>.Instance);
        var userMemoryTool = new UserMemoryTool(
            sessionManager,
            NullLogger<UserMemoryTool>.Instance);
        var gitCloneTool = new GitWorkspaceTool(
            "git_clone",
            "clone repo",
            [1],
            gitService.Object,
            NullLogger<GitWorkspaceTool>.Instance);
        var revertTool = new GitWorkspaceTool(
            "openspec_revert_changes",
            "revert repo",
            [3],
            gitService.Object,
            NullLogger<GitWorkspaceTool>.Instance);

        Assert.False(continueTool.Metadata.IsReadOnly);
        Assert.Equal(ToolInterruptBehavior.CancelSafe, continueTool.Metadata.InterruptBehavior);

        Assert.True(listTool.Metadata.IsReadOnly);
        Assert.True(listTool.Metadata.IsConcurrencySafe);

        Assert.True(cleanupTool.Metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.RequiresConfirmation, cleanupTool.Metadata.InterruptBehavior);

        Assert.False(userMemoryTool.Metadata.IsReadOnly);
        Assert.False(userMemoryTool.Metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.CancelSafe, userMemoryTool.Metadata.InterruptBehavior);

        Assert.False(gitCloneTool.Metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.RequiresConfirmation, gitCloneTool.Metadata.InterruptBehavior);

        Assert.True(revertTool.Metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.RequiresConfirmation, revertTool.Metadata.InterruptBehavior);
    }

    private static Mock<ICodexTool> CreateTool(string name, string description, ToolCategory category)
    {
        var mockTool = new Mock<ICodexTool>();
        mockTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        mockTool.Setup(t => t.Name).Returns(name);
        mockTool.Setup(t => t.Description).Returns(description);
        mockTool.Setup(t => t.Category).Returns(category);
        mockTool.Setup(t => t.AllowedStages).Returns(Array.Empty<int>());
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CodexToolResult.Succeeded("ok"));
        return mockTool;
    }
}
