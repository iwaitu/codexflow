// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using System.Runtime.CompilerServices;
using Xunit;

namespace CodexFlow.Core.Tests;

public class AdaptivePlannerTests
{
    private readonly Mock<IChatClient> _mockChat;
    private readonly Mock<ILogger<DefaultCodexPlanner>> _mockLogger;
    private readonly DefaultCodexPlanner _planner;

    public AdaptivePlannerTests()
    {
        _mockChat = new Mock<IChatClient>();
        _mockLogger = new Mock<ILogger<DefaultCodexPlanner>>();
        _planner = new DefaultCodexPlanner(_mockChat.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task Planner_Should_Assign_ComplexityLevel_Correctly()
    {
        // Arrange
        var session = new CodexSession 
        { 
            Id = "session-adaptive-001",
            ProjectSummary = "A C# library for data processing.",
            ActiveFacts = { new MemoryEntry("ArchitectureAudit", "Found God Object: CoreProcessor.cs", "Audit", DateTime.Now) }
        };

        // Mock LLM response with various complexity levels
        var mockResponse = @"[
            {
                ""Id"": ""T1"",
                ""Title"": ""[Refactor] Split CoreProcessor"",
                ""Description"": ""Extract interface from God Object CoreProcessor."",
                ""StageId"": 3,
                ""RiskLevel"": ""High"",
                ""ComplexityLevel"": 3
            },
            {
                ""Id"": ""T2"",
                ""Title"": ""Add Feature X"",
                ""Description"": ""Implement new processing logic."",
                ""StageId"": 3,
                ""RiskLevel"": ""Medium"",
                ""ComplexityLevel"": 2
            },
            {
                ""Id"": ""T3"",
                ""Title"": ""Update Docs"",
                ""Description"": ""Update README.md"",
                ""StageId"": 3,
                ""RiskLevel"": ""Low"",
                ""ComplexityLevel"": 1
            }
        ]";

        _mockChat.Setup(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(mockResponse, ct));

        // Act
        var tasks = await _planner.GeneratePlanAsync(session, "Refactor CoreProcessor and add docs");

        // Assert
        Assert.NotNull(tasks);
        Assert.Equal(3, tasks.Count);

        var t1 = tasks.First(t => t.Id == "T1");
        Assert.Equal(3, t1.ComplexityLevel); // Refactor -> Level 3
        Assert.Equal("High", t1.RiskLevel);

        var t2 = tasks.First(t => t.Id == "T2");
        Assert.Equal(2, t2.ComplexityLevel); // Feature -> Level 2
        Assert.Equal("Medium", t2.RiskLevel);

        var t3 = tasks.First(t => t.Id == "T3");
        Assert.Equal(1, t3.ComplexityLevel); // Docs -> Level 1
        Assert.Equal("Low", t3.RiskLevel);
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
