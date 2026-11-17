// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Gateway;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class GatewayDailyToolsSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-gw-daily-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task MainSession_ExposesDailySearchAndMemoryToolsByDefault_AndActivatesDeferredDailyToolsViaToolSearch()
    {
        var capturedToolNamesPerRound = new ConcurrentQueue<IReadOnlyList<string>>();
        var capturedToolOutputs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var round = 0;

        var fakeExecutor = new FakeStreamingLlmExecutor((request, _) =>
        {
            var currentRound = Interlocked.Increment(ref round);
            var toolNames = (request.Options?.Tools ?? [])
                .OfType<AIFunction>()
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            capturedToolNamesPerRound.Enqueue(toolNames);

            foreach (var msg in request.Messages)
            {
                if (msg.Role == ChatRole.Tool)
                {
                    foreach (var result in msg.Contents.OfType<FunctionResultContent>())
                    {
                        capturedToolOutputs[result.CallId ?? Guid.NewGuid().ToString("N")] = result.Result?.ToString() ?? string.Empty;
                    }
                }
            }

            if (currentRound == 1)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [
                        new FunctionCallContent("call-search-1", "tool_search", new Dictionary<string, object?> { ["query"] = "summary" })
                    ])
                ];
            }

            if (currentRound == 2)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [
                        new FunctionCallContent("call-cron-list", "cron_list", new Dictionary<string, object?>()),
                        new FunctionCallContent("call-save-summary", "save_project_summary", new Dictionary<string, object?> { ["summary"] = "Daily test summary" }),
                        new FunctionCallContent("call-learn-pref", "user_learn_preference", new Dictionary<string, object?> { ["key"] = "currency", ["value"] = "CNY" })
                    ])
                ];
            }

            return [new ChatResponseUpdate(ChatRole.Assistant, "已完成查询与定时检查。")];
        });

        await using var host = await StartGatewayHostAsync(fakeExecutor, planPermissionModeEnabled: false);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=DailyTools", null);
        sessionResp.EnsureSuccessStatusCode();
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        using var scope = host.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IGatewayMessageProcessor>();
        var sessionManager = scope.ServiceProvider.GetRequiredService<CodexSessionManager>();
        var ws = Path.Combine(_root, "ws-daily");
        Directory.CreateDirectory(ws);
        await sessionManager.GetOrCreateSessionAsync(sessionId, LocalUser.Id, ws, (Uri?)null);

        var events = new List<GatewaySseEvent>();
        await processor.ProcessAsync(
            new GatewayMessage
            {
                Source = GatewayMessageSource.UserChat,
                SessionId = sessionId,
                UserId = LocalUser.Id,
                Content = "查询一下今天的黄金价格，并看看定时任务"
            },
            evt =>
            {
                events.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var rounds = capturedToolNamesPerRound.ToArray();
        Assert.True(rounds.Length >= 3, $"Expected at least 3 rounds, got {rounds.Length}");

        // Round 1: daily high-frequency tools (including AlwaysOn daily tools like cron_list) are available immediately;
        // deferred tools like save_project_summary await tool_search activation.
        var round1Tools = rounds[0];
        Assert.Contains("web_search", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("fetch_webpage", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("glob", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("user_learn_preference", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cron_list", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tool_search", round1Tools, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("save_project_summary", round1Tools, StringComparer.OrdinalIgnoreCase);

        // Round 2: after tool_search("summary"), save_project_summary is available_now=true and present on the tool surface
        var searchEnvelope = Assert.Single(capturedToolOutputs.Values, o => o.Contains("save_project_summary", StringComparison.OrdinalIgnoreCase));
        using var envelopeJson = JsonDocument.Parse(searchEnvelope);
        var innerOutput = envelopeJson.RootElement.GetProperty("output").GetString()!;
        Assert.Contains("\"available_now\":true", innerOutput, StringComparison.OrdinalIgnoreCase);

        var round2Tools = rounds[1];
        Assert.Contains("save_project_summary", round2Tools, StringComparer.OrdinalIgnoreCase);

        // Round 3: cron_list, save_project_summary, and user_learn_preference executed cleanly
        Assert.Contains(capturedToolOutputs.Values, o => o.Contains("cron schedules", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedToolOutputs.Values, o => o.Contains("PROJECT_SUMMARY.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedToolOutputs.Values, o => o.Contains("currency", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, e => e.Type == GatewaySseEventType.Content);
    }

    [Fact]
    public async Task MainSession_WhenPlanModeActive_KeepsReadOnlyDailyToolsAndBlocksWriteTools()
    {
        IReadOnlyList<string>? capturedRound1Tools = null;

        var fakeExecutor = new FakeStreamingLlmExecutor((request, _) =>
        {
            capturedRound1Tools ??= (request.Options?.Tools ?? [])
                .OfType<AIFunction>()
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return [new ChatResponseUpdate(ChatRole.Assistant, "已处于只读规划模式。")];
        });

        await using var host = await StartGatewayHostAsync(fakeExecutor, planPermissionModeEnabled: true);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=PlanModeTools", null);
        sessionResp.EnsureSuccessStatusCode();
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        using var scope = host.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IGatewayMessageProcessor>();
        var sessionManager = scope.ServiceProvider.GetRequiredService<CodexSessionManager>();
        var ws = Path.Combine(_root, "ws-planmode");
        Directory.CreateDirectory(ws);
        var session = await sessionManager.GetOrCreateSessionAsync(sessionId, LocalUser.Id, ws, (Uri?)null);
        session.Metadata["PlanModeActive"] = "true";
        await sessionManager.UpdateSessionAsync(session);

        await processor.ProcessAsync(
            new GatewayMessage
            {
                Source = GatewayMessageSource.UserChat,
                SessionId = sessionId,
                UserId = LocalUser.Id,
                Content = "帮我查一下相关资料"
            },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.NotNull(capturedRound1Tools);
        Assert.Contains("web_search", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("fetch_webpage", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("glob", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("user_learn_preference", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("write_file", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete_file", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("apply_patch", capturedRound1Tools!, StringComparer.OrdinalIgnoreCase);
    }

    private Task<P3TestHost> StartGatewayHostAsync(ILLMExecutor llmExecutor, bool planPermissionModeEnabled)
    {
        var globalSettings = Path.Combine(_root, "global-" + Guid.NewGuid().ToString("N"), "appsettings.json");
        return P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, configuration) =>
            {
                services.AddHttpClient();
                services.AddSingleton(Mock.Of<IChatClient>());
                services.AddCodexFlowRuntime(configuration);
                services.AddScoped<ILLMExecutor>(_ => llmExecutor);
                services.Configure<PlanningOptions>(opts => opts.PlanPermissionModeEnabled = planPermissionModeEnabled);
            });
    }

    private sealed class FakeStreamingLlmExecutor(
        Func<LLMExecutionRequest, CancellationToken, IReadOnlyList<ChatResponseUpdate>> responder) : ILLMExecutor
    {
        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var update in responder(request, ct))
            {
                await Task.Yield();
                yield return update;
            }
        }
    }
}
