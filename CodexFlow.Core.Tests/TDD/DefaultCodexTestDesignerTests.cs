// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests.TDD;

public class DefaultCodexTestDesignerTests
{
    [Fact]
    public async Task DesignTestsAsync_Should_Generate_TestCode_From_LLM_Response()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var logger = NullLogger<DefaultCodexTestDesigner>.Instance;
        var designer = new DefaultCodexTestDesigner(mockChatClient.Object, logger);

        var task = new CodexTask { Id = "T1", Title = "Calc", Description = "Implement Add", ComplexityLevel = 2 };
        var session = new CodexSession { Id = "S1", ActiveFacts = { new MemoryEntry("ProjectLanguage", "CSHARP", "fact", DateTime.UtcNow) } };

        // Mock LLM Response
        var jsonResponse = """
        {
            "reasoning": "Standard xUnit test",
            "files": [
                {
                    "path": "test/CalculatorTests.cs",
                    "content": "public class CalculatorTests { [Fact] public void TestAdd() {} }"
                }
            ]
        }
        """;
        
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, jsonResponse) }));

        // Act
        var plan = await designer.DesignTestsAsync(task, session);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal("xUnit", plan.Framework); // Default for C#
        Assert.Single(plan.TestFiles);
        Assert.Equal("test/CalculatorTests.cs", plan.TestFiles[0].FilePath);
        Assert.Contains("public class CalculatorTests", plan.TestFiles[0].Content);
    }

    [Theory]
    [InlineData("", "Auto-generated", false)]
    [InlineData("{}", "Auto-generated", false)]
    [InlineData("null", "Auto-generated", false)]
    [InlineData("{ /* legacy */ 'Reasoning': 1.50, 'ignored': {'x': 1}, 'Files': [{ 'PATH': 'test/CalculatorTests.cs', 'Content': true, },], }", "1.50", true)]
    public async Task DesignTestsAsync_PreservesLegacyJsonResponses(string json, string expectedReasoning, bool hasFile)
    {
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, json) }));
        var designer = new DefaultCodexTestDesigner(client.Object, NullLogger<DefaultCodexTestDesigner>.Instance);

        var plan = await designer.DesignTestsAsync(new CodexTask { Id = "T1" }, new CodexSession { Id = "S1" });

        Assert.Equal(expectedReasoning, plan.Reasoning);
        if (hasFile)
        {
            var file = Assert.Single(plan.TestFiles);
            Assert.Equal("test/CalculatorTests.cs", file.FilePath);
            Assert.Equal("true", file.Content);
        }
        else
        {
            Assert.Empty(plan.TestFiles);
        }
    }

    [Fact]
    public async Task DesignTestsAsync_Should_Handle_Timeout()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var logger = NullLogger<DefaultCodexTestDesigner>.Instance;
        var designer = new DefaultCodexTestDesigner(mockChatClient.Object, logger);
        
        var task = new CodexTask { Id = "T1", Description = "Timeout Test" };
        var session = new CodexSession { Id = "S1" };

        // Mock Timeout: Delay longer than 120s (simulated by throwing OperationCanceledException when token cancelled)
        mockChatClient.Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var plan = await designer.DesignTestsAsync(task, session);

        // Assert
        Assert.Equal("Timeout generating tests", plan.Reasoning);
        Assert.Empty(plan.TestFiles);
    }
}
