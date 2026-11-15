// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

public sealed class VllmChatClientFactoryTests
{
    [Theory]
    [InlineData(null, VllmApiMode.ChatCompletions)]
    [InlineData("", VllmApiMode.ChatCompletions)]
    [InlineData("ChatCompletions", VllmApiMode.ChatCompletions)]
    [InlineData("responses", VllmApiMode.Responses)]
    [InlineData("anthropic_messages", VllmApiMode.AnthropicMessages)]
    public void ResolveApiMode_ParsesKnownValues(string? rawValue, VllmApiMode expected)
    {
        var actual = VllmChatClientFactory.ResolveApiMode(rawValue);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Create_GlmFivePointOne_UsesGlmClient()
    {
        using var client = VllmChatClientFactory.Create(
            "https://example.test/apps/anthropic",
            "token",
            "glm-5.1",
            "AnthropicMessages");

        Assert.IsType<VllmGlmChatClient>(client);
    }

    [Fact]
    public void Create_WithUriEndpoint_UsesGlmClient()
    {
        using var client = VllmChatClientFactory.Create(
            new Uri("https://example.test/apps/anthropic"),
            "token",
            "glm-5.1",
            VllmApiMode.AnthropicMessages);

        Assert.IsType<VllmGlmChatClient>(client);
    }

    [Fact]
    public void Create_GeminiWithResponses_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            VllmChatClientFactory.Create(
                "https://example.test/v1",
                "token",
                "google/gemini-3.1-pro-preview",
                "Responses"));

        Assert.Contains("Gemini client does not support", ex.Message, StringComparison.Ordinal);
    }
}
