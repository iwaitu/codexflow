// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class CodexFlowRuntimeModelClientTests
{
    private sealed class RecordingExecutor : ILLMExecutor
    {
        private readonly Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _stream;

        public RecordingExecutor(Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream)
            => _stream = stream;

        public List<LLMExecutionRequest> Requests { get; } = [];

        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("StreamingMode.Streaming should not call ExecuteAsync.");

        public IAsyncEnumerable<ChatResponseUpdate> StreamAsync(LLMExecutionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return _stream(request, ct);
        }
    }

    private static QueryRuntimeRequest CreateCoreRequest(
        VllmChatOptions? options = null,
        Func<QueryRuntimeDynamicContextRequest, CancellationToken, ValueTask<QueryRuntimeDynamicContextResult?>>? dynamicProvider = null)
        => new()
        {
            SessionId = "session-1",
            EntryPoint = QueryLoopEntryPoint.ForgeWorker,
            Scenario = MemoryInjectionScenario.Execution,
            Session = new CodexSession { Id = "session-1", WorkspacePath = Path.GetTempPath() },
            InitialMessages = [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "hi")],
            Options = options ?? new VllmChatOptions
            {
                ModelId = "test-model",
                ThinkingEnabled = true,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["sampling_flag"] = "keep" }
            },
            DynamicContextProvider = dynamicProvider
        };

    private static QreProtocol.RuntimeModelRequest CreateProtocolRequest(
        IReadOnlyList<QreProtocol.RuntimeToolDescriptor>? tools = null,
        IReadOnlyList<QreProtocol.RuntimeMessage>? messages = null,
        string? requiredToolName = null)
        => new(
            new QreProtocol.RuntimeSessionId("session-1"),
            new QreProtocol.RuntimeTurnId("turn-1"),
            new QreProtocol.RuntimeStepId("step:1"),
            messages ??
            [
                new QreProtocol.RuntimeMessage(QreProtocol.RuntimeMessageRole.System, [new QreProtocol.RuntimeTextItem("sys")]),
                new QreProtocol.RuntimeMessage(QreProtocol.RuntimeMessageRole.User, [new QreProtocol.RuntimeTextItem("hi")])
            ],
            tools ?? [],
            new QreProtocol.RuntimeModelParameters(Model: "test-model", RequiredToolName: requiredToolName),
            HistoryVersion: 1);

    private static AIFunction CreateTestTool(string name)
        => AIFunctionFactory.Create((string arg) => arg, name, "test tool");

    private static QreProtocol.RuntimeToolDescriptor CreateDescriptor(AIFunction tool)
        => new(
            tool.Name,
            "1",
            tool.Description,
            tool.JsonSchema,
            QreProtocol.RuntimeToolSideEffect.ReadOnly,
            QreProtocol.RuntimeToolIdempotency.Idempotent);

    private static async Task<List<QreProtocol.RuntimeModelStreamEvent>> DrainAsync(
        IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> stream,
        CancellationToken ct = default)
    {
        var list = new List<QreProtocol.RuntimeModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(ct))
        {
            list.Add(item);
        }
        return list;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldUpdates(
        params ChatResponseUpdate[] updates)
    {
        await Task.Yield();
        foreach (var update in updates)
        {
            yield return update;
        }
    }

    [Fact]
    public async Task Differential_DynamicContextAndRequiredTool_MatchLegacyBehavior()
    {
        var tool = CreateTestTool("write_file");
        var descriptor = CreateDescriptor(tool);

        var coreReq = CreateCoreRequest(dynamicProvider: (_, _) => ValueTask.FromResult<QueryRuntimeDynamicContextResult?>(
            new QueryRuntimeDynamicContextResult
            {
                Messages = [new ChatMessage(ChatRole.User, "dynamic-note")],
                RecoveryHints = [new RuntimeRecoveryHint { Source = "guard", Message = "use write_file", RequiredToolName = "write_file" }],
                RequiredToolName = "write_file"
            }));
        var protoReq = CreateProtocolRequest(tools: [descriptor]);

        var legacyExec = new RecordingExecutor((_, _) => YieldUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]) { FinishReason = ChatFinishReason.Stop }));
        var meaiExec = new RecordingExecutor((_, _) => YieldUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]) { FinishReason = ChatFinishReason.Stop }));

        var legacy = new QreV2BackedQueryRuntimeEngine.CodexFlowV2ModelClient(
            legacyExec, coreReq, [tool], TimeSpan.FromSeconds(5), NullLogger.Instance);
        var modern = new CodexFlowRuntimeModelClient(
            meaiExec, coreReq, [tool], TimeSpan.FromSeconds(5), NullLogger.Instance);

        var legacyEvents = await DrainAsync(legacy.StreamAsync(protoReq));
        var modernEvents = await DrainAsync(modern.StreamAsync(protoReq, new QreProtocol.RuntimeModelAttemptContext(1)));

        Assert.Equal(
            legacyEvents.Select(e => e.GetType()),
            modernEvents.Select(e => e.GetType()));
        Assert.Equal(
            legacyExec.Requests[0].Messages.Select(m => m.Text),
            meaiExec.Requests[0].Messages.Select(m => m.Text));

        var legacyOpts = Assert.IsType<VllmChatOptions>(legacyExec.Requests[0].Options);
        var modernOpts = Assert.IsType<VllmChatOptions>(meaiExec.Requests[0].Options);
        Assert.False(legacyOpts.ThinkingEnabled);
        Assert.False(modernOpts.ThinkingEnabled);
        Assert.IsType<RequiredChatToolMode>(legacyOpts.ToolMode);
        Assert.IsType<RequiredChatToolMode>(modernOpts.ToolMode);
    }

    [Fact]
    public async Task Differential_EmptyStreamRetry_RetainsFirstUsageAndRetriesStreaming()
    {
        var tool = CreateTestTool("read_file");
        var coreReq = CreateCoreRequest();
        var protoReq = CreateProtocolRequest(tools: [CreateDescriptor(tool)]);

        static IAsyncEnumerable<ChatResponseUpdate> EmptyThenText(int callIndex)
        {
            if (callIndex == 1)
            {
                return YieldUpdates(
                    new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 1 })])
                    { FinishReason = ChatFinishReason.Stop });
            }

            return YieldUpdates(
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("recovered")]) { FinishReason = ChatFinishReason.Stop });
        }

        var legacyCalls = 0;
        var legacyExec = new RecordingExecutor((_, _) => EmptyThenText(++legacyCalls));
        var modernCalls = 0;
        var modernExec = new RecordingExecutor((_, _) => EmptyThenText(++modernCalls));

        var legacyEvents = await DrainAsync(
            new QreV2BackedQueryRuntimeEngine.CodexFlowV2ModelClient(legacyExec, coreReq, [tool], TimeSpan.FromSeconds(5), NullLogger.Instance)
                .StreamAsync(protoReq));
        var modernEvents = await DrainAsync(
            new CodexFlowRuntimeModelClient(modernExec, coreReq, [tool], TimeSpan.FromSeconds(5), NullLogger.Instance)
                .StreamAsync(protoReq));

        Assert.Equal(2, legacyCalls);
        Assert.Equal(2, modernCalls);
        Assert.Contains(legacyEvents, e => e is QreProtocol.RuntimeUsageEvent u && u.Usage.InputTokens == 10);
        Assert.Contains(modernEvents, e => e is QreProtocol.RuntimeUsageEvent u && u.Usage.InputTokens == 10);
        Assert.Contains(modernEvents, e => e is QreProtocol.RuntimeTextDeltaEvent t && t.Text == "recovered");
    }

    [Fact]
    public async Task Differential_TimeoutVsCallerCancellation_AreDistinguished()
    {
        var coreReq = CreateCoreRequest();
        var protoReq = CreateProtocolRequest();

        static async IAsyncEnumerable<ChatResponseUpdate> Hang([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            yield break;
        }

        var legacyTimeoutExec = new RecordingExecutor((_, ct) => Hang(ct));
        var modernTimeoutExec = new RecordingExecutor((_, ct) => Hang(ct));

        var legacyEx = await Assert.ThrowsAsync<QreProtocol.RuntimeModelClientException>(() =>
            DrainAsync(new QreV2BackedQueryRuntimeEngine.CodexFlowV2ModelClient(
                legacyTimeoutExec, coreReq, [], TimeSpan.FromMilliseconds(40), NullLogger.Instance).StreamAsync(protoReq)));
        var modernEx = await Assert.ThrowsAsync<QreProtocol.RuntimeModelClientException>(() =>
            DrainAsync(new CodexFlowRuntimeModelClient(
                modernTimeoutExec, coreReq, [], TimeSpan.FromMilliseconds(40), NullLogger.Instance).StreamAsync(protoReq)));

        Assert.Equal(QreProtocol.RuntimeErrorCategory.ProviderTransport, legacyEx.Error.Category);
        Assert.Equal(QreProtocol.RuntimeErrorCategory.ProviderTransport, modernEx.Error.Category);
        Assert.True(modernEx.Error.Retryable);

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DrainAsync(new CodexFlowRuntimeModelClient(
                new RecordingExecutor((_, ct) => Hang(ct)),
                coreReq, [], TimeSpan.FromSeconds(10), NullLogger.Instance).StreamAsync(protoReq, callerCts.Token), callerCts.Token));

        // Timeout during DynamicContextProvider must also classify as retryable ProviderTransport.
        var slowDynamicCore = CreateCoreRequest(dynamicProvider: async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return null;
        });
        var dynamicTimeoutEx = await Assert.ThrowsAsync<QreProtocol.RuntimeModelClientException>(() =>
            DrainAsync(new CodexFlowRuntimeModelClient(
                new RecordingExecutor((_, _) => YieldUpdates()),
                slowDynamicCore, [], TimeSpan.FromMilliseconds(40), NullLogger.Instance).StreamAsync(protoReq)));
        Assert.Equal(QreProtocol.RuntimeErrorCategory.ProviderTransport, dynamicTimeoutEx.Error.Category);
        Assert.True(dynamicTimeoutEx.Error.Retryable);
    }

    [Fact]
    public async Task OptionsClonedPerAttempt_WhenSamplingLayerMutatesAdditionalProperties()
    {
        var coreReq = CreateCoreRequest();
        var protoReq = CreateProtocolRequest();

        var exec = new RecordingExecutor((req, _) =>
        {
            // Simulate ConfiguredSamplingChatClient reading & removing a property in-place.
            Assert.NotNull(req.Options?.AdditionalProperties);
            Assert.True(req.Options!.AdditionalProperties!.Remove("sampling_flag"));
            return YieldUpdates(
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]) { FinishReason = ChatFinishReason.Stop });
        });

        var client = new CodexFlowRuntimeModelClient(exec, coreReq, [], TimeSpan.FromSeconds(5), NullLogger.Instance);

        await DrainAsync(client.StreamAsync(protoReq));
        await DrainAsync(client.StreamAsync(protoReq));

        Assert.Equal(2, exec.Requests.Count);
        Assert.True(coreReq.Options!.AdditionalProperties!.ContainsKey("sampling_flag"));
    }

    [Fact]
    public async Task UnexposedToolCallGuard_NudgesFirstThenStops()
    {
        var tool = CreateTestTool("read_file");
        var coreReq = CreateCoreRequest();

        var deniedItem = new QreProtocol.RuntimeToolResultItem(
            new QreProtocol.RuntimeToolResult(new QreProtocol.RuntimeInvocationId("c1"), null, false,
                new QreProtocol.RuntimeError(QreProtocol.RuntimeErrorCategory.PolicyDenied, UnexposedToolCallGuard.DeniedCode, "fake_tool")));
        var nudgeMessages = new List<QreProtocol.RuntimeMessage>
        {
            new(QreProtocol.RuntimeMessageRole.User, [new QreProtocol.RuntimeTextItem("hi")])
        };
        for (var i = 0; i < UnexposedToolCallGuard.NudgeAfterDenials; i++)
        {
            nudgeMessages.Add(new(QreProtocol.RuntimeMessageRole.Tool, [deniedItem]));
        }

        var exec = new RecordingExecutor((_, _) => YieldUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]) { FinishReason = ChatFinishReason.Stop }));
        var client = new CodexFlowRuntimeModelClient(exec, coreReq, [tool], TimeSpan.FromSeconds(5), NullLogger.Instance);

        await DrainAsync(client.StreamAsync(CreateProtocolRequest([CreateDescriptor(tool)], nudgeMessages)));
        Assert.Contains(exec.Requests[0].Messages, m => m.Text?.Contains("read_file", StringComparison.Ordinal) == true);

        var stopMessages = new List<QreProtocol.RuntimeMessage>(nudgeMessages);
        while (stopMessages.Count - 1 < UnexposedToolCallGuard.StopAfterDenials)
        {
            stopMessages.Add(new(QreProtocol.RuntimeMessageRole.Tool, [deniedItem]));
        }
        var ex = await Assert.ThrowsAsync<QreProtocol.RuntimeModelClientException>(() =>
            DrainAsync(client.StreamAsync(CreateProtocolRequest([CreateDescriptor(tool)], stopMessages))));
        Assert.Equal(UnexposedToolCallGuard.StopCode, ex.Error.Code);
    }

    [Fact]
    public async Task HttpCaptureHandler_ClassifiesRateLimitWithClassifyProviderErrors()
    {
        using var httpClient = QreModelHttpErrorCaptureHandler.CreateHttpClient(new StubStatusHandler(HttpStatusCode.TooManyRequests));
        async IAsyncEnumerable<ChatResponseUpdate> CallAndThrow([EnumeratorCancellation] CancellationToken ct)
        {
            using var response = await httpClient.GetAsync("http://127.0.0.1/v1/chat/completions", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("HTTP 429 dropped by SDK");
            }
            yield break;
        }

        var exec = new RecordingExecutor((_, ct) => CallAndThrow(ct));

        var client = new CodexFlowRuntimeModelClient(exec, CreateCoreRequest(), [], TimeSpan.FromSeconds(5), NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<QreProtocol.RuntimeModelClientException>(() =>
            DrainAsync(client.StreamAsync(CreateProtocolRequest())));
        Assert.True(ex.Error.Retryable);
    }

    private sealed class StubStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
