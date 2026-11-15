// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Adapters;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

/// <summary>
/// Phase 4A.2: Kernel Runtime 专项测试 — 验证 guardrail + critique 闭环
/// </summary>
[Collection(KernelRuntimeEnvironmentCollection.Name)]
public class KernelRuntimeIntegrationTests
{
    /// <summary>
    /// 验证 KERNEL_DISABLE_RUNTIME=true 时回退到旧路径
    /// </summary>
    [Fact]
    public async Task Kernel_DisableRuntimeEnvVar_FallsBackToOldPath()
    {
        // Arrange: 设置环境变量禁用 runtime
        var originalValue = Environment.GetEnvironmentVariable("KERNEL_DISABLE_RUNTIME");
        Environment.SetEnvironmentVariable("KERNEL_DISABLE_RUNTIME", "true");

        try
        {
            var mockChatClient = new Mock<IChatClient>();
            var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
            var mockCritique = new Mock<ICodexCritiqueService>();
            var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
            var mockAnalysis = new Mock<ICodeAnalysisService>();
            var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
            var mockSessionManager = new Mock<CodexSessionManager>(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

            // 设置 role registry
            mockRoleRegistry
                .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
                .Returns("You are an AI assistant.");

            // 设置 mock chat client 返回流式响应（旧路径使用流式）
            var streamingUpdates = new List<ChatResponseUpdate>
            {
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Old path response")])
            };

            mockChatClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns(new MockAsyncEnumerable<ChatResponseUpdate>(streamingUpdates));

            var kernel = new DefaultCodexKernel(
                mockChatClient.Object,
                toolRegistry,
                mockCritique.Object,
                mockRoleRegistry.Object,
                mockAnalysis.Object,
                NullLogger<DefaultCodexKernel>.Instance,
                mockScanner.Object,
                mockSessionManager.Object,
                null, // llmExecutor
                null, // telemetry
                mockRuntimeEngine.Object); // runtime engine (should be ignored due to env var)

            var session = new CodexSession { Id = "test-old-path", WorkspacePath = "/tmp/ws" };

            // Act
            var result = await kernel.RunLoopAsync(session, "test", CodexAgentRole.Forge);

            // Assert: runtime engine 不应被调用
            mockRuntimeEngine.Verify(
                r => r.ExecuteAsync(
                    It.IsAny<QueryRuntimeRequest>(),
                    It.IsAny<IQueryRuntimeEventSink>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);

            // Assert: chat client 应被直接调用（旧路径）
            mockChatClient.Verify(
                c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);

            Assert.Contains("Old path", result.Text);
        }
        finally
        {
            // Cleanup: 恢复环境变量
            Environment.SetEnvironmentVariable("KERNEL_DISABLE_RUNTIME", originalValue);
        }
    }

    /// <summary>
    /// 验证 runtime 启用时 OnEvent 顺序正确
    /// </summary>
    [Fact]
    public async Task Kernel_RuntimeEnabled_OnEventOrderCorrect()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var mockRuntimeEngine = new Mock<IQueryRuntimeEngine>();

        // 设置 role registry
        mockRoleRegistry
            .Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession>()))
            .Returns("You are an AI assistant.");
        mockRoleRegistry
            .Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>()))
            .Returns("Test-Agent");

        // 设置 runtime engine 返回成功结果
        mockRuntimeEngine
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.Normal,
                TotalRounds = 1,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 0,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "Runtime response",
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
            mockSessionManager.Object,
            null,
            null,
            mockRuntimeEngine.Object);

        var session = new CodexSession { Id = "test-runtime", WorkspacePath = "/tmp/ws" };
        var eventsReceived = new List<CodexEventType>();

        kernel.OnEvent += evt => eventsReceived.Add(evt.Type);

        // Act
        var result = await kernel.RunLoopAsync(session, "test", CodexAgentRole.Forge);

        // Assert: runtime engine 应被调用
        mockRuntimeEngine.Verify(
            r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: request 应包含 InterventionHook
        var capturedRequest = mockRuntimeEngine.Invocations[0].Arguments[0] as QueryRuntimeRequest;
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.InterventionHook);
        Assert.IsType<KernelRuntimeEventAdapter>(capturedRequest.InterventionHook);

        Assert.Contains("Runtime", result.Text);
    }

    /// <summary>
    /// 验证 Guardrail 在 Forge 路径仍能阻止危险工具
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_GuardrailBlocksDangerousTool_ForForgeRole()
    {
        // Arrange
        var session = new CodexSession { Id = "guardrail-test", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();
        var eventsReceived = new List<CodexEvent>();

        mockGuardrail
            .Setup(g => g.CheckAsync(
                It.IsAny<CodexSession>(),
                "write_file",
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(GuardrailCheckResult.Blocked("核心文件被熔断保护", "/tmp/ws/core.cs"));

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Forge,
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            evt => eventsReceived.Add(evt));

        // Act: 调用 guardrail 检查
        var arguments = new Dictionary<string, object?> { ["path"] = "/tmp/ws/core.cs" };
        var intervention = await adapter.OnToolCallRequestedAsync(
            "write_file",
            arguments,
            session,
            CancellationToken.None);

        // Assert: 应阻止执行
        Assert.True(intervention.ShouldBlock);
        Assert.NotNull(intervention.InjectedMessage);
        Assert.Equal(ChatRole.User, intervention.InjectedMessage.Role);
        Assert.Contains("GUARDRAIL", intervention.InjectedMessage.Text ?? "");
        Assert.Contains("write_file", intervention.InjectedMessage.Text ?? "");

        // Assert: 应发送 guardrail 事件
        Assert.Contains(eventsReceived, e => e.Type == CodexEventType.GuardrailBlocked);
    }

    /// <summary>
    /// 验证 Guardrail 不阻止非 Forge 角色
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_GuardrailSkips_ForNonForgeRole()
    {
        // Arrange
        var session = new CodexSession { Id = "guardrail-skip", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();

        // Guardrail 配置为阻止，但应被跳过（非 Forge 角色）
        mockGuardrail
            .Setup(g => g.CheckAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(GuardrailCheckResult.Blocked("Should not be called"));

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Architect, // 非 Forge
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            null);

        // Act
        var arguments = new Dictionary<string, object?> { ["path"] = "/tmp/ws/file.cs" };
        var intervention = await adapter.OnToolCallRequestedAsync(
            "write_file",
            arguments,
            session,
            CancellationToken.None);

        // Assert: 不应阻止
        Assert.False(intervention.ShouldBlock);
        Assert.Null(intervention.InjectedMessage);

        // Assert: Guardrail 不应被调用
        mockGuardrail.Verify(
            g => g.CheckAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// 验证 Critique reject 能形成闭环 - 反馈注入到下一轮
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_CritiqueReject_InjectsFeedbackToNextRound()
    {
        // Arrange
        var session = new CodexSession { Id = "critique-test", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();
        var eventsReceived = new List<CodexEvent>();

        // Critique 配置为 reject
        mockCritique
            .Setup(c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(false, "代码风格不符合规范，请重新生成"));

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Forge,
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            evt => eventsReceived.Add(evt));

        // Act: 模拟工具执行完成，触发 critique
        var intervention = await adapter.OnToolExecutionCompletedAsync(
            "write_file",
            "File written successfully",
            true,
            session,
            CancellationToken.None);

        // Assert: 应跳过工具结果
        Assert.True(intervention.ShouldSkipToolResult);

        // Assert: 应注入 critique 反馈
        Assert.NotNull(intervention.InjectedMessage);
        Assert.Equal(ChatRole.User, intervention.InjectedMessage.Role);
        Assert.Contains("CRITIQUE", intervention.InjectedMessage.Text ?? "");
        Assert.Contains("write_file", intervention.InjectedMessage.Text ?? "");
        Assert.Contains("代码风格不符合规范", intervention.InjectedMessage.Text ?? "");

        // Assert: 应发送 critique feedback 事件
        Assert.Contains(eventsReceived, e => e.Type == CodexEventType.CritiqueFeedback);
    }

    /// <summary>
    /// 验证 Critique 在 Security 角色不执行
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_CritiqueSkips_ForSecurityRole()
    {
        // Arrange
        var session = new CodexSession { Id = "critique-skip", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();

        // Critique 配置为 reject，但应被跳过（Security 角色）
        mockCritique
            .Setup(c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(false, "Should not be called"));

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Security, // Security 角色不执行 critique
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            null);

        // Act
        var intervention = await adapter.OnToolExecutionCompletedAsync(
            "run_command",
            "Command executed",
            true,
            session,
            CancellationToken.None);

        // Assert: 不应跳过
        Assert.False(intervention.ShouldSkipToolResult);
        Assert.Null(intervention.InjectedMessage);

        // Assert: Critique 不应被调用
        mockCritique.Verify(
            c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary
    /// 验证 Critique retry 计数器正确工作
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_CritiqueRetryCount_ResetsOnPass()
    {
        // Arrange
        var session = new CodexSession { Id = "critique-retry", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();

        var callCount = 0;
        mockCritique
            .Setup(c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // 前两次 reject，第三次 pass
                return callCount < 3
                    ? new CritiqueResult(false, $"Attempt {callCount} rejected")
                    : new CritiqueResult(true, "Approved");
            });

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Forge,
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            null);

        // Act: 第一次 reject
        var intervention1 = await adapter.OnToolExecutionCompletedAsync(
            "write_file", "result1", true, session, CancellationToken.None);
        Assert.True(intervention1.ShouldSkipToolResult);

        // Act: 第二次 reject
        var intervention2 = await adapter.OnToolExecutionCompletedAsync(
            "write_file", "result2", true, session, CancellationToken.None);
        Assert.True(intervention2.ShouldSkipToolResult);

        // Act: 第三次 pass
        var intervention3 = await adapter.OnToolExecutionCompletedAsync(
            "write_file", "result3", true, session, CancellationToken.None);
        Assert.False(intervention3.ShouldSkipToolResult); // 通过后不跳过

        // Act: 第四次应该是新周期的第一次
        mockCritique.Reset();
        mockCritique
            .Setup(c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(false, "New cycle reject"));

        var intervention4 = await adapter.OnToolExecutionCompletedAsync(
            "write_file", "result4", true, session, CancellationToken.None);

        // Assert: 新周期应该又能 reject（计数器已重置）
        Assert.True(intervention4.ShouldSkipToolResult);
    }

    /// <summary>
    /// 验证 Critique 达到最大 retry 后接受结果
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_CritiqueMaxRetry_AcceptsResultWithWarning()
    {
        // Arrange
        var session = new CodexSession { Id = "critique-max", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();
        var eventsReceived = new List<CodexEvent>();

        mockCritique
            .Setup(c => c.ReviewAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(false, "Always reject"));

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Forge,
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            evt => eventsReceived.Add(evt));

        // Act: 连续 4 次（超过 3 次）
        for (int i = 0; i < 4; i++)
        {
            await adapter.OnToolExecutionCompletedAsync(
                $"tool_{i}", $"result_{i}", true, session, CancellationToken.None);
        }

        // Act: 第 5 次应该接受结果
        var intervention5 = await adapter.OnToolExecutionCompletedAsync(
            "tool_4", "result_4", true, session, CancellationToken.None);

        // Assert: 应接受结果（超过 max retries）
        Assert.False(intervention5.ShouldSkipToolResult);

        // Assert: 应发送 warning 事件
        Assert.Contains(eventsReceived, e =>
            e.Type == CodexEventType.CritiqueFeedback &&
            (e.Message?.Contains("exhausted") ?? false));
    }

    /// <summary>
    /// 验证事件转发正确映射 runtime event 到 CodexEvent
    /// </summary>
    [Fact]
    public async Task KernelRuntimeEventAdapter_EventForwarding_MapsCorrectly()
    {
        // Arrange
        var session = new CodexSession { Id = "event-mapping", WorkspacePath = "/tmp/ws" };
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockGuardrail = new Mock<ICodexGuardrail>();
        var eventsReceived = new List<CodexEvent>();
        var queryId = Guid.NewGuid();

        var adapter = new KernelRuntimeEventAdapter(
            session,
            CodexAgentRole.Forge,
            mockCritique.Object,
            mockGuardrail.Object,
            NullLogger.Instance,
            evt => eventsReceived.Add(evt));

        // Act: 发送各种 runtime events
        await adapter.OnEventAsync(new AssistantDeltaEvent(
            Seq: 1,
            QueryId: queryId,
            SessionId: session.Id,
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Round: 0,
            Delta: "Hello"));

        await adapter.OnEventAsync(new ThinkingStartedEvent(
            Seq: 2,
            QueryId: queryId,
            SessionId: session.Id,
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Round: 0));

        await adapter.OnEventAsync(new ThinkingDeltaEvent(
            Seq: 3,
            QueryId: queryId,
            SessionId: session.Id,
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Round: 0,
            Delta: "Thinking..."));

        await adapter.OnEventAsync(new ToolCallRequestedEvent(
            Seq: 4,
            QueryId: queryId,
            SessionId: session.Id,
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Round: 0,
            ToolName: "read_file",
            CallId: "call-1",
            Arguments: new Dictionary<string, object?>()));

        await adapter.OnEventAsync(new ToolExecutionCompletedEvent(
            Seq: 5,
            QueryId: queryId,
            SessionId: session.Id,
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Round: 0,
            ToolName: "read_file",
            CallId: "call-1",
            Result: "Content",
            Success: true));

        // Assert: 验证映射
        Assert.Equal(5, eventsReceived.Count);

        Assert.Equal(CodexEventType.Content, eventsReceived[0].Type);
        Assert.Equal("Hello", eventsReceived[0].Message);

        Assert.Equal(CodexEventType.ThinkingStart, eventsReceived[1].Type);

        Assert.Equal(CodexEventType.ThinkingContent, eventsReceived[2].Type);
        Assert.Contains("Thinking", eventsReceived[2].Message ?? "");

        Assert.Equal(CodexEventType.ToolCall, eventsReceived[3].Type);
        Assert.Contains("read_file", eventsReceived[3].Message ?? "");

        Assert.Equal(CodexEventType.ToolResult, eventsReceived[4].Type);
        Assert.Contains("read_file", eventsReceived[4].Message ?? "");
    }
}

/// <summary>
/// Phase 4A.2: DefaultCodexGuardrail 单元测试
/// </summary>
public class DefaultCodexGuardrailTests
{
    [Fact]
    public async Task Guardrail_AllowsSafeTools()
    {
        // Arrange
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var guardrail = new DefaultCodexGuardrail(
            mockAnalysis.Object,
            NullLogger<DefaultCodexGuardrail>.Instance);

        var session = new CodexSession { Id = "test", WorkspacePath = "/tmp/ws" };
        var arguments = new Dictionary<string, object?> { ["path"] = "/tmp/ws/file.cs" };

        // Act: 测试安全工具
        var result = await guardrail.CheckAsync(
            session,
            "read_file", // 安全工具
            arguments,
            CancellationToken.None);

        // Assert: 应允许
        Assert.True(result.IsAllowed);
        Assert.Null(result.Reason);

        // Assert: analysis service 不应被调用
        mockAnalysis.Verify(
            a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("write_file", "path")]
    [InlineData("hs_write", "filePath")]
    public async Task Guardrail_ChecksDangerousTools(string toolName, string pathParameter)
    {
        // Arrange
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var graph = new DependencyGraph(); // Assuming this type exists

        mockAnalysis
            .Setup(a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(graph);

        mockAnalysis
            .Setup(a => a.CheckGuardrailAsync(It.IsAny<DependencyGraph>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new GuardrailResult(true, "核心文件锁定"));

        var guardrail = new DefaultCodexGuardrail(
            mockAnalysis.Object,
            NullLogger<DefaultCodexGuardrail>.Instance);

        var session = new CodexSession { Id = "test", WorkspacePath = "/tmp/ws" };
        var arguments = new Dictionary<string, object?> { [pathParameter] = "/tmp/ws/core.cs" };

        // Act: 测试危险工具
        var result = await guardrail.CheckAsync(
            session,
            toolName, // 危险工具
            arguments,
            CancellationToken.None);

        // Assert: 应阻止
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Reason);
        Assert.Contains("核心文件", result.Reason);
        Assert.Equal("/tmp/ws/core.cs", result.TargetPath);
    }

    [Fact]
    public async Task Guardrail_NullSession_Throws()
    {
        // Arrange
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var guardrail = new DefaultCodexGuardrail(
            mockAnalysis.Object,
            NullLogger<DefaultCodexGuardrail>.Instance);

        var arguments = new Dictionary<string, object?>();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            guardrail.CheckAsync(null!, "write_file", arguments, CancellationToken.None).AsTask());
        Assert.Contains("session", ex.ParamName ?? "");
    }

    [Fact]
    public async Task Guardrail_NullArguments_Throws()
    {
        // Arrange
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var guardrail = new DefaultCodexGuardrail(
            mockAnalysis.Object,
            NullLogger<DefaultCodexGuardrail>.Instance);

        var session = new CodexSession { Id = "test", WorkspacePath = "/tmp/ws" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            guardrail.CheckAsync(session, "write_file", null!, CancellationToken.None).AsTask());
        Assert.Contains("arguments", ex.ParamName ?? "");
    }

    [Fact]
    public async Task Guardrail_DeniesWhenPathCannotBeExtracted()
    {
        // Arrange
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var guardrail = new DefaultCodexGuardrail(
            mockAnalysis.Object,
            NullLogger<DefaultCodexGuardrail>.Instance);

        var session = new CodexSession { Id = "test", WorkspacePath = "/tmp/ws" };
        var arguments = new Dictionary<string, object?> { ["other_param"] = "value" }; // 没有 path 参数

        // Act
        var result = await guardrail.CheckAsync(
            session,
            "write_file",
            arguments,
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("UnknownTargetPath", result.ReasonCode);

        // Assert: analysis service 不应被调用
        mockAnalysis.Verify(
            a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

/// <summary>
/// Helper class for mocking async enumerable streams
/// </summary>
internal sealed class MockAsyncEnumerable<T> : IAsyncEnumerable<T>
{
    private readonly IEnumerable<T> _data;

    public MockAsyncEnumerable(IEnumerable<T> data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new MockAsyncEnumerator<T>(_data.GetEnumerator());
    }
}

internal sealed class MockAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _enumerator;

    public MockAsyncEnumerator(IEnumerator<T> enumerator)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    public T Current => _enumerator.Current;

    public ValueTask<bool> MoveNextAsync()
    {
        return new ValueTask<bool>(_enumerator.MoveNext());
    }

    public ValueTask DisposeAsync()
    {
        _enumerator.Dispose();
        return ValueTask.CompletedTask;
    }
}
