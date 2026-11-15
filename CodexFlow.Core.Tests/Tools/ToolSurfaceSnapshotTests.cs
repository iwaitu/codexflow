// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class ToolSurfaceSnapshotTests
{
    [Fact]
    public void Bootstrapper_ShouldExposeExpectedAlwaysOnAndDeferredToolNames()
    {
        var registry = new RecordingToolRegistry();
        CreateBootstrapper().EnsureRegistered(registry);

        Assert.Equal(
        [
            "analyze_code",
            "analyze_project",
            "ask_user_question",
            "cleanup_worker_worktree",
            "continue_worker",
            "create_directory",
            "create_session_plan",
            "cron_create",
            "cron_delete",
            "cron_list",
            "delete_file",
            "download_artifact",
            "edit_file",
            "enter_plan_mode",
            "enter_worktree",
            "exec_cmd",
            "exec_code",
            "exit_plan_mode",
            "exit_worktree",
            "generate_dev_plan",
            "git_clone",
            "glob",
            "hs_read",
            "hs_write",
            "ivilson_ls",
            "ivilson_read",
            "list_mcp_resources",
            "list_workers",
            "list_workspace",
            "lsp_document_symbols",
            "lsp_find_references",
            "lsp_get_diagnostics",
            "lsp_go_to_definition",
            "lsp_workspace_symbols",
            "monitor",
            "notebook_edit",
            "push_notification",
            "read_mcp_resource",
            "remote_trigger",
            "report_task_status",
            "retry_failed_task",
            "run_tests",
            "search_file_index",
            "search_in_files",
            "skill",
            "spawn_worker",
            "stop_worker",
            "synthetic_output",
            "task_create",
            "task_get",
            "task_list",
            "task_output",
            "task_stop",
            "task_update",
            "tool_search",
            "worker_output",
            "workflow",
            "write_file",
            "zip_directory"
        ], registry.NamesFor(ToolLoading.AlwaysOn));

        Assert.Equal(
        [
            "apply_patch",
            "fetch_webpage",
            "ivilson_smart_patch",
            "openspec_create_checkpoint",
            "openspec_revert_changes",
            "save_project_summary",
            "user_learn_preference",
            "web_search"
        ], registry.NamesFor(ToolLoading.Deferred));
    }

    [Fact]
    public void WorkerRuntimeContext_ShouldExposeStableToolSurfaceSnapshots()
    {
        var registry = new DefaultWorkerDefinitionRegistry();

        Assert.Equal(
        [
            "analyze_code",
            "analyze_project",
            "fetch_webpage",
            "glob",
            "ivilson_ls",
            "ivilson_read",
            "list_workspace",
            "lsp_document_symbols",
            "lsp_find_references",
            "lsp_get_diagnostics",
            "lsp_go_to_definition",
            "lsp_workspace_symbols",
            "search_file_index",
            "search_in_files",
            "tool_search",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Explore).AllowedToolNames);

        Assert.Equal(
        [
            "analyze_code",
            "analyze_project",
            "fetch_webpage",
            "glob",
            "ivilson_ls",
            "ivilson_read",
            "list_workspace",
            "lsp_document_symbols",
            "lsp_find_references",
            "lsp_get_diagnostics",
            "lsp_go_to_definition",
            "lsp_workspace_symbols",
            "search_file_index",
            "search_in_files",
            "tool_search",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Plan).AllowedToolNames);

        Assert.Equal(
        [
            "analyze_code",
            "apply_patch",
            "create_directory",
            "delete_file",
            "edit_file",
            "exec_cmd",
            "exec_code",
            "glob",
            "hs_read",
            "hs_write",
            "ivilson_ls",
            "ivilson_read",
            "ivilson_smart_patch",
            "list_workspace",
            "lsp_document_symbols",
            "lsp_find_references",
            "lsp_get_diagnostics",
            "lsp_go_to_definition",
            "lsp_workspace_symbols",
            "notebook_edit",
            "openspec_create_checkpoint",
            "openspec_revert_changes",
            "report_task_status",
            "run_tests",
            "search_file_index",
            "search_in_files",
            "write_file"
        ], registry.BuildRuntimeContext(WorkerType.Forge).AllowedToolNames);

        Assert.Equal(
        [
            "analyze_code",
            "analyze_project",
            "exec_cmd",
            "fetch_webpage",
            "glob",
            "ivilson_ls",
            "ivilson_read",
            "list_workspace",
            "lsp_document_symbols",
            "lsp_find_references",
            "lsp_get_diagnostics",
            "lsp_go_to_definition",
            "lsp_workspace_symbols",
            "run_tests",
            "search_file_index",
            "search_in_files",
            "tool_search",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Verify).AllowedToolNames);
    }

    [Fact]
    public void WorkerRuntimeContext_ShouldExposeStableAutoActivatedDeferredTools()
    {
        var registry = new DefaultWorkerDefinitionRegistry();

        Assert.Equal(
        [
            "fetch_webpage",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Explore).AutoActivateToolNames);

        Assert.Equal(
        [
            "fetch_webpage",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Plan).AutoActivateToolNames);

        Assert.Equal(
        [
            "apply_patch",
            "ivilson_smart_patch",
            "openspec_create_checkpoint"
        ], registry.BuildRuntimeContext(WorkerType.Forge).AutoActivateToolNames);

        Assert.Equal(
        [
            "fetch_webpage",
            "web_search"
        ], registry.BuildRuntimeContext(WorkerType.Verify).AutoActivateToolNames);
    }

    [Fact]
    public void WorkerWhitelist_ShouldNotReferenceMissingBootstrapperTools()
    {
        var toolRegistry = new RecordingToolRegistry();
        CreateBootstrapper().EnsureRegistered(toolRegistry);
        var registeredToolNames = toolRegistry.RegisteredNames;

        var workerRegistry = new DefaultWorkerDefinitionRegistry();
        foreach (var definition in workerRegistry.GetAll())
        {
            var missing = definition.AllowedToolNames
                .Where(name => !registeredToolNames.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.True(
                missing.Length == 0,
                $"{definition.WorkerType} references unregistered tool(s): {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void CoordinatorSurfacePolicy_ShouldOnlyExposeCoordinatorTools()
    {
        var toolRegistry = new RecordingToolRegistry();
        CreateBootstrapper().EnsureRegistered(toolRegistry);

        var coordinatorTools = CoordinatorToolSurfacePolicy
            .Filter(toolRegistry.GetActiveTools())
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
        [
            "ask_user_question",
            "cleanup_worker_worktree",
            "continue_worker",
            "cron_create",
            "cron_delete",
            "cron_list",
            "enter_plan_mode",
            "enter_worktree",
            "exit_plan_mode",
            "exit_worktree",
            "list_workers",
            "monitor",
            "push_notification",
            "remote_trigger",
            "spawn_worker",
            "stop_worker",
            "synthetic_output",
            "task_create",
            "task_get",
            "task_list",
            "task_output",
            "task_stop",
            "task_update",
            "worker_output",
            "workflow"
        ], coordinatorTools);

        Assert.DoesNotContain("write_file", coordinatorTools);
        Assert.DoesNotContain("exec_cmd", coordinatorTools);
        Assert.DoesNotContain("run_tests", coordinatorTools);
        Assert.DoesNotContain("apply_patch", coordinatorTools);
    }

    internal static ToolRegistryBootstrapper CreateBootstrapper()
    {
        var sessionManager = new CodexSessionManager(
            new Mock<ISessionStore>().Object,
            new Mock<IMemoryService>().Object,
            NullLogger<CodexSessionManager>.Instance);

        return new ToolRegistryBootstrapper(
            new Mock<IGitService>().Object,
            orchestrator: null!,
            new ProjectScanner(NullLogger<ProjectScanner>.Instance),
            new Mock<ICodeAnalysisService>().Object,
            new Mock<IArchitectureService>().Object,
            new Mock<ICodexPlanner>().Object,
            sessionManager,
            new Mock<IProjectMemoryService>().Object,
            new SearchFileIndexTool(sessionManager, NullLogger<SearchFileIndexTool>.Instance),
            new RunTestsTool(Array.Empty<ITestFrameworkAdapter>(), NullLogger<RunTestsTool>.Instance),
            _ => Task.FromResult(true),
            NullLoggerFactory.Instance,
            NullLogger<ToolRegistryBootstrapper>.Instance,
            languageServiceRegistry: new Mock<ILanguageServiceRegistry>().Object,
            languageServiceSessionFactory: new Mock<ILanguageServiceSessionFactory>().Object,
            httpClientFactory: new Mock<IHttpClientFactory>().Object,
            spawnWorkerFunc: (_, _) => Task.FromResult(new SpawnWorkerResult { Success = true, JobId = "job-1" }),
            continueWorkerFunc: (_, _) => Task.FromResult(new ContinueWorkerResult { Success = true, Mode = ContinueWorkerMode.Resumed, JobId = "job-1" }),
            stopWorkerFunc: (_, _) => Task.FromResult(new StopWorkerResult { Success = true, JobId = "job-1" }),
            cleanupWorkerWorktreeFunc: (_, _) => Task.FromResult(new CleanupWorkerWorktreeResult { Success = true, JobId = "job-1" }),
            listWorkersFunc: (_, _) => Task.FromResult<IReadOnlyList<WorkerJobSummary>>(Array.Empty<WorkerJobSummary>()),
            workerOutputFunc: (_, _) => Task.FromResult(new WorkerOutputResult { Success = true, JobId = "job-1" }));
    }

    internal sealed class RecordingToolRegistry : IToolRegistry
    {
        private readonly List<(string Name, ToolLoading Loading, ICodexTool Tool)> _registrations = [];

        public IReadOnlySet<string> RegisteredNames => _registrations
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public string[] NamesFor(ToolLoading loading) => _registrations
            .Where(entry => entry.Loading == loading)
            .Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public void RegisterTool(ICodexTool tool) => RegisterTool(tool, ToolLoading.AlwaysOn);

        public void RegisterTool(ICodexTool tool, ToolLoading loading)
            => _registrations.Add((tool.Name, loading, tool));

        public IEnumerable<ICodexTool> GetAvailableTools(CodexSession session) => GetActiveTools();

        public IEnumerable<ICodexTool> GetActiveTools()
            => _registrations
                .Where(entry => entry.Loading == ToolLoading.AlwaysOn)
                .Select(entry => entry.Tool);

        public IEnumerable<ICodexTool> GetAlwaysOnTools() => GetActiveTools();

        public IEnumerable<ICodexTool> SearchTools(string query)
            => _registrations
                .Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Tool);

        public bool ActivateTool(string toolName) => RegisteredNames.Contains(toolName);

        public string GetCatalog() => string.Join(Environment.NewLine, _registrations.Select(entry => entry.Name));

        public IReadOnlySet<string> GetActiveToolNames()
            => GetActiveTools().Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
