// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Core.Telemetry;
using CodexFlow.Gateway;
using CodexFlow.Gateway.Adapters;
using CodexFlow.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class P3TurnCancelPlanAndToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p3-turn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task CancelTurnEndpoint_CancelsRunningTurn_DeniesPendingApproval_EmitsCancelledDone_AndKeepsChannelAlive()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IExecutionApprovalBroker? capturedBroker = null;
        Task<ExecutionAuthorizationResult>? pendingAuthTask = null;

        var processor = new DelegateGatewayMessageProcessor(async (msg, emit, ct) =>
        {
            if (msg.Content == "turn-1")
            {
                await emit(new GatewaySseEvent { Type = GatewaySseEventType.Content, Payload = new { type = "content", content = "working...", text = "working..." } });
                var req = new ExecutionRequest
                {
                    SessionId = msg.SessionId,
                    TurnId = msg.MessageId,
                    WorkspaceRoot = _root,
                    ToolName = "exec_cmd",
                    Executable = "dotnet",
                    InterpreterChain = ["dotnet"],
                    Argv = ["--version"],
                    Cwd = ".",
                    EnvironmentDelta = [],
                    Backend = ExecutionBackend.Local,
                    Network = "Host",
                    Filesystem = "unrestricted"
                };
                pendingAuthTask = capturedBroker!.AuthorizeAsync(
                    new ExecutionAuthorizationContext(req, _root, _root, _root, null),
                    ct);
                turn1Started.TrySetResult();
                await pendingAuthTask.ConfigureAwait(false);
            }
            else
            {
                await emit(new GatewaySseEvent { Type = GatewaySseEventType.Content, Payload = new { type = "content", content = "turn-2-ok", text = "turn-2-ok" } });
                turn2Completed.TrySetResult(msg.Content);
            }
        });

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            customProcessor: processor);
        capturedBroker = host.Broker;

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=CancelTest", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        var observedEvents = new ConcurrentQueue<(string Type, string RawJson)>();
        var cancelledDoneReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sseTask = Task.Run(async () =>
        {
            using var sseReq = new HttpRequestMessage(HttpMethod.Get, $"/api/gateway/sse?sessionId={Uri.EscapeDataString(sessionId)}");
            using var sseResp = await client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
            using var stream = await sseResp.Content.ReadAsStreamAsync(sseCts.Token);
            using var reader = new StreamReader(stream);
            string? currentEvent = null;
            while (!sseCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(sseCts.Token);
                if (line is null) break;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent is not null)
                {
                    var data = line[5..].Trim();
                    observedEvents.Enqueue((currentEvent, data));
                    if (currentEvent == "done" && data.Contains("\"cancelled\"", StringComparison.Ordinal))
                    {
                        cancelledDoneReceived.TrySetResult(data);
                    }

                    currentEvent = null;
                }
            }
        }, sseCts.Token);

        // Wait until SSE subscriber is attached so Ask doesn't fast-fail headless
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Gateway.HasActiveSubscribers(sessionId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Start turn 1
        var chat1 = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "turn-1" });
        Assert.Equal(HttpStatusCode.Accepted, chat1.StatusCode);

        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Wait until pending approval is registered
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Broker.GetPendingApprovalCount(sessionId) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(1, host.Broker.GetPendingApprovalCount(sessionId));

        // Cancel turn 1
        var cancelResp = await client.PostAsync($"/api/gateway/session/{sessionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var cancelDto = await cancelResp.Content.ReadFromJsonAsync<CancelTurnResponse>();
        Assert.True(cancelDto!.Cancelled);

        // Pending approval must be denied & resolved (turn ct throws OperationCanceledException after publishing deny)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingAuthTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.Broker.GetPendingApprovalCount(sessionId));

        // SSE receives done{source:"cancelled"}
        var doneData = await cancelledDoneReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"source\":\"cancelled\"", doneData, StringComparison.Ordinal);

        // Channel remains alive for turn 2
        var chat2 = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "turn-2" });
        Assert.Equal(HttpStatusCode.Accepted, chat2.StatusCode);
        Assert.Equal("turn-2", await turn2Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        sseCts.Cancel();
        try { await sseTask; } catch (OperationCanceledException) { }
        Assert.Contains(observedEvents, e => e.Type == GatewaySseEventType.ToolApprovalResolved && e.RawJson.Contains("\"deny\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelTurn_DoesNotCancelConcurrentBackgroundJobApproval_AndLegacyToolLoopPropagatesCancellation()
    {
        var globalSettings = Path.Combine(_root, "global-b7", "appsettings.json");
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IExecutionApprovalBroker? brokerRef = null;
        Task<ExecutionAuthorizationResult>? turnAuthTask = null;

        var processor = new DelegateGatewayMessageProcessor(async (msg, _, ct) =>
        {
            var req = new ExecutionRequest
            {
                SessionId = msg.SessionId,
                TurnId = msg.MessageId,
                WorkspaceRoot = _root,
                ToolName = "exec_cmd",
                Executable = "dotnet",
                InterpreterChain = ["dotnet"],
                Argv = ["--info"],
                Cwd = ".",
                EnvironmentDelta = [],
                Backend = ExecutionBackend.Local,
                Network = "Host",
                Filesystem = "unrestricted"
            };
            turnAuthTask = brokerRef!.AuthorizeAsync(new ExecutionAuthorizationContext(req, _root, _root, _root, null), ct);
            turnStarted.TrySetResult();
            await turnAuthTask.ConfigureAwait(false);
        });

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            customProcessor: processor);
        brokerRef = host.Broker;

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=B7Isolation", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sseTask = client.GetStreamAsync($"/api/gateway/sse?sessionId={Uri.EscapeDataString(sessionId)}", sseCts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Gateway.HasActiveSubscribers(sessionId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Start a concurrent background job approval using an independent CTS
        using var jobCts = new CancellationTokenSource();
        var jobReq = new ExecutionRequest
        {
            SessionId = sessionId,
            TurnId = "job-turn-independent",
            WorkspaceRoot = _root,
            ToolName = "exec_cmd",
            Executable = "python",
            InterpreterChain = ["python"],
            Argv = ["script.py"],
            Cwd = ".",
            EnvironmentDelta = [],
            Backend = ExecutionBackend.Local,
            Network = "Host",
            Filesystem = "unrestricted"
        };
        var jobAuthTask = host.Broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(jobReq, _root, _root, _root, null),
            jobCts.Token);

        var chatResp = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "run" });
        Assert.Equal(HttpStatusCode.Accepted, chatResp.StatusCode);
        await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Broker.GetPendingApprovalCount(sessionId) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, host.Broker.GetPendingApprovalCount(sessionId));

        // Cancel main turn ONLY -> background job approval must remain pending!
        var cancelResp = await client.PostAsync($"/api/gateway/session/{sessionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turnAuthTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(jobAuthTask.IsCompleted);
        Assert.Equal(1, host.Broker.GetPendingApprovalCount(sessionId));

        jobCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobAuthTask.WaitAsync(TimeSpan.FromSeconds(5)));
        sseCts.Cancel();
        try { await sseTask; } catch { }
    }

    [Fact]
    public async Task PlanEndpoint_AndRequestPlanApproval_EmitsPlanApprovalRequestedWithVersionAndHash()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=PlanTest", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        // Initially no plan -> 404
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/gateway/session/{sessionId}/plan")).StatusCode);

        // Save plan artifact and run RequestPlanApprovalTool wrapped by GatewayGuardedPlanTool
        var markdown = "# Plan\n\n## Execution Steps\n1. Do step one.\n";
        var artifact = new PlanArtifact
        {
            Id = "plan-1",
            SessionId = sessionId,
            Version = "3",
            Goal = "Implement feature X",
            Markdown = markdown,
            Status = PlanArtifactStatus.Draft
        };
        await host.PlanStore.SaveAsync(artifact);
        await host.PlanStore.SetCurrentAsync(sessionId, artifact.Id);

        await using var scope = host.Services.CreateAsyncScope();
        var sessionMgr = scope.ServiceProvider.GetRequiredService<CodexSessionManager>();
        var memSession = await sessionMgr.GetOrCreateSessionAsync(sessionId, LocalUser.Id, Path.Combine(_root, "ws"), (Uri?)null);
        memSession.CurrentPlanArtifactId = artifact.Id;
        await sessionMgr.UpdateSessionAsync(memSession);

        var innerTool = new RequestPlanApprovalTool(sessionMgr, host.PlanStore, new DefaultPlanApprovalService(host.PlanStore));
        GatewaySseEvent? emittedPlanEvent = null;
        var guarded = new GatewayGuardedPlanTool(
            innerTool,
            GatewayGuardedPlanToolRole.Request,
            new GatewayMessage { Source = GatewayMessageSource.UserChat, SessionId = sessionId, UserId = LocalUser.Id, Content = "create plan" },
            memSession,
            new GatewayApprovalGate(host.Services.GetRequiredService<IServiceScopeFactory>()),
            onApprovalRequested: async (result, _) =>
            {
                var meta = result.Metadata;
                emittedPlanEvent = new GatewaySseEvent
                {
                    Type = GatewaySseEventType.PlanApprovalRequested,
                    Payload = new
                    {
                        type = "plan_approval_requested",
                        planArtifactId = meta?.GetProperty("plan_artifact_id").GetString(),
                        version = meta?.GetProperty("plan_version").GetString(),
                        markdownHash = meta?.GetProperty("markdown_hash").GetString()
                    }
                };
                await ValueTask.CompletedTask;
            });

        var toolResult = await guarded.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = sessionId });
        Assert.Equal(ToolResultStatus.Success, toolResult.Status);
        Assert.NotNull(emittedPlanEvent);
        var eventJson = JsonSerializer.Serialize(emittedPlanEvent!.Payload);
        Assert.Contains("\"planArtifactId\":\"plan-1\"", eventJson, StringComparison.Ordinal);
        Assert.Contains("\"version\":\"3\"", eventJson, StringComparison.Ordinal);
        Assert.Contains(PlanArtifactSupport.ComputeMarkdownHash(markdown), eventJson, StringComparison.Ordinal);

        // GET /api/gateway/session/{id}/plan returns version, hash, status, markdown
        var planDto = await client.GetFromJsonAsync<SessionPlanDto>($"/api/gateway/session/{sessionId}/plan");
        Assert.NotNull(planDto);
        Assert.Equal("plan-1", planDto!.PlanArtifactId);
        Assert.Equal("3", planDto.Version);
        Assert.Equal(PlanArtifactSupport.ComputeMarkdownHash(markdown), planDto.MarkdownHash);
        Assert.Equal("ReadyForApproval", planDto.Status);
        Assert.Equal(markdown, planDto.Markdown);
    }

    [Fact]
    public async Task ToolEvents_SanitizeSecretsTruncateArgs_EmitToolStarted_AndStreamExecCodeOutput()
    {
        // 1. Sanitization & truncation
        var longPayload = new string('x', 3000);
        var rawArgs = new Dictionary<string, object?>
        {
            ["apiKey"] = "super-secret-key",
            ["token"] = "abc",
            ["command"] = "curl -H \"Authorization: Bearer my-secret-token-123456\" https://example.com?api_key=query_secret_999&x=1 && gh --token my_cli_secret_value && echo ghp_1234567890abcdef123456 AKIAIOSFODNN7EXAMPLE",
            ["largeField"] = longPayload
        };

        var emitted = new List<GatewaySseEvent>();
        var adapter = new GatewayRuntimeEventAdapter(evt =>
        {
            emitted.Add(evt);
            return ValueTask.CompletedTask;
        }, "msg-1");

        await adapter.OnEventAsync(new ToolCallRequestedEvent(
            1, Guid.NewGuid(), "s1", QueryLoopEntryPoint.GatewayMessageProcessor, 1, "exec_cmd", "call-1", rawArgs));
        await adapter.OnEventAsync(new ToolExecutionStartedEvent(
            2, Guid.NewGuid(), "s1", QueryLoopEntryPoint.GatewayMessageProcessor, 1, "exec_cmd", "call-1"));

        Assert.Equal(2, emitted.Count);
        Assert.Equal(GatewaySseEventType.ToolCall, emitted[0].Type);
        var callJson = JsonSerializer.Serialize(emitted[0].Payload);
        Assert.DoesNotContain("super-secret-key", callJson, StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret-token-123456", callJson, StringComparison.Ordinal);
        Assert.DoesNotContain("query_secret_999", callJson, StringComparison.Ordinal);
        Assert.DoesNotContain("my_cli_secret_value", callJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef123456", callJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", callJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", callJson, StringComparison.Ordinal);
        Assert.Contains("\"argumentsTruncated\":true", callJson, StringComparison.Ordinal);

        Assert.Equal(GatewaySseEventType.ToolStarted, emitted[1].Type);
        var startedJson = JsonSerializer.Serialize(emitted[1].Payload);
        Assert.Contains("\"callId\":\"call-1\"", startedJson, StringComparison.Ordinal);

        // 2. ExecCodeTool streams tool_output through ToolOutputEventThrottler via ICodexGateway
        var recorder = new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: true);
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(s => s.GetService(typeof(ICodexGateway))).Returns(recorder);

        var sessionMock = new Mock<IIsolatedExecutionSession>();
        sessionMock.SetupGet(s => s.Layout).Returns(new ExecutionLayout(ExecutionBackend.Local, _root, _root, _root, _root, new Dictionary<string, string>()));
        sessionMock.Setup(s => s.RunAsync(It.IsAny<IsolatedCommandRequest>(), It.IsAny<CancellationToken>()))
            .Returns<IsolatedCommandRequest, CancellationToken>((req, _) =>
            {
                req.OnOutput?.Invoke(new CommandOutputLine("stdout", "hello from exec_code"));
                return Task.FromResult(new IsolatedCommandResult(ExecutionDecision.Allow(), IsolatedCommandStatus.Completed, 0, "hello from exec_code", "", false, TimeSpan.FromMilliseconds(5), "ev"));
            });

        var backendMock = new Mock<IIsolatedExecutionService>();
        backendMock.Setup(b => b.SupportsNetworkMode(ExecutionNetworkMode.Host)).Returns(true);
        backendMock.Setup(b => b.OpenPinnedBackendSessionAsync(ExecutionBackend.Local, It.IsAny<TrustedExecutionContext>(), It.IsAny<IsolatedSessionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IsolatedSessionOpenResult(ExecutionDecision.Allow(), sessionMock.Object));

        var accessor = new TrustedExecutionContextAccessor();
        var optionsMonitor = new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(
            new ExecutionOptions { Backend = ExecutionBackend.Local, PermissionMode = ExecutionPermissionMode.FullAuto });
        var broker = new ExecutionApprovalBroker(optionsMonitor, new ExecutionPermissionStore(Path.Combine(_root, "p.json")), NullLogger<ExecutionApprovalBroker>.Instance);
        var gate = new ToolIsolationGate(backendMock.Object, accessor, new IsolationOptions { StagingRoot = _root }, broker, optionsMonitor, spMock.Object);

        using var scope = accessor.Begin(new TrustedExecutionContext
        {
            UserId = "local",
            SessionId = "s-exec-stream",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = _root,
            Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
        });

        var execTool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);
        var res = await execTool.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print('hello')", ["language"] = "python" });
        Assert.Equal(ToolResultStatus.Success, res.Status);
        Assert.Contains(recorder.Events, e => e.Event.Type == GatewaySseEventType.ToolOutput && JsonSerializer.Serialize(e.Event.Payload).Contains("hello from exec_code", StringComparison.Ordinal));
    }

    private sealed class DelegateGatewayMessageProcessor(
        Func<GatewayMessage, Func<GatewaySseEvent, ValueTask>, CancellationToken, Task> handler) : IGatewayMessageProcessor
    {
        public Task ProcessAsync(GatewayMessage message, Func<GatewaySseEvent, ValueTask> emit, CancellationToken ct)
            => handler(message, emit, ct);
    }
}
