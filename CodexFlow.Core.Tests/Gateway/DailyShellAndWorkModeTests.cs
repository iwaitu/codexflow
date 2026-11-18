// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Gateway;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class DailyShellAndWorkModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-daily-wm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task CreateSession_DailyAssistant_AndWorkModeImmutability_ShouldFollowContract()
    {
        var llm = new FakeStreamingLlmExecutor((_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        await using var host = await StartHostAsync(llm);
        using var normalClient = host.CreateNormalClient();
        using var controlClient = host.CreateControlClient();

        // 1. Default POST /api/gateway/session creates Development (devsim compat)
        var devResp = await normalClient.PostAsync("/api/gateway/session?name=DevProj", null);
        devResp.EnsureSuccessStatusCode();
        var devJson = await devResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Development", devJson!["workMode"]);
        var devSessionId = devJson["sessionId"];

        // 2. ?workMode=daily creates Daily
        var dailyResp = await normalClient.PostAsync("/api/gateway/session?name=DailyNotes&workMode=daily", null);
        dailyResp.EnsureSuccessStatusCode();
        var dailyJson = await dailyResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Daily", dailyJson!["workMode"]);
        var dailySessionId = dailyJson["sessionId"];

        // 3. Invalid workMode -> 400
        var invalidResp = await normalClient.PostAsync("/api/gateway/session?name=Bad&workMode=unknown", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);

        // 4. PATCH mode/workMode rejected with 400 mode_immutable
        var patchModeResp = await normalClient.PatchAsJsonAsync($"/api/gateway/session/{dailySessionId}", new { workMode = "Development" });
        Assert.Equal(HttpStatusCode.BadRequest, patchModeResp.StatusCode);
        var patchBody = await patchModeResp.Content.ReadAsStringAsync();
        Assert.Contains("mode_immutable", patchBody, StringComparison.Ordinal);

        // 5. Daily Assistant shortcut creates & reuses protected "日常助手" in Daily mode
        var assistant1 = await normalClient.PostAsync("/api/gateway/session/daily-assistant", null);
        assistant1.EnsureSuccessStatusCode();
        var a1 = await assistant1.Content.ReadFromJsonAsync<SessionBootstrapResponse>();
        Assert.Equal("日常助手", a1!.WorkspaceName);
        Assert.Equal("Daily", a1.WorkMode);
        Assert.True(a1.Created);

        var assistant2 = await normalClient.PostAsync("/api/gateway/session/daily-assistant", null);
        assistant2.EnsureSuccessStatusCode();
        var a2 = await assistant2.Content.ReadFromJsonAsync<SessionBootstrapResponse>();
        Assert.Equal(a1.SessionId, a2!.SessionId);
        Assert.False(a2.Created);

        // Reserved "日常助手" cannot be deleted or renamed
        var delAssistant = await normalClient.DeleteAsync($"/api/gateway/session/{a1.SessionId}");
        Assert.Equal(HttpStatusCode.BadRequest, delAssistant.StatusCode);

        // 6. OpenLocalWorkspace against existing Daily workspace path -> blocked (400 managed path denied / 409 conflict)
        var dailyWorkspacePath = a1.WorkspacePath;
        var openDailyResp = await controlClient.PostAsJsonAsync("/api/gateway/workspaces", new OpenLocalWorkspaceRequest(dailyWorkspacePath));
        Assert.True(
            openDailyResp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"Expected 400 or 409, got {openDailyResp.StatusCode}: {await openDailyResp.Content.ReadAsStringAsync()}");

        // Development session still visible as Development in snapshot
        var devSnap = await normalClient.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{devSessionId}/snapshot");
        Assert.Equal("Development", devSnap!.Session.WorkMode);
    }

    [Fact]
    public async Task PlanModeEndpoint_ShouldRequireControlPlaneToExit_AndEnforceCasVersion()
    {
        var llm = new FakeStreamingLlmExecutor((_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "ok")]);
        await using var host = await StartHostAsync(llm);
        using var normalClient = host.CreateNormalClient();
        using var controlClient = host.CreateControlClient();

        var created = await (await normalClient.PostAsync("/api/gateway/session?name=PlanCas&workMode=daily", null))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var sessionId = created!["sessionId"];

        // Normal client can enter Plan Mode
        var enterResp = await normalClient.PostAsJsonAsync($"/api/gateway/session/{sessionId}/plan-mode", new UpdatePlanModeRequest(true, Reason: "inspect first"));
        enterResp.EnsureSuccessStatusCode();
        var entered = await enterResp.Content.ReadFromJsonAsync<PlanModeResponse>();
        Assert.True(entered!.PlanModeActive);
        Assert.Equal(2, entered.PlanModeVersion);

        // Normal client attempting to exit Plan Mode gets 403 Forbidden
        var normalExitResp = await normalClient.PostAsJsonAsync(
            $"/api/gateway/session/{sessionId}/plan-mode",
            new UpdatePlanModeRequest(false, ExpectedPlanModeVersion: entered.PlanModeVersion));
        Assert.Equal(HttpStatusCode.Forbidden, normalExitResp.StatusCode);

        // Control plane with stale version gets 409 Conflict
        var staleExitResp = await controlClient.PostAsJsonAsync(
            $"/api/gateway/session/{sessionId}/plan-mode",
            new UpdatePlanModeRequest(false, ExpectedPlanModeVersion: 1));
        Assert.Equal(HttpStatusCode.Conflict, staleExitResp.StatusCode);

        // Control plane with matching CAS version exits cleanly
        var controlExitResp = await controlClient.PostAsJsonAsync(
            $"/api/gateway/session/{sessionId}/plan-mode",
            new UpdatePlanModeRequest(false, ExpectedPlanModeVersion: entered.PlanModeVersion, Reason: "confirmed"));
        controlExitResp.EnsureSuccessStatusCode();
        var exited = await controlExitResp.Content.ReadFromJsonAsync<PlanModeResponse>();
        Assert.False(exited!.PlanModeActive);
        Assert.Equal(3, exited.PlanModeVersion);
    }

    [Fact]
    public async Task DailySession_ToolSurface_ShouldExposeDailyToolsAndRunShell_AndDropThemAfterEnterPlanMode()
    {
        var roundsTools = new ConcurrentQueue<IReadOnlyList<string>>();
        var enableSkillsFlags = new ConcurrentQueue<bool>();
        var round = 0;

        var llm = new FakeStreamingLlmExecutor((request, _) =>
        {
            var r = Interlocked.Increment(ref round);
            var names = (request.Options?.Tools ?? []).OfType<AIFunction>().Select(t => t.Name).ToArray();
            roundsTools.Enqueue(names);
            if (request.Options is VllmChatOptions vopts)
            {
                enableSkillsFlags.Enqueue(vopts.EnableSkills);
            }

            if (r == 1)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [
                        new FunctionCallContent("c-enter-plan", "enter_plan_mode", new Dictionary<string, object?> { ["reason"] = "switch to read only" })
                    ])
                ];
            }

            return [new ChatResponseUpdate(ChatRole.Assistant, "已进入只读规划模式。")];
        });

        await using var host = await StartHostAsync(llm);
        using var normalClient = host.CreateNormalClient();

        var dailyCreated = await (await normalClient.PostAsync("/api/gateway/session?name=DailySurface&workMode=daily", null))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var dailySessionId = dailyCreated!["sessionId"];

        var devCreated = await (await normalClient.PostAsync("/api/gateway/session?name=DevSurface", null))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var devSessionId = devCreated!["sessionId"];

        using var scope = host.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IGatewayMessageProcessor>();

        await processor.ProcessAsync(
            new GatewayMessage
            {
                Source = GatewayMessageSource.UserChat,
                SessionId = dailySessionId,
                UserId = LocalUser.Id,
                Content = "先看看工具面，再进入计划模式"
            },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        var captured = roundsTools.ToArray();
        Assert.True(captured.Length >= 2);
        var round1 = captured[0];
        var round2 = captured[1];

        // Round 1 (Daily, !PlanModeActive): run_shell + list_authorized_directory present; NO dev tools
        Assert.Contains("run_shell", round1, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("list_authorized_directory", round1, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("enter_plan_mode", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("start_next_task", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("spawn_worker", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_session_plan", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("apply_patch", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("exec_cmd", round1, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ui_show_diff", round1, StringComparer.OrdinalIgnoreCase);

        // EnableSkills must be false in Daily mode
        Assert.All(enableSkillsFlags, flag => Assert.False(flag));

        // Round 2 (immediately after enter_plan_mode): run_shell MUST be dropped monotonically
        Assert.DoesNotContain("run_shell", round2, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("list_authorized_directory", round2, StringComparer.OrdinalIgnoreCase);

        // Development mode NEVER exposes run_shell
        roundsTools.Clear();
        Interlocked.Exchange(ref round, 0);
        await processor.ProcessAsync(
            new GatewayMessage
            {
                Source = GatewayMessageSource.UserChat,
                SessionId = devSessionId,
                UserId = LocalUser.Id,
                Content = "看看开发模式工具"
            },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        var devRound1 = roundsTools.ToArray()[0];
        Assert.DoesNotContain("run_shell", devRound1, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DailySession_RunShell_EndToEndApprovalAndScriptEndpoint_ShouldVerifyControlPlaneAndRun()
    {
        var round = 0;
        var toolOutputs = new ConcurrentQueue<string>();

        var llm = new FakeStreamingLlmExecutor((request, _) =>
        {
            foreach (var msg in request.Messages.Where(m => m.Role == ChatRole.Tool))
            {
                foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                {
                    toolOutputs.Enqueue(fr.Result?.ToString() ?? string.Empty);
                }
            }

            if (Interlocked.Increment(ref round) == 1)
            {
                var shellChoice = OperatingSystem.IsWindows() ? "powershell" : "sh";
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [
                        new FunctionCallContent("c-shell-1", "run_shell", new Dictionary<string, object?>
                        {
                            ["script"] = "echo daily_shell_ok",
                            ["shell"] = shellChoice
                        })
                    ])
                ];
            }

            return [new ChatResponseUpdate(ChatRole.Assistant, "脚本已执行完成。")];
        });

        await using var host = await StartHostAsync(llm);
        using var normalClient = host.CreateNormalClient();
        using var controlClient = host.CreateControlClient();

        var created = await (await normalClient.PostAsync("/api/gateway/session?name=DailyShellE2E&workMode=daily", null))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var sessionId = created!["sessionId"];

        using var scope = host.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IGatewayMessageProcessor>();
        var broker = scope.ServiceProvider.GetRequiredService<IExecutionApprovalBroker>();
        var events = new ConcurrentQueue<GatewaySseEvent>();

        var runTask = processor.ProcessAsync(
            new GatewayMessage
            {
                Source = GatewayMessageSource.UserChat,
                SessionId = sessionId,
                UserId = LocalUser.Id,
                Content = "运行一下测试脚本"
            },
            evt =>
            {
                events.Enqueue(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        PendingApprovalItem? pending = null;
        for (var i = 0; i < 80 && pending is null; i++)
        {
            pending = broker.GetPendingApprovals(sessionId).FirstOrDefault();
            if (pending is not null) break;
            if (runTask.IsCompleted)
            {
                await runTask;
                break;
            }
            await Task.Delay(50);
        }

        Assert.True(pending is not null, $"No pending approval captured. ToolOutputs=[{string.Join(" | ", toolOutputs)}]");
        Assert.Equal("run_shell", pending!.Tool);
        Assert.False(string.IsNullOrEmpty(pending.ScriptSha256));
        Assert.Equal(["deny", "allow_once"], pending.AllowedDecisions);

        // Normal client cannot read full script snapshot from control-plane endpoint
        var normalScriptResp = await normalClient.GetAsync($"/api/gateway/approvals/{pending.ApprovalId}/script");
        Assert.Equal(HttpStatusCode.Forbidden, normalScriptResp.StatusCode);

        // Control plane receives full script snapshot with matching SHA-256
        var controlScriptResp = await controlClient.GetAsync($"/api/gateway/approvals/{pending.ApprovalId}/script");
        controlScriptResp.EnsureSuccessStatusCode();
        var snap = await controlScriptResp.Content.ReadFromJsonAsync<PendingApprovalScriptSnapshot>();
        Assert.Equal("echo daily_shell_ok", snap!.ScriptText);
        Assert.Equal(pending.ScriptSha256, snap.ScriptSha256);

        // AllowAlways is refused (400 decision_not_allowed)
        var denyAlwaysResp = await controlClient.PostAsJsonAsync(
            $"/api/gateway/approvals/{pending.ApprovalId}",
            new SubmitExecutionApprovalRequest(pending.RequestDigest, "allow_always"));
        Assert.Equal(HttpStatusCode.BadRequest, denyAlwaysResp.StatusCode);

        // AllowOnce approves and executes inside Local workspace
        var approveOnceResp = await controlClient.PostAsJsonAsync(
            $"/api/gateway/approvals/{pending.ApprovalId}",
            new SubmitExecutionApprovalRequest(pending.RequestDigest, "allow_once"));
        approveOnceResp.EnsureSuccessStatusCode();

        await runTask;

        Assert.Contains(toolOutputs, output => output.Contains("daily_shell_ok", StringComparison.Ordinal));
    }

    private Task<P3TestHost> StartHostAsync(ILLMExecutor llmExecutor)
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
