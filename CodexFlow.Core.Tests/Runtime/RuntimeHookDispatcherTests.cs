// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public class RuntimeHookDispatcherTests
{
    [Fact]
    public async Task DispatchAfterModelResponseAsync_ShouldInvokeRegisteredHook_AndApplyMutation()
    {
        var hook = new RecordingRuntimeHook();
        var dispatcher = new RuntimeHookDispatcher([hook], NullLogger<RuntimeHookDispatcher>.Instance);

        var result = await dispatcher.DispatchAfterModelResponseAsync(CreateContext(
            responseText: "original",
            thinkingText: "thought"));

        Assert.Equal([(0, "original", "thought")], hook.Invocations);
        Assert.Equal("hooked-original", result.ResponseText);
        Assert.Equal("hooked-thought", result.ThinkingText);
    }

    [Fact]
    public async Task DispatchAfterModelResponseAsync_ShouldPreserveRegistrationOrder_AndFoldMutations()
    {
        var observations = new List<string>();
        var dispatcher = new RuntimeHookDispatcher(
        [
            new DelegatingRuntimeHook(context =>
            {
                observations.Add($"first:{context.ResponseText}|{context.ThinkingText}");
                return new AfterModelResponseHookResult
                {
                    ResponseText = $"{context.ResponseText}-first",
                    ThinkingText = $"{context.ThinkingText}-first"
                };
            }),
            new DelegatingRuntimeHook(context =>
            {
                observations.Add($"second:{context.ResponseText}|{context.ThinkingText}");
                return new AfterModelResponseHookResult
                {
                    ResponseText = $"{context.ResponseText}-second"
                };
            }),
            new DelegatingRuntimeHook(context =>
            {
                observations.Add($"third:{context.ResponseText}|{context.ThinkingText}");
                return AfterModelResponseHookResult.None;
            })
        ],
        NullLogger<RuntimeHookDispatcher>.Instance);

        var result = await dispatcher.DispatchAfterModelResponseAsync(CreateContext(
            responseText: "seed",
            thinkingText: "brain"));

        Assert.Equal(
        [
            "first:seed|brain",
            "second:seed-first|brain-first",
            "third:seed-first-second|brain-first"
        ],
        observations);
        Assert.Equal("seed-first-second", result.ResponseText);
        Assert.Equal("brain-first", result.ThinkingText);
    }

    [Fact]
    public async Task DispatchAfterModelResponseAsync_ShouldLogAndContinue_WhenOneHookFails()
    {
        var observations = new List<string>();
        var dispatcher = new RuntimeHookDispatcher(
        [
            new ThrowingRuntimeHook(),
            new DelegatingRuntimeHook(context =>
            {
                observations.Add($"{context.ResponseText}|{context.ThinkingText}");
                return new AfterModelResponseHookResult
                {
                    ResponseText = "recovered-response",
                    ThinkingText = "recovered-thinking"
                };
            })
        ],
        NullLogger<RuntimeHookDispatcher>.Instance);

        var result = await dispatcher.DispatchAfterModelResponseAsync(CreateContext(
            responseText: "original",
            thinkingText: "thought"));

        Assert.Equal(["original|thought"], observations);
        Assert.Equal("recovered-response", result.ResponseText);
        Assert.Equal("recovered-thinking", result.ThinkingText);
    }

    [Fact]
    public async Task DispatchBeforeStopAsync_ShouldReturnFirstContinuationDecision()
    {
        var observations = new List<string>();
        var dispatcher = new RuntimeHookDispatcher(
        [
            new DelegatingRuntimeHook(
                _ => AfterModelResponseHookResult.None,
                context =>
                {
                    observations.Add($"first:{context.LastAssistantMessage}");
                    return BeforeStopHookResult.None;
                }),
            new DelegatingRuntimeHook(
                _ => AfterModelResponseHookResult.None,
                context =>
                {
                    observations.Add($"second:{context.StopHookActive}");
                    return new BeforeStopHookResult
                    {
                        Continue = true,
                        Message = "run tests before stopping",
                        Reason = "missing verification"
                    };
                }),
            new DelegatingRuntimeHook(
                _ => AfterModelResponseHookResult.None,
                _ =>
                {
                    observations.Add("third");
                    return BeforeStopHookResult.None;
                })
        ],
        NullLogger<RuntimeHookDispatcher>.Instance);

        var result = await dispatcher.DispatchBeforeStopAsync(CreateStopContext("premature final"));

        Assert.Equal(["first:premature final", "second:False"], observations);
        Assert.True(result.Continue);
        Assert.Equal("run tests before stopping", result.Message);
        Assert.Equal("missing verification", result.Reason);
    }

    private static AfterModelResponseContext CreateContext(string responseText, string thinkingText)
        => new()
        {
            Request = new QueryRuntimeRequest
            {
                SessionId = "session-runtime-hook",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages = [new ChatMessage(ChatRole.User, "test prompt")]
            },
            Round = 0,
            ResponseText = responseText,
            ThinkingText = thinkingText,
            ToolCalls = []
        };

    private static BeforeStopContext CreateStopContext(string lastAssistantMessage)
        => new()
        {
            Request = new QueryRuntimeRequest
            {
                SessionId = "session-runtime-hook",
                EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
                InitialMessages = [new ChatMessage(ChatRole.User, "test prompt")]
            },
            Round = 0,
            LastAssistantMessage = lastAssistantMessage,
            ThinkingText = "thought",
            StopHookActive = false,
            ContinuationCount = 0
        };

    private sealed class RecordingRuntimeHook : IRuntimeHook
    {
        public List<(int Round, string ResponseText, string ThinkingText)> Invocations { get; } = [];

        public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
            AfterModelResponseContext context,
            CancellationToken ct = default)
        {
            Invocations.Add((context.Round, context.ResponseText, context.ThinkingText));
            return ValueTask.FromResult(new AfterModelResponseHookResult
            {
                ResponseText = $"hooked-{context.ResponseText}",
                ThinkingText = $"hooked-{context.ThinkingText}"
            });
        }

        public ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
            BeforeStopContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(BeforeStopHookResult.None);
    }

    private sealed class DelegatingRuntimeHook(
        Func<AfterModelResponseContext, AfterModelResponseHookResult> handler,
        Func<BeforeStopContext, BeforeStopHookResult>? stopHandler = null) : IRuntimeHook
    {
        public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
            AfterModelResponseContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(handler(context));

        public ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
            BeforeStopContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(stopHandler?.Invoke(context) ?? BeforeStopHookResult.None);
    }

    private sealed class ThrowingRuntimeHook : IRuntimeHook
    {
        public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
            AfterModelResponseContext context,
            CancellationToken ct = default)
            => throw new InvalidOperationException("runtime hook failure");

        public ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
            BeforeStopContext context,
            CancellationToken ct = default)
            => throw new InvalidOperationException("runtime stop hook failure");
    }
}
