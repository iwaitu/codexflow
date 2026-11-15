// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Newtonsoft.Json;
using System.Net.Http;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class KernelTests
{
    [Fact]
    public async Task RunLoopAsync_ShouldRetryTransientTransportFailure_WithoutInjectingParseErrorFeedback()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(Mock.Of<ISessionStore>(), Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);

        var session = new CodexSession { Id = "s-transport", ProjectSummary = "Test transport retry", WorkspacePath = "/tmp/ws" };
        var capturedPrompts = new List<List<ChatMessage>>();

        var callCount = 0;
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
            {
                capturedPrompts.Add(messages.ToList());
            })
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    throw new HttpRequestException("An error occurred while sending the request.", new Exception("The response ended prematurely. (ResponseEnded)"));
                }

                return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [new TextContent("Recovered")]) });
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            mockSessionManager.Object);

        var result = await kernel.RunLoopAsync(session, "hello", CodexAgentRole.Forge);

        Assert.Contains("Recovered", result.Text);
        Assert.Equal(3, callCount);
        Assert.All(capturedPrompts, prompt =>
        {
            Assert.DoesNotContain(prompt, m =>
                m.Role == ChatRole.User
                && m.Text != null
                && m.Text.Contains("系统解析错误", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task RunLoopAsync_ShouldCallTool_AndStop_WhenModelFinished()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockTool = new Mock<ICodexTool>();
        mockTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();

        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(Mock.Of<ISessionStore>(), Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);

        mockTool.Setup(t => t.Name).Returns("test_tool");
        mockTool.Setup(t => t.Description).Returns("A test tool");
        mockTool.Setup(t => t.Category).Returns(ToolCategory.Read);
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CodexToolResult.Succeeded("Tool Success"));

        mockCritique.Setup(c => c.ReviewAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new CritiqueResult(true, "Looks good"));

        toolRegistry.RegisterTool(mockTool.Object);

        var session = new CodexSession { Id = "test-session", ProjectSummary = "Test Context" };
        var kernel = new DefaultCodexKernel(mockChatClient.Object, toolRegistry, mockCritique.Object, mockRoleRegistry.Object, mockAnalysis.Object, NullLogger<DefaultCodexKernel>.Instance, mockScanner.Object, mockSessionManager.Object);

        // First response: Tool call
        var toolCall = new FunctionCallContent("call_1", "test_tool", new Dictionary<string, object?> { ["arg1"] = "val1" });
        var firstUpdate = new ChatResponseUpdate { Contents = [toolCall] };

        // Second response: Final text (No tool calls)
        var finalUpdate = new ChatResponseUpdate { Contents = [new TextContent("All Done")] };

        int callCount = 0;
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => {
                callCount++;
                if (callCount == 1) return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [toolCall]) });
                return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [new TextContent("All Done")]) });
            });

        // Act
        var result = await kernel.RunLoopAsync(session, "Do something");

        // Assert
        Assert.Contains("All Done", result.Text);
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunLoopAsync_ShouldEmitTrace_WhenSearchFileIndexToolIsCalled()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);

        var session = new CodexSession
        {
            Id = "s-search",
            ProjectSummary = "Test",
            WorkspacePath = "/tmp/ws",
            CurrentStage = 3
        };

        var indexJson = JsonConvert.SerializeObject(new List<FileIndexEntry>
        {
            new FileIndexEntry { Path = "src/AppFile.cs", Type = "C# Source", Size = 123 }
        });

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("s-search", It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync("s-search", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                new MemoryEntry("ProjectFileIndex", indexJson, "fact", DateTime.UtcNow)
            });
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        toolRegistry.RegisterTool(new SearchFileIndexTool(sessionManager, NullLogger<SearchFileIndexTool>.Instance));

        mockCritique.Setup(c => c.ReviewAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(true, "ok"));

        var toolCall = new FunctionCallContent("c-search", "search_file_index", new Dictionary<string, object?>
        {
            ["query"] = "AppFile"
        });

        var callCount = 0;
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [toolCall]) });
                }
                return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [new TextContent("done")]) });
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            sessionManager);

        var emitted = new List<CodexEvent>();
        kernel.OnEvent += e => emitted.Add(e);

        // Act
        var result = await kernel.RunLoopAsync(session, "find file", CodexAgentRole.Forge);

        // Assert
        Assert.Contains("done", result.Text);
        Assert.Contains(emitted, e => e.Message.Contains("Executed tool search_file_index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunLoopAsync_ShouldRecover_FromEmptyToolName_AndContinueExecution()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockTool = new Mock<ICodexTool>();
        mockTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(Mock.Of<ISessionStore>(), Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);

        mockTool.Setup(t => t.Name).Returns("test_tool");
        mockTool.Setup(t => t.Description).Returns("A test tool");
        mockTool.Setup(t => t.Category).Returns(ToolCategory.Read);
        mockTool.Setup(t => t.AllowedStages).Returns(new List<int> { 3 });
        mockTool.Setup(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CodexToolResult.Succeeded("Tool Success"));

        toolRegistry.RegisterTool(mockTool.Object);

        mockCritique.Setup(c => c.ReviewAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CritiqueResult(true, "ok"));

        var session = new CodexSession
        {
            Id = "s-empty-tool",
            ProjectSummary = "Test",
            CurrentStage = 3,
            WorkspacePath = "/tmp/ws"
        };

        var emptyToolCall = new FunctionCallContent("call_empty", "", new Dictionary<string, object?>());
        var validToolCall = new FunctionCallContent("call_valid", "test_tool", new Dictionary<string, object?> { ["x"] = 1 });

        var callCount = 0;
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [emptyToolCall]) });
                }
                if (callCount == 2)
                {
                    return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [validToolCall]) });
                }
                return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [new TextContent("done")]) });
            });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            mockSessionManager.Object);

        // Act
        var result = await kernel.RunLoopAsync(session, "continue after empty tool", CodexAgentRole.Forge);

        // Assert
        Assert.Contains("done", result.Text);
        Assert.True(callCount >= 3, "Kernel should continue loop after empty tool name.");
        mockTool.Verify(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunLoopAsync_ShouldEmbedToolCatalogInSystemPrompt_WhenToolsEnabled()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoleRegistry = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(Mock.Of<ISessionStore>(), Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);

        var alwaysOnTool = new Mock<ICodexTool>();
        alwaysOnTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        alwaysOnTool.Setup(t => t.Name).Returns("run_tests");
        alwaysOnTool.Setup(t => t.Description).Returns("Run the project test suite");
        alwaysOnTool.Setup(t => t.Category).Returns(ToolCategory.Sentry);
        alwaysOnTool.Setup(t => t.AllowedStages).Returns(Array.Empty<int>());

        var deferredTool = new Mock<ICodexTool>();
        deferredTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        deferredTool.Setup(t => t.Name).Returns("ivilson_smart_patch");
        deferredTool.Setup(t => t.Description).Returns("Apply focused patches");
        deferredTool.Setup(t => t.Category).Returns(ToolCategory.Forge);
        deferredTool.Setup(t => t.AllowedStages).Returns(Array.Empty<int>());

        toolRegistry.RegisterTool(alwaysOnTool.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(deferredTool.Object, CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        mockRoleRegistry.Setup(r => r.GetSystemPrompt(It.IsAny<CodexAgentRole>(), It.IsAny<CodexSession?>())).Returns("Role prompt");
        mockRoleRegistry.Setup(r => r.GetRoleName(It.IsAny<CodexAgentRole>())).Returns("Forge");

        var capturedMessages = new List<ChatMessage>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
            {
                capturedMessages = messages.ToList();
            })
            .ReturnsAsync(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, [new TextContent("done")]) }));

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object,
            toolRegistry,
            mockCritique.Object,
            mockRoleRegistry.Object,
            mockAnalysis.Object,
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object,
            mockSessionManager.Object);

        var session = new CodexSession { Id = "s-catalog", ProjectSummary = "Test context", WorkspacePath = "/tmp/ws" };

        var result = await kernel.RunLoopAsync(session, "hello");

        Assert.Contains("done", result.Text);
        var systemPrompt = capturedMessages.Single(m => m.Role == ChatRole.System).Text ?? string.Empty;
        Assert.Contains("## 工具使用准则", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("工具调用被拒绝", systemPrompt, StringComparison.Ordinal);
    }
}
