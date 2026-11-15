// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DefaultAgentRoleRegistry = CodexFlow.Core.Services.DefaultAgentRoleRegistry;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class WorkerTypeRealLlmIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task ForgeRole_RealLlm_UsesForgeWorkerContext()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            output.WriteLine("未启用真实模型测试。设置 RUN_REAL_LLM_TOKEN_TESTS=true 后可运行。");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        using var client = CreateChatClient(settings);
        var toolRegistry = CreateToolRegistry();
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var roleRegistry = new DefaultAgentRoleRegistry();
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var engine = CreateRuntimeEngine(client);
        var kernel = CreateKernel(client, toolRegistry, roleRegistry, sessionManager, engine, workerDefinitionRegistry);

        var session = new CodexSession
        {
            Id = "real-llm-forge",
            WorkspacePath = "/tmp/real-llm-forge",
            ProjectSummary = "Phase 2 worker type real-LLM connectivity check."
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        var response = await kernel.RunLoopAsync(
            session,
            "这是一个连通性测试。不要调用任何工具，只回复字符串 FORGE_REAL_LLM_OK。",
            CodexAgentRole.Forge,
            cts.Token,
            enableTools: true);

        output.WriteLine("Forge response: {0}", response.Text);
        Assert.NotNull(engine.LastRequest);
        Assert.NotNull(engine.LastRequest!.WorkerContext);
        Assert.Equal(WorkerType.Forge, engine.LastRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.ShadowWorktree, engine.LastRequest.WorkerContext.IsolationMode);
        Assert.Contains("write_file", engine.LastRequest.WorkerContext.AllowedToolNames);
        Assert.Contains(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.DoesNotContain(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "generate_dev_plan");
        Assert.Contains("Ivilson-Forge", engine.LastRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("FORGE_REAL_LLM_OK", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task SentryRole_RealLlm_UsesVerifyWorkerContext()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            output.WriteLine("未启用真实模型测试。设置 RUN_REAL_LLM_TOKEN_TESTS=true 后可运行。");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        using var client = CreateChatClient(settings);
        var toolRegistry = CreateToolRegistry();
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var roleRegistry = new DefaultAgentRoleRegistry();
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var engine = CreateRuntimeEngine(client);
        var kernel = CreateKernel(client, toolRegistry, roleRegistry, sessionManager, engine, workerDefinitionRegistry);

        var session = new CodexSession
        {
            Id = "real-llm-verify",
            WorkspacePath = "/tmp/real-llm-verify",
            ProjectSummary = "Phase 2 verification worker real-LLM connectivity check."
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        var response = await kernel.RunLoopAsync(
            session,
            "这是一个连通性测试。不要调用任何工具，只回复字符串 VERIFY_REAL_LLM_OK。",
            CodexAgentRole.Sentry,
            cts.Token,
            enableTools: true);

        output.WriteLine("Verify response: {0}", response.Text);
        Assert.NotNull(engine.LastRequest);
        Assert.NotNull(engine.LastRequest!.WorkerContext);
        Assert.Equal(WorkerType.Verify, engine.LastRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.Workspace, engine.LastRequest.WorkerContext.IsolationMode);
        Assert.Equal(WorkerLanguageServiceAccess.DiagnosticsEvidenceRead, engine.LastRequest.WorkerContext.LanguageServiceAccess);
        Assert.Contains(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "lsp_get_diagnostics");
        Assert.Contains(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "run_command");
        Assert.Contains(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "run_tests");
        Assert.DoesNotContain(engine.LastRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.Contains("Ivilson-Verify", engine.LastRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Ivilson-Sentry", engine.LastRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("VERIFY_REAL_LLM_OK", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultCodexKernel CreateKernel(
        IChatClient client,
        DefaultToolRegistry toolRegistry,
        DefaultAgentRoleRegistry roleRegistry,
        CodexSessionManager sessionManager,
        RecordingDelegatingRuntimeEngine runtimeEngine,
        DefaultWorkerDefinitionRegistry workerDefinitionRegistry)
    {
        return new DefaultCodexKernel(
            client,
            toolRegistry,
            Mock.Of<ICodexCritiqueService>(),
            roleRegistry,
            Mock.Of<ICodeAnalysisService>(),
            NullLogger<DefaultCodexKernel>.Instance,
            new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance).Object,
            sessionManager,
            null,
            null,
            runtimeEngine,
            null,
            workerDefinitionRegistry);
    }

    private static RecordingDelegatingRuntimeEngine CreateRuntimeEngine(IChatClient client)
    {
        var memoryAssembler = new Mock<IMemoryContextAssembler>();
        memoryAssembler
            .Setup(x => x.AssembleAsync(It.IsAny<MemoryContextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MemoryContextEnvelope.Empty);

        var llmExecutor = new DefaultLLMExecutor(
            client,
            memoryAssembler.Object,
            NullLogger<DefaultLLMExecutor>.Instance);
        var inner = new QreV2BackedQueryRuntimeEngine(
            llmExecutor,
            contextWindowManager: null,
            telemetry: null,
            logger: NullLogger<QreV2BackedQueryRuntimeEngine>.Instance);

        return new RecordingDelegatingRuntimeEngine(inner);
    }

    private static DefaultToolRegistry CreateToolRegistry()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        registry.RegisterTool(CreateTool("tool_search", ToolCategory.System));
        registry.RegisterTool(CreateTool("search_file_index", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("search_in_files", ToolCategory.Read));
        registry.RegisterTool(CreateTool("ivilson_read", ToolCategory.Read));
        registry.RegisterTool(CreateTool("ivilson_ls", ToolCategory.Read));
        registry.RegisterTool(CreateTool("list_workspace", ToolCategory.Read));
        registry.RegisterTool(CreateTool("analyze_project", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("analyze_code", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_get_diagnostics", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_document_symbols", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_find_references", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_go_to_definition", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("lsp_workspace_symbols", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("write_file", ToolCategory.Forge));
        registry.RegisterTool(CreateTool("run_command", ToolCategory.Forge));
        registry.RegisterTool(CreateTool("run_tests", ToolCategory.Analysis));
        registry.RegisterTool(CreateTool("generate_dev_plan", ToolCategory.Planning));
        return registry;
    }

    private static DelegateCodexTool CreateTool(string name, ToolCategory category)
        => new(
            name,
            $"{name} description",
            category,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok")));

    private static bool TryLoadRealApiSettings(out RealApiSettings settings)
    {
        settings = default!;

        var enabled = Environment.GetEnvironmentVariable("RUN_REAL_LLM_TOKEN_TESTS");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apiUrl = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_KEY");
        var model = Environment.GetEnvironmentVariable("IVILSON_VLLM_MODEL");
        var apiMode = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_MODE");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            var configPath = FindRepositoryFile(Path.Combine("CodexFlow", "appsettings.json"));
            if (configPath == null)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            if (!doc.RootElement.TryGetProperty("VllmAgent", out var vllmAgent))
            {
                return false;
            }

            apiUrl ??= vllmAgent.TryGetProperty("ApiUrl", out var apiUrlProp) ? apiUrlProp.GetString() : null;
            apiKey ??= vllmAgent.TryGetProperty("ApiKey", out var apiKeyProp) ? apiKeyProp.GetString() : null;
            model ??= vllmAgent.TryGetProperty("Model", out var modelProp) ? modelProp.GetString() : null;
            apiMode ??= vllmAgent.TryGetProperty("ApiMode", out var apiModeProp) ? apiModeProp.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        settings = new RealApiSettings(apiUrl, apiKey, model, apiMode);
        return true;
    }

    private static IChatClient CreateChatClient(RealApiSettings settings)
        => VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static string? FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed class RecordingDelegatingRuntimeEngine(IQueryRuntimeEngine inner) : IQueryRuntimeEngine
    {
        public QueryRuntimeRequest? LastRequest { get; private set; }

        public Task<QueryRuntimeResult> ExecuteAsync(
            QueryRuntimeRequest request,
            IQueryRuntimeEventSink eventSink,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return inner.ExecuteAsync(request, eventSink, ct);
        }
    }

    private sealed record RealApiSettings(string ApiUrl, string ApiKey, string Model, string? ApiMode);
}
