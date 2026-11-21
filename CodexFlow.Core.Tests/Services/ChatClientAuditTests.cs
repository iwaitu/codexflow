// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

public sealed class ChatClientAuditTests
{
    [Fact]
    public void LogInteraction_PreservesLegacyFieldsAndPreviews()
    {
        var logger = new CapturingLogger();
        var options = new VllmChatOptions
        {
            Temperature = 0.25f,
            TopP = float.NaN,
            ToolMode = ChatToolMode.Auto,
            ThinkingEnabled = false,
            MaxOutputTokens = 128
        };
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "用户"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("one", new[] { "first", " ", "second" })])
        };

        ChatClientAudit.LogInteraction(logger, "source", "sync", messages, options, new string('x', 601), "思考",
            new InvalidOperationException("error text"));

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.StartsWith("FULLTEXT|", line.Message, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line.Message["FULLTEXT|".Length..]);
        var root = document.RootElement;
        Assert.NotEqual(default, root.GetProperty("timestamp").GetDateTime());
        Assert.Equal("ichatclient_interaction_error", root.GetProperty("eventType").GetString());
        Assert.Equal("__ichatclient__", root.GetProperty("sessionId").GetString());
        Assert.Equal(0, root.GetProperty("stage").GetInt32());
        Assert.Equal(0, root.GetProperty("round").GetInt32());
        var payload = root.GetProperty("payload");
        Assert.Equal("source", payload.GetProperty("source").GetString());
        Assert.Equal("sync", payload.GetProperty("mode").GetString());
        Assert.Equal(new string('x', 600) + "...[truncated]", payload.GetProperty("response").GetString());
        Assert.Equal(601, payload.GetProperty("responseLength").GetInt32());
        Assert.Equal("思考", payload.GetProperty("thinking").GetString());
        Assert.Equal(2, payload.GetProperty("thinkingLength").GetInt32());
        Assert.Equal("error text", payload.GetProperty("error").GetString());
        var request = payload.GetProperty("request");
        Assert.Equal("用户", request[0].GetProperty("text").GetString());
        Assert.Equal(1, request[1].GetProperty("index").GetInt32());
        Assert.Equal("tool", request[1].GetProperty("role").GetString());
        Assert.Equal("[tool_result:one] first second", request[1].GetProperty("text").GetString());
        var loggedOptions = payload.GetProperty("options");
        Assert.Equal(0.25f, loggedOptions.GetProperty("temperature").GetSingle());
        Assert.Equal("NaN", loggedOptions.GetProperty("topP").GetString());
        Assert.Equal(0, loggedOptions.GetProperty("toolCount").GetInt32());
        Assert.Equal(0, loggedOptions.GetProperty("toolNames").GetArrayLength());
        Assert.Equal(options.ToolMode.ToString(), loggedOptions.GetProperty("toolMode").GetString());
        Assert.False(loggedOptions.GetProperty("thinkingEnabled").GetBoolean());
        Assert.Equal(128, loggedOptions.GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public void LogInteraction_WhenOptionsAreMissing_PreservesNullFields()
    {
        var logger = new CapturingLogger();

        ChatClientAudit.LogInteraction(logger, "source", "sync", [], new ChatOptions(), "", "");

        var line = Assert.Single(logger.Entries);
        using var document = JsonDocument.Parse(line.Message["FULLTEXT|".Length..]);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        var options = payload.GetProperty("options");
        Assert.Equal(JsonValueKind.Null, options.GetProperty("thinkingEnabled").ValueKind);
        Assert.Equal(JsonValueKind.Null, options.GetProperty("toolMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, options.GetProperty("maxOutputTokens").ValueKind);
    }

    [Fact]
    public void ReasoningAndResponseRetainTheirAggregation()
    {
        var response = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatClientAudit.AppendStreamingUpdate(
            new ReasoningChatResponseUpdate
            {
                Thinking = true,
                Reasoning = "separate provider field",
                Contents = [new TextContent("thought")]
            },
            response,
            reasoning);
        ChatClientAudit.AppendStreamingUpdate(
            new ChatResponseUpdate { Contents = [new TextContent("answer")] },
            response,
            reasoning);

        // The previous audit adds the provider update's Text and its TextContent.
        Assert.Equal("thoughtthought", reasoning.ToString());
        Assert.Equal("answer", response.ToString());

        var combined = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("thought"), new TextContent("answer")]));
        Assert.Equal(("answer", "thought"), ChatClientAudit.ExtractResponse(combined));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
