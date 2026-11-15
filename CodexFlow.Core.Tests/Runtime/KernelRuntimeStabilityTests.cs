// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Adapters;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Kimi;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

[Collection(KernelRuntimeEnvironmentCollection.Name)]
public class KernelRuntimeStabilityTests
{
    [Fact]
    public async Task KernelRuntimeStabilityTests_RuntimePath_ProjectsExpectedRequestAndResponse()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

        toolRegistry.RegisterTool(new DelegateCodexTool(
            "tool_search",
            "Search tool catalog.",
            ToolCategory.System,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("tool_search"))));
        toolRegistry.RegisterTool(new DelegateCodexTool(
            "write_file",
            "Write a file.",
            ToolCategory.Forge,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))));
        toolRegistry.RegisterTool(new DelegateCodexTool(
            "generate_dev_plan",
            "Generate a plan.",
            ToolCategory.Planning,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))));
        toolRegistry.RegisterTool(new DelegateCodexTool(
            "lsp_get_diagnostics",
            "Get diagnostics.",
            ToolCategory.Analysis,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))));

        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("You are an AI assistant.");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Forge-Agent");

        QueryRuntimeRequest? capturedRequest = null;
        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRuntimeRequest, IQueryRuntimeEventSink, CancellationToken>((request, _, _) => capturedRequest = request)
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 2,
                TotalToolCalls = 3,
                WriteToolCalls = 1,
                ZeroToolCallRounds = 1,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "Runtime response",
                FinalThinking = "thinking",
                TotalDurationMs = 100
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager,
            null,
            null,
            mockRuntimeEngine.Object,
            null,
            workerDefinitionRegistry);

        var session = new CodexSession { Id = "kernel-runtime", WorkspacePath = "/tmp/ws" };

        var result = await kernel.RunLoopAsync(
            session,
            "test runtime projection",
            CodexAgentRole.Forge,
            TestContext.Current.CancellationToken,
            enableTools: true);

        Assert.NotNull(capturedRequest);
        Assert.Equal(QueryLoopEntryPoint.DefaultCodexKernel, capturedRequest!.EntryPoint);
        Assert.Equal("kernel-runtime", capturedRequest.SessionId);
        Assert.Equal(60, capturedRequest.MaxRounds);
        Assert.True(capturedRequest.EnableTools);
        Assert.Equal(MemoryInjectionScenario.Execution, capturedRequest.Scenario);
        Assert.NotNull(capturedRequest.AdapterHints);
        Assert.True(capturedRequest.AdapterHints!.EnableEmptyResponseRecovery);
        Assert.True(capturedRequest.AdapterHints.EnableMalformedProtocolRecovery);
        Assert.True(capturedRequest.AdapterHints.EnableTransportFailureRecovery);
        Assert.True(capturedRequest.AdapterHints.EnableToolDeduplication);
        Assert.NotNull(capturedRequest.InterventionHook);
        Assert.IsType<KernelRuntimeEventAdapter>(capturedRequest.InterventionHook);
        Assert.NotNull(capturedRequest.WorkerContext);
        Assert.Equal(WorkerType.Forge, capturedRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.ShadowWorktree, capturedRequest.WorkerContext.IsolationMode);
        Assert.Contains("write_file", capturedRequest.WorkerContext.AllowedToolNames);
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "lsp_get_diagnostics");
        Assert.DoesNotContain(capturedRequest.AvailableTools ?? [], tool => tool.Name == "generate_dev_plan");
        var options = Assert.IsType<VllmChatOptions>(capturedRequest.Options);
        Assert.False(options.ThinkingEnabled);
        Assert.True(options.EnableLegacyToolCallTextFallback);

        Assert.Equal("Runtime response", result.Text);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.TotalToolCalls);
        Assert.Equal(1, result.WriteToolCalls);
        Assert.Equal("thinking", result.ThinkingContent);
    }

    [Fact]
    public async Task KernelRuntimeStabilityTests_SentryRole_MapsToVerifyWorkerContextAndReadonlyTools()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var workerDefinitionRegistry = new DefaultWorkerDefinitionRegistry();
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

        toolRegistry.RegisterTool(new DelegateCodexTool(
            "tool_search",
            "Search tool catalog.",
            ToolCategory.System,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("tool_search"))));
        toolRegistry.RegisterTool(new DelegateCodexTool(
            "write_file",
            "Write a file.",
            ToolCategory.Forge,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))));
        toolRegistry.RegisterTool(new DelegateCodexTool(
            "lsp_get_diagnostics",
            "Get diagnostics.",
            ToolCategory.Analysis,
            [0, 1, 2, 3, 4, 5],
            static (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))));

        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("Legacy role prompt");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Sentry-Agent");

        QueryRuntimeRequest? capturedRequest = null;
        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
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
                FinalText = "Verify response",
                TotalDurationMs = 5
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager,
            null,
            null,
            mockRuntimeEngine.Object,
            null,
            workerDefinitionRegistry);

        var session = new CodexSession { Id = "kernel-verify", WorkspacePath = "/tmp/ws" };

        var result = await kernel.RunLoopAsync(
            session,
            "verify this task",
            CodexAgentRole.Sentry,
            TestContext.Current.CancellationToken,
            enableTools: true);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest!.WorkerContext);
        Assert.Equal(WorkerType.Verify, capturedRequest.WorkerContext!.WorkerType);
        Assert.Equal(WorkerIsolationMode.Workspace, capturedRequest.WorkerContext.IsolationMode);
        Assert.Equal(WorkerLanguageServiceAccess.DiagnosticsEvidenceRead, capturedRequest.WorkerContext.LanguageServiceAccess);
        Assert.Contains("lsp_get_diagnostics", capturedRequest.WorkerContext.AllowedToolNames);
        Assert.Contains(capturedRequest.AvailableTools ?? [], tool => tool.Name == "lsp_get_diagnostics");
        Assert.DoesNotContain(capturedRequest.AvailableTools ?? [], tool => tool.Name == "write_file");
        Assert.Contains("验证", capturedRequest.InitialMessages[0].Text ?? string.Empty, StringComparison.Ordinal);
        var options = Assert.IsType<VllmChatOptions>(capturedRequest.Options);
        Assert.True(options.ThinkingEnabled);
        Assert.True(options.EnableLegacyToolCallTextFallback);
        Assert.Equal("Verify response", result.Text);
    }

    [Fact]
    public async Task KernelRuntimeStabilityTests_RuntimeFailure_FallsBackToLegacyPath()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("You are an AI assistant.");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Forge-Agent");

        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("runtime failed"));

        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(new MockAsyncEnumerable<ChatResponseUpdate>(
                [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("legacy fallback response")])]));

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager,
            null,
            null,
            mockRuntimeEngine.Object);

        var session = new CodexSession { Id = "kernel-fallback", WorkspacePath = "/tmp/ws" };
        var result = await kernel.RunLoopAsync(
            session,
            "test fallback",
            CodexAgentRole.Forge,
            TestContext.Current.CancellationToken,
            enableTools: false);

        mockRuntimeEngine.Verify(
            r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        mockChatClient.Verify(
            c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        Assert.Contains("legacy fallback", result.Text);
    }

    [Fact]
    public async Task KernelRuntimeStabilityTests_RuntimeCancellation_PropagatesCanceledTokenThroughFallback()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();
        var runtimeSawCanceledToken = false;
        var fallbackSawCanceledToken = false;

        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("You are an AI assistant.");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Forge-Agent");

        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRuntimeRequest, IQueryRuntimeEventSink, CancellationToken>((_, _, ct) => runtimeSawCanceledToken = ct.IsCancellationRequested)
            .ThrowsAsync(new OperationCanceledException("runtime canceled"));

        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, _, ct) =>
            {
                fallbackSawCanceledToken = ct.IsCancellationRequested;
                return new CancelingAsyncEnumerable<ChatResponseUpdate>(ct);
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager,
            null,
            null,
            mockRuntimeEngine.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var session = new CodexSession { Id = "kernel-canceled", WorkspacePath = "/tmp/ws" };
        var originalDisableRuntime = Environment.GetEnvironmentVariable("KERNEL_DISABLE_RUNTIME");
        Environment.SetEnvironmentVariable("KERNEL_DISABLE_RUNTIME", null);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => kernel.RunLoopAsync(
                session,
                "test canceled runtime",
                CodexAgentRole.Forge,
                cts.Token,
                enableTools: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KERNEL_DISABLE_RUNTIME", originalDisableRuntime);
        }

        Assert.True(runtimeSawCanceledToken);
        Assert.True(fallbackSawCanceledToken);
    }

    [Fact]
    public async Task KernelRuntimeStabilityTests_RuntimeEvents_AreForwardedToOnEventInOrder()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("You are an AI assistant.");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Forge-Agent");

        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (QueryRuntimeRequest request, IQueryRuntimeEventSink sink, CancellationToken ct) =>
            {
                var queryId = Guid.NewGuid();
                await sink.OnEventAsync(new ThinkingStartedEvent(1, queryId, request.SessionId, request.EntryPoint, 0));
                await sink.OnEventAsync(new ThinkingDeltaEvent(2, queryId, request.SessionId, request.EntryPoint, 0, "reasoning"));
                await sink.OnEventAsync(new ThinkingEndedEvent(3, queryId, request.SessionId, request.EntryPoint, 0, "reasoning complete"));
                await sink.OnEventAsync(new AssistantDeltaEvent(4, queryId, request.SessionId, request.EntryPoint, 0, "answer"));
                await sink.OnEventAsync(new ToolCallRequestedEvent(5, queryId, request.SessionId, request.EntryPoint, 0, "search_file_index", "call-1", new Dictionary<string, object?>()));
                await sink.OnEventAsync(new ToolExecutionCompletedEvent(6, queryId, request.SessionId, request.EntryPoint, 0, "search_file_index", "call-1", "match", true, 10));
                await sink.OnEventAsync(new RecoveryTriggeredEvent(7, queryId, request.SessionId, request.EntryPoint, 0, "empty_response", 1, "empty assistant response"));
                await sink.OnEventAsync(new ErrorEvent(8, queryId, request.SessionId, request.EntryPoint, "SampleError", "boom"));

                return new QueryRuntimeResult
                {
                    TerminationReason = QueryTerminationReason.NoToolCalls,
                    TotalRounds = 1,
                    TotalToolCalls = 1,
                    ZeroToolCallRounds = 0,
                    EmptyResponseCount = 0,
                    RecoveryCount = 1,
                    MalformedProtocolCount = 0,
                    FinalText = "done",
                    TotalDurationMs = 10
                };
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager,
            null,
            null,
            mockRuntimeEngine.Object);

        var forwarded = new List<CodexEvent>();
        kernel.OnEvent += forwarded.Add;

        var session = new CodexSession { Id = "kernel-events", WorkspacePath = "/tmp/ws" };
        var result = await kernel.RunLoopAsync(
            session,
            "test event forwarding",
            CodexAgentRole.Forge,
            TestContext.Current.CancellationToken,
            enableTools: true);

        Assert.Equal("done", result.Text);
        Assert.Equal(
            [
                CodexEventType.ThinkingStart,
                CodexEventType.ThinkingContent,
                CodexEventType.ThinkingEnd,
                CodexEventType.Content,
                CodexEventType.ToolCall,
                CodexEventType.ToolResult,
                CodexEventType.RecoveryTriggered,
                CodexEventType.Error
            ],
            forwarded.Select(e => e.Type).ToArray());
    }

    private sealed class CancelingAsyncEnumerable<T>(CancellationToken cancellationToken) : IAsyncEnumerable<T>
    {
        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ct.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
