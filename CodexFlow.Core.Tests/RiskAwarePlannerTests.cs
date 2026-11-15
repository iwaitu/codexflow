// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Xunit;
using Newtonsoft.Json;
using System.Runtime.CompilerServices;

namespace CodexFlow.Core.Tests.Agents;

public class RiskAwarePlannerTests
{
    [Fact]
    public async Task GeneratePlanAsync_ShouldMarkHighRisk_WhenCriticalFileIsTargeted()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);

        var criticalFile = "CodexFlow.Core/Services/SemanticDependencyScanner.cs";
        var session = new CodexSession
        {
            Id = "s1",
            ProjectSummary = "A polyglot coding agent project.",
            ActiveFacts = { new MemoryEntry("DependencyGraphSummary", $"Total Files: 10\nTop Critical Files:\n- {criticalFile} (Refs: 12)", "general", DateTime.Now) }
        };

        // 模拟 LLM 返回符合风控规则的 JSON
        var expectedTasks = new List<CodexTask>
        {
            new CodexTask 
            { 
                Id = "TASK_HIGH_RISK", 
                Title = "Update Scanner Logic", 
                Description = "Modify SemanticDependencyScanner.cs to support C++. 执行回归测试和边界校验。", 
                RiskLevel = "High",
                StageId = 3
            }
        };
        var jsonResponse = JsonConvert.SerializeObject(expectedTasks);

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                      .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(jsonResponse, ct));

        // Act
        var plan = await planner.GeneratePlanAsync(session, $"Update {criticalFile}");

        // Assert
        Assert.NotEmpty(plan);
        var task = plan.First();
        Assert.Equal("High", task.RiskLevel);
        Assert.Contains("回归测试", task.Description);
        Assert.Contains("边界校验", task.Description);
        
        // 验证 Prompt 中是否包含依赖摘要（确保投喂成功）
        mockChatClient.Verify(c => c.GetStreamingResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(m => m.Any(msg => msg.Text != null && msg.Text.Contains(criticalFile))),
            It.IsAny<ChatOptions>(), 
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamText(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
    }
}
