// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Workers;
using Xunit;

namespace CodexFlow.Core.Tests.Workers;

public class WorkerDefinitionRegistryTests
{
    // Deny first, then allow: a worker gets exactly the tools its definition names. Two properties are pinned because a tool added to a list by
    // mistake would give a read-only worker a way to write, or a worker a way to start workers and approve plans (opencode's explore agent is
    // "*: deny" with reads allowed, and no sub-agent can open another).
    private static readonly string[] WriteTools =
    [
        "hs_write", "write_file", "edit_file", "notebook_edit", "delete_file", "create_directory", "apply_patch", "ivilson_smart_patch",
        "openspec_revert_changes", "openspec_create_checkpoint"
    ];

    private static readonly string[] CoordinationTools =
    [
        "spawn_worker", "continue_worker", "stop_worker", "retry_failed_task", "approve_plan", "start_next_task", "create_session_plan",
        "task_create", "task_update", "task_stop"
    ];

    [Theory]
    [InlineData(WorkerType.Explore)]
    [InlineData(WorkerType.Plan)]
    [InlineData(WorkerType.Verify)]
    public void AWorkerThatOnlyReads_HasNoToolThatWritesTheWorkspace(WorkerType workerType)
    {
        var allowed = new DefaultWorkerDefinitionRegistry().GetRequired(workerType).AllowedToolNames;

        Assert.Empty(allowed.Where(name => WriteTools.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(WorkerType.Explore)]
    [InlineData(WorkerType.Plan)]
    [InlineData(WorkerType.Forge)]
    [InlineData(WorkerType.Verify)]
    public void NoWorker_CanStartWorkersApprovePlansOrEditTheTaskList(WorkerType workerType)
    {
        var allowed = new DefaultWorkerDefinitionRegistry().GetRequired(workerType).AllowedToolNames;

        Assert.Empty(allowed.Where(name => CoordinationTools.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(WorkerType.Explore, WorkerIsolationMode.Workspace, WorkerOutputContract.TaskNotificationEnvelope, WorkerLanguageServiceAccess.ReadOnly, "lsp_get_diagnostics")]
    [InlineData(WorkerType.Plan, WorkerIsolationMode.Workspace, WorkerOutputContract.TaskNotificationEnvelope, WorkerLanguageServiceAccess.ReadOnly, "search_file_index")]
    [InlineData(WorkerType.Verify, WorkerIsolationMode.Workspace, WorkerOutputContract.VerificationReportEnvelope, WorkerLanguageServiceAccess.DiagnosticsEvidenceRead, "lsp_find_references")]
    public void Registry_ShouldExposeReadOnlyWorkerDefinitions(
        WorkerType workerType,
        WorkerIsolationMode expectedIsolation,
        WorkerOutputContract expectedContract,
        WorkerLanguageServiceAccess expectedAccess,
        string expectedTool)
    {
        var registry = new DefaultWorkerDefinitionRegistry();

        var definition = registry.GetRequired(workerType);
        var prompt = definition.BuildSystemPrompt(new CodexSession { Id = "session-phase2", WorkspacePath = "/tmp/codexflow-phase2" });
        var runtimeContext = registry.BuildRuntimeContext(workerType);

        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains(expectedTool, definition.AllowedToolNames);
        Assert.Equal(expectedIsolation, definition.DefaultIsolationMode);
        Assert.Equal(expectedContract, definition.OutputContract);
        Assert.Equal(expectedAccess, definition.LanguageServiceAccess);
        Assert.Equal(workerType, runtimeContext.WorkerType);
        Assert.Contains(expectedTool, runtimeContext.AllowedToolNames);

        if (workerType == WorkerType.Verify)
        {
            Assert.Contains("exec_cmd", definition.AllowedToolNames);
            Assert.Contains("run_tests", definition.AllowedToolNames);
            Assert.DoesNotContain("write_file", definition.AllowedToolNames);
        }
    }

    [Fact]
    public void Registry_ShouldFilterToolsByWhitelistAndCategory()
    {
        var registry = new DefaultWorkerDefinitionRegistry();
        var availableTools = new ICodexTool[]
        {
            CreateTool("tool_search", ToolCategory.System),
            CreateTool("lsp_get_diagnostics", ToolCategory.Analysis),
            CreateTool("write_file", ToolCategory.Forge),
            CreateTool("hs_read", ToolCategory.Read),
            CreateTool("hs_write", ToolCategory.Forge),
            CreateTool("search_file_index", ToolCategory.Analysis)
        };

        var exploreTools = registry.FilterAvailableTools(WorkerType.Explore, availableTools);
        var planTools = registry.FilterAvailableTools(WorkerType.Plan, availableTools);
        var forgeTools = registry.FilterAvailableTools(WorkerType.Forge, availableTools);
        var verifyTools = registry.FilterAvailableTools(WorkerType.Verify, availableTools);

        Assert.Contains(exploreTools, tool => tool.Name == "tool_search");
        Assert.Contains(exploreTools, tool => tool.Name == "lsp_get_diagnostics");
        Assert.DoesNotContain(exploreTools, tool => tool.Name == "write_file");
        Assert.DoesNotContain(exploreTools, tool => tool.Name == "write_file");

        Assert.Contains(planTools, tool => tool.Name == "search_file_index");
        Assert.Contains(forgeTools, tool => tool.Name == "hs_read");
        Assert.Contains(forgeTools, tool => tool.Name == "hs_write");
        Assert.DoesNotContain(verifyTools, tool => tool.Name == "write_file");
    }

    [Fact]
    public void Registry_ShouldExposeHashlineReadWriteTools_ForForgeWorker()
    {
        var registry = new DefaultWorkerDefinitionRegistry();

        var definition = registry.GetRequired(WorkerType.Forge);
        var runtimeContext = registry.BuildRuntimeContext(WorkerType.Forge);

        Assert.Contains("hs_read", definition.AllowedToolNames);
        Assert.Contains("hs_write", definition.AllowedToolNames);
        Assert.Contains("hs_read", runtimeContext.AllowedToolNames);
        Assert.Contains("hs_write", runtimeContext.AllowedToolNames);
    }

    [Fact]
    public void Registry_ShouldPreferHashlineWriteTool_ForForgeRecovery()
    {
        var registry = new DefaultWorkerDefinitionRegistry();

        var definition = registry.GetRequired(WorkerType.Forge);
        var contract = Assert.IsType<RequiredToolExecutionContract>(definition.RequiredToolContract);

        Assert.Equal("hs_write", contract.PreferredRecoveryToolName);
        Assert.Equal("hs_write", contract.AnyOfToolNames[0]);
        Assert.Contains("apply_patch", contract.AnyOfToolNames);
    }

    private static ICodexTool CreateTool(string name, ToolCategory category)
        => new TestCodexTool(name, category);

    private sealed class TestCodexTool(string name, ToolCategory category) : ICodexTool
    {
        public string Name => name;
        public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
        public string Description => name;
        public ToolCategory Category => category;
        public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
            => Task.FromResult(CodexToolResult.Succeeded("ok"));
    }
}

// A live developer conversation in Chinese got an explore worker report written in Russian: no worker prompt said which language to answer in.
public class WorkerReplyLanguageTests
{
    [Theory]
    [InlineData(WorkerType.Explore)]
    [InlineData(WorkerType.Plan)]
    [InlineData(WorkerType.Forge)]
    [InlineData(WorkerType.Verify)]
    public void EveryWorkerPrompt_TellsTheWorkerToAnswerInTheLanguageOfTheTask(WorkerType workerType)
    {
        var prompt = new DefaultWorkerDefinitionRegistry().GetRequired(workerType)
            .BuildSystemPrompt(new CodexSession { Id = "s", WorkspacePath = "/tmp/w" });

        Assert.Contains("与任务描述相同的语言", prompt, StringComparison.Ordinal);
    }
}
