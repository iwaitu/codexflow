// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Planning.Committee;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests;

public class BufferedNonStreamingLlmHelperTests
{
    [Fact]
    public async Task GetJsonTextAsync_CleansMarkdownFence()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "```json\n{\"value\":1}\n```")]));

        var helper = new BufferedNonStreamingLlmHelper(mockChatClient.Object, NullLogger.Instance);

        var result = await helper.GetJsonTextAsync(
            [new ChatMessage(ChatRole.User, "return json")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            "test_json_call");

        Assert.Equal("{\"value\":1}", result);
    }

    [Fact]
    public async Task GetTextAsync_PropagatesCancellation()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("cancelled"));

        var helper = new BufferedNonStreamingLlmHelper(mockChatClient.Object, NullLogger.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => helper.GetTextAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions(),
            "cancelled_call",
            CancellationToken.None));
    }

    [Fact]
    public async Task ClassifyAsync_AmbiguousGoal_ParsesBufferedJsonResponse()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "```json\n{\"is_complex\":true,\"reason\":\"跨层影响\",\"indicators\":[\"cross-layer\"]}\n```")]));

        var classifier = new DefaultComplexityClassifier(
            mockChatClient.Object,
            NullLogger<DefaultComplexityClassifier>.Instance);

        var result = await classifier.ClassifyAsync(
            "实现用户列表页面的分页功能",
            "该功能会跨 controller、service 和 persistence 层修改");

        Assert.True(result.IsComplex);
        Assert.Equal("跨层影响", result.Reason);
        Assert.Equal(["cross-layer"], result.TriggeredIndicators);
    }

    [Fact]
    public async Task ClassifyAsync_AmbiguousGoal_WhenChatClientFails_FallsBackToSimple()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network failure"));

        var classifier = new DefaultComplexityClassifier(
            mockChatClient.Object,
            NullLogger<DefaultComplexityClassifier>.Instance);

        var result = await classifier.ClassifyAsync(
            "实现用户列表页面的分页功能",
            "普通分页需求");

        Assert.False(result.IsComplex);
        Assert.Equal("LLM 判定失败，默认视为简单需求", result.Reason);
        Assert.Empty(result.TriggeredIndicators);
    }

    [Fact]
    public async Task ClassifyAsync_AmbiguousGoal_WhenCancelled_PropagatesCancellation()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("cancelled"));

        var classifier = new DefaultComplexityClassifier(
            mockChatClient.Object,
            NullLogger<DefaultComplexityClassifier>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => classifier.ClassifyAsync(
            "实现用户列表页面的分页功能",
            "普通分页需求",
            CancellationToken.None));
    }
}
