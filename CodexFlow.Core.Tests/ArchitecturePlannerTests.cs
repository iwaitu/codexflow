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
using System.Runtime.CompilerServices;

namespace CodexFlow.Core.Tests.Agents;

public class ArchitecturePlannerTests
{
    [Fact]
    public async Task GeneratePlanAsync_ShouldInsertRefactorTask_WhenArchitectureDebtIsPresent()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);

        var session = new CodexSession
        {
            Id = "s1",
            ProjectSummary = "A legacy project.",
            ActiveFacts = { new MemoryEntry("ArchitectureAudit", "Detected God Object: Core/GodService.cs (Refactoring Priority: 80)", "general", DateTime.Now) }
        };

        // 模拟 AI 返回包含重构任务的 JSON
        var jsonResponse = @"
        [
          {
            ""Id"": ""TASK_REF_001"",
            ""Title"": ""[Refactor] GodService Decoupling"",
            ""Description"": ""Extract interfaces from GodService to handle architectural debt."",
            ""Dependencies"": [],
            ""StageId"": 3,
            ""RiskLevel"": ""High""
          },
          {
            ""Id"": ""TASK_002"",
            ""Title"": ""Implement Feature"",
            ""Description"": ""Add new logic using the refactored service."",
            ""Dependencies"": [""TASK_REF_001""],
            ""StageId"": 3,
            ""RiskLevel"": ""Medium""
          }
        ]";

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                      .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(jsonResponse, ct));

        // Act
        var plan = await planner.GeneratePlanAsync(session, "Update GodService logic");

        // Assert
        Assert.Contains(plan, t => t.Title.Contains("[Refactor]"));
        Assert.Contains(plan, t => t.RiskLevel == "High");
        
        // 验证 System Prompt 是否包含架构审计关键词
        mockChatClient.Verify(c => c.GetStreamingResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(m => m.Any(msg => msg.Role == ChatRole.System && msg.Text != null && msg.Text.Contains("架构审计发现", StringComparison.Ordinal))),
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
