// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DefaultAgentRoleRegistry = CodexFlow.Core.Services.DefaultAgentRoleRegistry;

namespace CodexFlow.Core.Tests.Runtime;

public class WorkerTypeRuntimeIntegrationTests
{
    [Fact]
    public async Task ForgeRole_RuntimeRequest_UsesWritableWorkerSurface()
    {
        var toolRegistry = CreateToolRegistry();
        var roleRegistry = new DefaultAgentRoleRegistry();
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var runtime = new Mock<IQueryRuntimeEngine>();
        QueryRuntimeRequest? capturedRequest = null;

        runtime
            .Setup(x => x.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRuntimeRequest, IQueryRuntimeEventSink, CancellationToken>((request, _, _) => capturedRequest = request)
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 1,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 1,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "forge result",
                TotalDurationMs = 1
            });

        var kernel = new DefaultCodexKernel(
            Mock.Of<IChatClient>(),
            toolRegistry,
            Mock.Of<ICodexCritiqueService>(),
            roleRegistry,
            Mock.Of<ICodeAnalysisService>(),
            NullLogger<DefaultCodexKernel>.Instance,
            new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance).Object,
            sessionManager,
            null,
            null,
            runtime.Object,
            null,
            workerDefinitionRegistry);

        var session = new CodexSession
        {
            Id = "forge-session",
            WorkspacePath = "/tmp/forge-ws",
            ProjectSummary = "Worker type integration coverage."
        };

        var response = await kernel.RunLoopAsync(
            session,
            "Implement the assigned change.",
            CodexAgentRole.Forge,
            TestContext.Current.CancellationToken,
            enableTools: true);

        Assert.Equal("forge result", response.Text);
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest!.WorkerContext);
        Assert.Equal(WorkerType.Forge, capturedRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.ShadowWorktree, capturedRequest.WorkerContext.IsolationMode);
        Assert.Equal(WorkerOutputContract.TaskNotificationEnvelope, capturedRequest.WorkerContext.OutputContract);
        Assert.Equal(WorkerLanguageServiceAccess.ReadWriteRefresh, capturedRequest.WorkerContext.LanguageServiceAccess);
        Assert.NotNull(capturedRequest.RequiredToolContract);
        Assert.Equal("forge_worker_write_evidence", capturedRequest.RequiredToolContract!.Name);
        Assert.Equal("hs_write", capturedRequest.RequiredToolContract.PreferredRecoveryToolName);
        Assert.Contains("hs_write", capturedRequest.RequiredToolContract.AnyOfToolNames);
        Assert.Contains("apply_patch", capturedRequest.RequiredToolContract.AnyOfToolNames);
        Assert.Same(capturedRequest.RequiredToolContract, capturedRequest.WorkerContext.RequiredToolContract);
        Assert.Contains("Ivilson-Forge", capturedRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "hs_write");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "exec_cmd");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "run_tests");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "lsp_get_diagnostics");
        Assert.DoesNotContain(capturedRequest.AvailableTools ?? [], tool => tool.Name == "generate_dev_plan");
    }

    [Fact]
    public async Task SentryRole_RuntimeRequest_UsesVerifyWorkerSurface()
    {
        var toolRegistry = CreateToolRegistry();
        var roleRegistry = new DefaultAgentRoleRegistry();
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var runtime = new Mock<IQueryRuntimeEngine>();
        QueryRuntimeRequest? capturedRequest = null;

        runtime
            .Setup(x => x.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRuntimeRequest, IQueryRuntimeEventSink, CancellationToken>((request, _, _) => capturedRequest = request)
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 1,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 1,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "verify result",
                TotalDurationMs = 1
            });

        var kernel = new DefaultCodexKernel(
            Mock.Of<IChatClient>(),
            toolRegistry,
            Mock.Of<ICodexCritiqueService>(),
            roleRegistry,
            Mock.Of<ICodeAnalysisService>(),
            NullLogger<DefaultCodexKernel>.Instance,
            new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance).Object,
            sessionManager,
            null,
            null,
            runtime.Object,
            null,
            workerDefinitionRegistry);

        var session = new CodexSession
        {
            Id = "verify-session",
            WorkspacePath = "/tmp/verify-ws",
            ProjectSummary = "Verification worker coverage."
        };

        var response = await kernel.RunLoopAsync(
            session,
            "Review the implementation and report evidence.",
            CodexAgentRole.Sentry,
            TestContext.Current.CancellationToken,
            enableTools: true);

        Assert.Equal("verify result", response.Text);
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest!.WorkerContext);
        Assert.Equal(WorkerType.Verify, capturedRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.Workspace, capturedRequest.WorkerContext.IsolationMode);
        Assert.Equal(WorkerOutputContract.VerificationReportEnvelope, capturedRequest.WorkerContext.OutputContract);
        Assert.Equal(WorkerLanguageServiceAccess.DiagnosticsEvidenceRead, capturedRequest.WorkerContext.LanguageServiceAccess);
        Assert.NotNull(capturedRequest.RequiredToolContract);
        Assert.Equal("verify_worker_evidence", capturedRequest.RequiredToolContract!.Name);
        Assert.Contains("exec_cmd", capturedRequest.RequiredToolContract.AnyOfToolNames);
        Assert.Same(capturedRequest.RequiredToolContract, capturedRequest.WorkerContext.RequiredToolContract);
        Assert.Contains("Ivilson-Verify", capturedRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Ivilson-Sentry", capturedRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "tool_search");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "search_file_index");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "lsp_get_diagnostics");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "exec_cmd");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "run_tests");
        Assert.DoesNotContain(capturedRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.DoesNotContain(capturedRequest.AvailableTools ?? [], tool => tool.Name == "generate_dev_plan");

        var verifyRunCommand = Assert.Single(capturedRequest.AvailableCodexTools ?? [], tool => tool.Name == "exec_cmd");
        var denied = await verifyRunCommand.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = Path.GetTempPath(),
            ["command"] = "git commit --allow-empty -m verify",
        });
        Assert.Equal(ToolResultStatus.Failed, denied.Status);
        Assert.Contains("denied by command policy", denied.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultToolRegistry CreateToolRegistry()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        registry.RegisterTool(CreateTool("tool_search", ToolCategory.System));
        registry.RegisterTool(CreateTool("search_file_index", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("search_in_files", ToolCategory.Read));
        registry.RegisterTool(CreateTool("hs_read", ToolCategory.Read));
        registry.RegisterTool(CreateTool("hs_write", ToolCategory.Forge));
        registry.RegisterTool(CreateTool("ivilson_read", ToolCategory.Read));
        registry.RegisterTool(CreateTool("ivilson_ls", ToolCategory.Read));
        registry.RegisterTool(CreateTool("list_workspace", ToolCategory.Read));
        registry.RegisterTool(CreateTool("analyze_project", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("analyze_code", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_get_diagnostics", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("write_file", ToolCategory.Forge));
        registry.RegisterTool(CreateTool("apply_patch", ToolCategory.Forge));
        registry.RegisterTool(new RunCommandTool(NullLogger<RunCommandTool>.Instance));
        registry.RegisterTool(CreateTool("run_tests", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("generate_dev_plan", ToolCategory.Planning));
        return registry;
    }

    private static DelegateCodexTool CreateTool(string name, ToolCategory category)
        => new(
            name,
            $"{name} description",
            category,
            ResolveAllowedStages(name),
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok")));

    private static IReadOnlyList<int> ResolveAllowedStages(string name)
        => name switch
        {
            "exec_cmd" or "write_file" or "hs_write" or "apply_patch" => [3, 4],
            "run_tests" => [2, 3, 4],
            "generate_dev_plan" => [0, 1],
            "tool_search" => [],
            _ => [0, 1, 2, 3, 4]
        };
}
