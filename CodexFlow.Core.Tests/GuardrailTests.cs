// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using CodexFlow.Core.Services;
using Xunit;
using System.Text.Json;

namespace CodexFlow.Core.Tests.Agents;

public class GuardrailTests
{
    [Fact]
    public async Task RunLoopAsync_ShouldTriggerGuardrail_WhenWritingToCriticalFileWithoutHighRisk()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var mockToolRegistry = new Mock<IToolRegistry>();
        var mockCritique = new Mock<ICodexCritiqueService>();
        var mockRoles = new Mock<IAgentRoleRegistry>();
        var mockAnalysis = new Mock<ICodeAnalysisService>();

        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockSessionManager = new Mock<CodexSessionManager>(Mock.Of<ISessionStore>(), Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance);

        mockToolRegistry.Setup(r => r.GetAvailableTools(It.IsAny<CodexSession>()))
                        .Returns(new List<ICodexTool> { 
                            new DelegateCodexTool("write_file", "write", ToolCategory.Forge, new List<int>{3}, (a, c) => Task.FromResult(CodexToolResult.Succeeded("OK")))
                        });

        var kernel = new DefaultCodexKernel(
            mockChatClient.Object, 
            mockToolRegistry.Object, 
            mockCritique.Object, 
            mockRoles.Object, 
            mockAnalysis.Object, 
            NullLogger<DefaultCodexKernel>.Instance,
            mockScanner.Object, 
            mockSessionManager.Object);

        var session = new CodexSession { 
            Id = "s1", 
            ActiveTaskId = "t1",
            Plan = { new CodexTask { Id = "t1", RiskLevel = "Low" } },
            WorkspacePath = "/tmp"
        };

        // 模拟 AI 尝试修改一个核心文件
        var toolCall = new FunctionCallContent("call_1", "write_file", new Dictionary<string, object?> { { "path", "Core/CriticalService.cs" } });
        
        // 第一轮：触发工具调用
        var firstUpdate = new ChatResponseUpdate { Contents = new List<AIContent> { toolCall } };
        // 第二轮：在收到警告后，AI 决定停止（返回文本）
        var stopUpdate = new ChatResponseUpdate { Contents = new List<AIContent> { new TextContent("I will stop as requested.") } };

        int callCount = 0;
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => {
                          callCount++;
                          var toolCallMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { toolCall });
                          var stopMsg = new ChatMessage(ChatRole.Assistant, "I will stop as requested.");
                          
                          if (callCount == 1) return new ChatResponse(new[] { toolCallMsg });
                          return new ChatResponse(new[] { stopMsg });
                      });

        // 模拟依赖分析认定这是高风险熔断点
        mockAnalysis.Setup(a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DependencyGraph());
        mockAnalysis.Setup(a => a.CheckGuardrailAsync(It.IsAny<DependencyGraph>(), "Core/CriticalService.cs", "Low"))
                    .ReturnsAsync(new GuardrailResult(true, "[Level 4] 核心组件物理锁定"));

        mockCritique.Setup(c => c.ReviewAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new CritiqueResult(true, "OK"));

        // Act
        await kernel.RunLoopAsync(session, "Fix CriticalService", CodexAgentRole.Forge);

        // Assert
        // 验证第二次调用前，上下文消息里是否已经包含了“熔断预警”
        mockChatClient.Verify(c => c.GetResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(m => m.Any(msg => msg.Role == ChatRole.User && msg.Text != null && msg.Text.Contains("熔斷預警"))),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }
}
