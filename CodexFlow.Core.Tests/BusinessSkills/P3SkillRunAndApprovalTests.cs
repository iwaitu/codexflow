// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Controllers;
using CodexFlow.Gateway.Http;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Services;
using CodexFlow.Domain.BusinessSkills;
using CodexFlow.Local;
using CodexFlow.Orchestration.BusinessSkills;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P3SkillRunAndApprovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p3-runs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task EndToEnd_OpenApi_ReceivableWorkflow_ReadOnlyTestRun_AllowOnceApproval_Publish_AndIdempotentRun()
    {
        var createdTasks = new ConcurrentBag<(string CustomerId, string InvoiceId, decimal Amount, string? IdemKey)>();
        var erpBuilder = WebApplication.CreateSlimBuilder();
        erpBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        erpBuilder.Logging.ClearProviders();

        var erpApp = erpBuilder.Build();
        erpApp.MapGet("/openapi.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "P3 ERP API", "version": "1.0.0" },
              "components": {
                "securitySchemes": {
                  "BearerAuth": { "type": "http", "scheme": "bearer" }
                }
              },
              "security": [{ "BearerAuth": [] }],
              "paths": {
                "/api/invoices": {
                  "get": {
                    "operationId": "listInvoices",
                    "summary": "分页查询应收账款",
                    "parameters": [
                      { "name": "page", "in": "query", "schema": { "type": "integer" } }
                    ],
                    "responses": { "200": { "description": "OK" } }
                  }
                },
                "/api/collection-tasks": {
                  "post": {
                    "operationId": "createCollectionTask",
                    "summary": "创建催收任务",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "customerId": { "type": "string" },
                              "invoiceId": { "type": "string" },
                              "unpaidAmount": { "type": "number" },
                              "currency": { "type": "string" },
                              "advice": { "type": "string" }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "201": { "description": "Created" } }
                  }
                }
              }
            }
            """, "application/json"));

        erpApp.MapGet("/api/invoices", (HttpContext ctx, int page) =>
        {
            if (ctx.Request.Headers.Authorization.ToString() != "Bearer p3-secret-token")
            {
                return Results.Unauthorized();
            }

            return page switch
            {
                1 => Results.Json(new
                {
                    items = new[]
                    {
                        new { invoiceId = "INV-01", customerId = "C-1", unpaidAmount = 70_000.00m, currency = "CNY", overdueDays = 38 },
                        new { invoiceId = "INV-03", customerId = "C-3", unpaidAmount = 90_000.00m, currency = "CNY", overdueDays = 30 }
                    },
                    hasMore = true
                }),
                _ => Results.Json(new
                {
                    items = new[]
                    {
                        new { invoiceId = "INV-02", customerId = "C-2", unpaidAmount = 50_000.01m, currency = "CNY", overdueDays = 31 },
                        new { invoiceId = "INV-04", customerId = "C-4", unpaidAmount = 50_000.00m, currency = "CNY", overdueDays = 40 },
                        new { invoiceId = "INV-05", customerId = "C-5", unpaidAmount = 100_000.00m, currency = "USD", overdueDays = 50 }
                    },
                    hasMore = false
                })
            };
        });

        erpApp.MapPost("/api/collection-tasks", async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers.Authorization.ToString() != "Bearer p3-secret-token")
            {
                return Results.Unauthorized();
            }

            using var bodyDoc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = bodyDoc.RootElement;
            var custId = root.GetProperty("customerId").GetString()!;
            var invId = root.GetProperty("invoiceId").GetString()!;
            var amt = root.GetProperty("unpaidAmount").GetDecimal();
            var idem = root.TryGetProperty("idempotencyKey", out var ik) ? ik.GetString() : null;

            createdTasks.Add((custId, invId, amt, idem));
            return Results.Created($"/api/collection-tasks/{invId}", new
            {
                code = "CREATED",
                data = new { taskId = $"TASK-{invId}" }
            });
        });

        await erpApp.StartAsync();

        try
        {
            var erpUri = new Uri(erpApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var dbPath = Path.Combine(_root, "data", "state.db");
            await using var host = await StartP3TestHostAsync(_root, dbPath);

            using var control = host.CreateControlClient();
            using var normal = host.CreateNormalClient();

            // 1. 创建插件 + 配置 Bearer 凭据 + 发现目录 + 审核 Read/Write 风险
            var createPluginResp = await control.PostAsJsonAsync("/api/plugins", new CreateBusinessPluginRequest(
                "ERP Receivable",
                BusinessPluginSourceType.OpenApi,
                new Uri(erpUri, "/openapi.json").ToString(),
                "staging",
                new EndpointPolicyDto { AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(erpUri), AllowPlainHttpForIntranet = true }));
            var plugin = await createPluginResp.Content.ReadFromJsonAsync<BusinessPluginDto>();
            Assert.NotNull(plugin);

            var credentialResponse = await control.PutAsJsonAsync($"/api/plugins/{plugin!.Id}/credentials/execution", new SetPluginCredentialRequest(
                BusinessAuthKind.Bearer,
                """{"token":"p3-secret-token"}"""));
            credentialResponse.EnsureSuccessStatusCode();

            var discResp = await control.PostAsJsonAsync($"/api/plugins/{plugin.Id}/discover", new DiscoverPluginRequest("p3-disc-1"));
            discResp.EnsureSuccessStatusCode();
            var disc = await discResp.Content.ReadFromJsonAsync<DiscoverPluginResultDto>();
            Assert.NotNull(disc?.Snapshot);
            var listInvId = $"{plugin.Id}:GET:/api/invoices";
            var createTaskId = $"{plugin.Id}:POST:/api/collection-tasks";

            await control.PutAsJsonAsync($"/api/plugins/{plugin.Id}/capabilities/risk",
                new UpdateCapabilityRiskRequest(listInvId, CapabilityRiskLevel.Read));
            await control.PutAsJsonAsync($"/api/plugins/{plugin.Id}/capabilities/risk",
                new UpdateCapabilityRiskRequest(createTaskId, CapabilityRiskLevel.Write));

            // 2. 创建草稿 + 冻结选择 + 写入受控 SkillDefinitionV1 + 校验
            var draftResp = await normal.PostAsJsonAsync("/api/skill-drafts", new CreateSkillDraftRequest(
                plugin.Id, disc!.Snapshot!.Id, "P3 逾期催收 Skill", "逾期>30天且未回款>5万元生成催收任务"));
            Assert.Equal(HttpStatusCode.Created, draftResp.StatusCode);
            var draft = await draftResp.Content.ReadFromJsonAsync<SkillDraftDto>();

            var selResp = await control.PutAsJsonAsync($"/api/skill-drafts/{draft!.Id}/selection",
                new PutDraftSelectionRequest(draft.Revision, disc.Snapshot.Id, [listInvId, createTaskId]));
            draft = await selResp.Content.ReadFromJsonAsync<SkillDraftDto>();

            // 验证 confirmModelDataScope=false 被拒绝
            var scopeDenyResp = await control.PostAsJsonAsync($"/api/skill-drafts/{draft!.Id}/generate",
                new GenerateSkillCandidateRequest("gen-req-1", ConfirmModelDataScope: false));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, scopeDenyResp.StatusCode);

            var skillDef = BuildOpenApiReceivableSkill(plugin.Id, disc.Snapshot.Id, listInvId, createTaskId);
            var putDefResp = await normal.PutAsJsonAsync($"/api/skill-drafts/{draft.Id}",
                new UpdateSkillDraftRequest(draft.Revision, CandidateDefinition: skillDef));
            draft = await putDefResp.Content.ReadFromJsonAsync<SkillDraftDto>();

            var valResp = await normal.PostAsync($"/api/skill-drafts/{draft!.Id}/validate", null);
            Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);
            draft = await valResp.Content.ReadFromJsonAsync<SkillDraftDto>();
            Assert.Empty(draft!.ValidationErrors);
            Assert.False(draft.NeedsRevalidation);

            // 3. 未通过试跑前禁止发布
            var prematurePublish = await control.PostAsJsonAsync($"/api/skill-drafts/{draft.Id}/publish",
                new PublishSkillDraftRequest(draft.Revision));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, prematurePublish.StatusCode);

            // 4. 默认只读试跑必须阻断含 Write 步骤的 Skill
            var blockedRoTest = await control.PostAsJsonAsync($"/api/skill-drafts/{draft.Id}/test-runs",
                new StartSkillRunRequest(null, "test-ro-blocked", new Dictionary<string, JsonElement>
                {
                    ["currency"] = JsonSerializer.SerializeToElement("CNY"),
                    ["asOfDate"] = JsonSerializer.SerializeToElement("2026-11-17")
                }, IsTestRun: true, AllowWriteInTestRun: false));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedRoTest.StatusCode);

            // 5. 显式写入试跑 -> 触发 WaitingForApproval -> 校验审批限制与 allow_once 批准完成
            var testRunResp = await control.PostAsJsonAsync($"/api/skill-drafts/{draft.Id}/test-runs",
                new StartSkillRunRequest(null, "test-write-ok", new Dictionary<string, JsonElement>
                {
                    ["currency"] = JsonSerializer.SerializeToElement("CNY"),
                    ["asOfDate"] = JsonSerializer.SerializeToElement("2026-11-17")
                }, IsTestRun: true, AllowWriteInTestRun: true));
            Assert.Equal(HttpStatusCode.Accepted, testRunResp.StatusCode);
            var testRun = await testRunResp.Content.ReadFromJsonAsync<SkillRunDto>();

            var waitingRun = await WaitForRunStatusAsync(normal, testRun!.RunId, SkillRunStatus.WaitingForApproval);
            Assert.NotNull(waitingRun.PendingApproval);
            Assert.Equal(BusinessApprovalConstants.BusinessSkillWriteKind, waitingRun.PendingApproval!.Kind);
            Assert.Equal(2, waitingRun.PendingApproval.Summary.BatchCount);
            Assert.Equal(120_000.01m, waitingRun.PendingApproval.Summary.KeyAmount);
            Assert.Equal("CNY", waitingRun.PendingApproval.Summary.Currency);
            Assert.True(waitingRun.PendingApproval.Summary.WriteStepUnverified);
            Assert.DoesNotContain("p3-secret-token", JsonSerializer.Serialize(waitingRun.PendingApproval), StringComparison.Ordinal);

            // 审批策略校验：普通令牌 403；allow_session 400；篡改 digest 409
            var normalApprovalAttempt = await normal.PostAsJsonAsync($"/api/gateway/approvals/{waitingRun.PendingApproval.ApprovalId}",
                new SubmitExecutionApprovalRequest(waitingRun.PendingApproval.RequestDigest, "allow_once"));
            Assert.Equal(HttpStatusCode.Forbidden, normalApprovalAttempt.StatusCode);

            var sessionDecisionAttempt = await control.PostAsJsonAsync($"/api/gateway/approvals/{waitingRun.PendingApproval.ApprovalId}",
                new SubmitExecutionApprovalRequest(waitingRun.PendingApproval.RequestDigest, "allow_session"));
            Assert.Equal(HttpStatusCode.BadRequest, sessionDecisionAttempt.StatusCode);

            var badDigestAttempt = await control.PostAsJsonAsync($"/api/gateway/approvals/{waitingRun.PendingApproval.ApprovalId}",
                new SubmitExecutionApprovalRequest("tampered-digest", "allow_once"));
            Assert.Equal(HttpStatusCode.Conflict, badDigestAttempt.StatusCode);

            // 使用正确摘要 allow_once 批准执行
            var approveOk = await control.PostAsJsonAsync($"/api/gateway/approvals/{waitingRun.PendingApproval.ApprovalId}",
                new SubmitExecutionApprovalRequest(waitingRun.PendingApproval.RequestDigest, "allow_once"));
            Assert.Equal(HttpStatusCode.OK, approveOk.StatusCode);

            var testDone = await WaitForRunStatusAsync(normal, testRun.RunId, SkillRunStatus.Succeeded);
            Assert.Equal(2, createdTasks.Count);
            Assert.Contains(createdTasks, t => t.InvoiceId == "INV-01" && t.Amount == 70_000.00m && t.IdemKey == "INV-01:2026-11-17");
            Assert.Contains(createdTasks, t => t.InvoiceId == "INV-02" && t.Amount == 50_000.01m);

            // 6. 事务发布为不可变 v1 + Markdown 只读预览 + 正式运行幂等检查
            var refreshedDraft = await normal.GetFromJsonAsync<SkillDraftDto>($"/api/skill-drafts/{draft.Id}");
            var pubResp = await control.PostAsJsonAsync($"/api/skill-drafts/{draft.Id}/publish",
                new PublishSkillDraftRequest(refreshedDraft!.Revision));
            Assert.Equal(HttpStatusCode.Created, pubResp.StatusCode);
            var published = await pubResp.Content.ReadFromJsonAsync<PublishedSkillDto>();
            Assert.Equal(1, published!.ActiveVersion);
            Assert.True(published.Versions[0].WriteStepVerified);

            var mdDto = await normal.GetFromJsonAsync<SkillMarkdownPreviewDto>($"/api/skills/{published.Id}/markdown");
            Assert.Contains("P3 逾期催收 Skill", mdDto!.Markdown, StringComparison.Ordinal);

            var formalStart = await control.PostAsJsonAsync($"/api/skills/{published.Id}/runs",
                new StartSkillRunRequest(null, "formal-run-1", new Dictionary<string, JsonElement>
                {
                    ["currency"] = JsonSerializer.SerializeToElement("CNY"),
                    ["asOfDate"] = JsonSerializer.SerializeToElement("2026-11-17")
                }));
            Assert.Equal(HttpStatusCode.Accepted, formalStart.StatusCode);
            var formalRun = await formalStart.Content.ReadFromJsonAsync<SkillRunDto>();

            var duplicateStart = await control.PostAsJsonAsync($"/api/skills/{published.Id}/runs",
                new StartSkillRunRequest(null, "formal-run-1", new Dictionary<string, JsonElement>
                {
                    ["currency"] = JsonSerializer.SerializeToElement("CNY"),
                    ["asOfDate"] = JsonSerializer.SerializeToElement("2026-11-17")
                }));
            var duplicateRun = await duplicateStart.Content.ReadFromJsonAsync<SkillRunDto>();
            Assert.Equal(formalRun!.RunId, duplicateRun!.RunId);
        }
        finally
        {
            await erpApp.StopAsync();
        }
    }

    [Fact]
    public async Task McpReadWriteFlow_FreshnessStaleCheck_PluginDisableInvalidatesApproval_AndStartupRecoveryMarksDispatchingAsOutcomeUnknown()
    {
        var toolRemoved = false;
        var writeExecutedCount = 0;

        var mcpBuilder = WebApplication.CreateSlimBuilder();
        mcpBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        mcpBuilder.Logging.ClearProviders();

        var inputSchema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement.Clone();
        mcpBuilder.Services
            .AddMcpServer(opts =>
            {
                opts.ProtocolVersion = BusinessSkillDefaults.LockedMcpProtocolVersion;
                opts.ServerInfo = new Implementation { Name = "P3McpServer", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithListToolsHandler((_, _) =>
            {
                var tools = new List<Tool>
                {
                    new()
                    {
                        Name = "get_pending",
                        Description = "读取待处理列表",
                        InputSchema = inputSchema,
                        Annotations = new ToolAnnotations { ReadOnlyHint = true }
                    }
                };
                if (!toolRemoved)
                {
                    tools.Add(new Tool
                    {
                        Name = "commit_batch",
                        Description = "执行批次提交",
                        InputSchema = inputSchema,
                        Annotations = new ToolAnnotations { ReadOnlyHint = false }
                    });
                }

                return ValueTask.FromResult(new ListToolsResult { Tools = tools });
            })
            .WithCallToolHandler((ctx, _) =>
            {
                if (ctx.Params?.Name == "get_pending")
                {
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = false,
                        StructuredContent = JsonDocument.Parse("""{"items":[{"id":"M-1"}]}""").RootElement.Clone()
                    });
                }

                Interlocked.Increment(ref writeExecutedCount);
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = false,
                    StructuredContent = JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()
                });
            });

        var mcpApp = mcpBuilder.Build();
        mcpApp.MapMcp("/mcp");
        await mcpApp.StartAsync();

        try
        {
            var baseUri = new Uri(mcpApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var dbPath = Path.Combine(_root, "data", "state.db");
            await using var host = await StartP3TestHostAsync(_root, dbPath);
            using var control = host.CreateControlClient();
            using var normal = host.CreateNormalClient();

            var plugin = await (await control.PostAsJsonAsync("/api/plugins", new CreateBusinessPluginRequest(
                "MCP ERP",
                BusinessPluginSourceType.Mcp,
                new Uri(baseUri, "/mcp").ToString(),
                "default",
                new EndpointPolicyDto { AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(baseUri), AllowedPathPrefix = "/mcp", AllowPlainHttpForIntranet = true })))
                .Content.ReadFromJsonAsync<BusinessPluginDto>();

            var disc = await (await control.PostAsJsonAsync($"/api/plugins/{plugin!.Id}/discover", new DiscoverPluginRequest("mcp-disc-1")))
                .Content.ReadFromJsonAsync<DiscoverPluginResultDto>();

            var getCapId = $"{plugin.Id}:MCP:get_pending";
            var commitCapId = $"{plugin.Id}:MCP:commit_batch";
            await control.PutAsJsonAsync($"/api/plugins/{plugin.Id}/capabilities/risk", new UpdateCapabilityRiskRequest(getCapId, CapabilityRiskLevel.Read));
            await control.PutAsJsonAsync($"/api/plugins/{plugin.Id}/capabilities/risk", new UpdateCapabilityRiskRequest(commitCapId, CapabilityRiskLevel.Write));

            // 1. 发布仅使用 get_pending + approve + commit_batch 的 MCP Skill
            var draft = await (await normal.PostAsJsonAsync("/api/skill-drafts",
                new CreateSkillDraftRequest(plugin.Id, disc!.Snapshot!.Id, "MCP Batch", "MCP batch"))).Content.ReadFromJsonAsync<SkillDraftDto>();
            draft = await (await control.PutAsJsonAsync($"/api/skill-drafts/{draft!.Id}/selection",
                new PutDraftSelectionRequest(draft.Revision, disc.Snapshot.Id, [getCapId, commitCapId]))).Content.ReadFromJsonAsync<SkillDraftDto>();

            var mcpDef = new SkillDefinitionV1
            {
                SchemaVersion = 1,
                SkillId = $"skl-{draft!.Id}",
                Version = 1,
                Name = "MCP Batch",
                Description = "MCP batch test",
                PluginId = plugin.Id,
                CatalogSnapshotId = disc.Snapshot.Id,
                SelectedCapabilityIds = [getCapId, commitCapId],
                UsedCapabilityIds = [getCapId, commitCapId],
                ConnectionBinding = new SkillConnectionBinding { ConnectionId = plugin.ConnectionId, Environment = "default", ConnectionRevision = 1, CredentialRevision = plugin.Connection!.CredentialRevision },
                Steps =
                [
                    new SkillToolStep { Id = "fetch", CapabilityId = getCapId },
                    new SkillApprovalStep { Id = "approve", TargetCapabilityId = commitCapId, ItemsSource = new() { Step = "fetch", Pointer = "/items" }, PlannedMutation = "提交 MCP 批次" },
                    new SkillForeachStep { Id = "loop", Source = new() { Step = "fetch", Pointer = "/items" }, BodyStepIds = ["commit"], MaxItems = 10, Sequential = true },
                    new SkillToolStep { Id = "commit", CapabilityId = commitCapId, Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal) { ["id"] = new() { Item = "item", Pointer = "/id" } } },
                    new SkillOutputStep { Id = "out", Source = new() { Step = "loop", Pointer = "/results" } }
                ],
                SuccessCriteria = new SkillSuccessCriteriaDto { RequiredSteps = ["fetch", "approve", "loop", "out"] }
            };

            draft = await (await normal.PutAsJsonAsync($"/api/skill-drafts/{draft.Id}",
                new UpdateSkillDraftRequest(draft.Revision, CandidateDefinition: mcpDef))).Content.ReadFromJsonAsync<SkillDraftDto>();
            draft = await (await normal.PostAsync($"/api/skill-drafts/{draft!.Id}/validate", null)).Content.ReadFromJsonAsync<SkillDraftDto>();

            var testRun = await (await control.PostAsJsonAsync($"/api/skill-drafts/{draft!.Id}/test-runs",
                new StartSkillRunRequest(null, "mcp-test-1", new Dictionary<string, JsonElement>(), IsTestRun: true, AllowWriteInTestRun: true))).Content.ReadFromJsonAsync<SkillRunDto>();
            var testWaiting = await WaitForRunStatusAsync(normal, testRun!.RunId, SkillRunStatus.WaitingForApproval);
            await control.PostAsJsonAsync($"/api/gateway/approvals/{testWaiting.PendingApproval!.ApprovalId}",
                new SubmitExecutionApprovalRequest(testWaiting.PendingApproval.RequestDigest, "allow_once"));
            await WaitForRunStatusAsync(normal, testRun.RunId, SkillRunStatus.Succeeded);
            Assert.Equal(1, Volatile.Read(ref writeExecutedCount));

            var latestDraft = await normal.GetFromJsonAsync<SkillDraftDto>($"/api/skill-drafts/{draft.Id}");
            var pubSkill = await (await control.PostAsJsonAsync($"/api/skill-drafts/{draft.Id}/publish",
                new PublishSkillDraftRequest(latestDraft!.Revision))).Content.ReadFromJsonAsync<PublishedSkillDto>();

            // 2. 启动运行进入 WaitingForApproval 后停用插件 -> 待审批必须立即作废且不再触发写入
            var runToCancel = await (await control.PostAsJsonAsync($"/api/skills/{pubSkill!.Id}/runs",
                new StartSkillRunRequest(null, "mcp-disable-run", new Dictionary<string, JsonElement>()))).Content.ReadFromJsonAsync<SkillRunDto>();
            await WaitForRunStatusAsync(normal, runToCancel!.RunId, SkillRunStatus.WaitingForApproval);

            var beforeDisableWrites = Volatile.Read(ref writeExecutedCount);
            await control.PostAsync($"/api/plugins/{plugin.Id}/disable", null);
            var afterDisableRun = await WaitForAnyTerminalStatusAsync(normal, runToCancel.RunId);
            Assert.NotEqual(SkillRunStatus.Succeeded, afterDisableRun.Status);
            Assert.Equal(beforeDisableWrites, Volatile.Read(ref writeExecutedCount));

            // 3. 验证 15 分钟目录过期且目标删除了工具时阻止启动新运行
            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                var p = await db.BusinessPlugins.FirstAsync(x => x.Id == plugin.Id);
                p.Enabled = true;
                var snap = await db.CatalogSnapshots.FirstAsync(x => x.Id == disc.Snapshot.Id);
                snap.FetchedAtUtc = DateTime.UtcNow.AddMinutes(-20);
                await db.SaveChangesAsync();
            }

            toolRemoved = true;
            var staleBlockedResp = await control.PostAsJsonAsync($"/api/skills/{pubSkill.Id}/runs",
                new StartSkillRunRequest(null, "mcp-stale-run", new Dictionary<string, JsonElement>()));
            Assert.Equal(HttpStatusCode.Conflict, staleBlockedResp.StatusCode);

            // 4. 模拟遗留 Dispatching 检查点，宿主恢复服务必须将其收敛为 OutcomeUnknown（严禁自动重放）
            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                db.SkillRuns.Add(new SkillRunEntity
                {
                    RunId = "srun-orphan-1",
                    SkillId = pubSkill.Id,
                    SkillVersion = 1,
                    ClientRequestId = "orphan-crash-1",
                    Status = nameof(SkillRunStatus.Running)
                });
                db.SkillRunSteps.Add(new SkillRunStepEntity
                {
                    RunId = "srun-orphan-1",
                    Sequence = 1,
                    StepId = "commit",
                    StepType = "tool",
                    State = nameof(SkillRunStepState.Dispatching)
                });
                await db.SaveChangesAsync();

                var runSvc = scope.ServiceProvider.GetRequiredService<IBusinessSkillRunService>();
                await runSvc.ReconcileOrphanedRunsOnStartupAsync();
            }

            var reconciled = await normal.GetFromJsonAsync<SkillRunDto>("/api/skill-runs/srun-orphan-1");
            Assert.Equal(SkillRunStatus.OutcomeUnknown, reconciled!.Status);
            Assert.Equal(SkillRunStepState.OutcomeUnknown, reconciled.Steps[0].State);
        }
        finally
        {
            await mcpApp.StopAsync();
        }
    }

    private static SkillDefinitionV1 BuildOpenApiReceivableSkill(
        string pluginId,
        string snapshotId,
        string listInvoicesCapId,
        string createTaskCapId)
        => new()
        {
            SchemaVersion = 1,
            SkillId = "skl-p3-receivable",
            Version = 1,
            Name = "P3 逾期应收催收 Skill",
            Description = "分页汇总并经 allow_once 审批创建催收任务",
            PluginId = pluginId,
            ConnectionBinding = new SkillConnectionBinding { Environment = "staging" },
            CatalogSnapshotId = snapshotId,
            SelectedCapabilityIds = [listInvoicesCapId, createTaskCapId],
            UsedCapabilityIds = [listInvoicesCapId, createTaskCapId],
            Inputs = new SkillInputSchemaDto
            {
                Required = ["currency", "asOfDate"],
                Properties = new Dictionary<string, SkillInputPropertyDto>(StringComparer.Ordinal)
                {
                    ["currency"] = new() { Type = "string" },
                    ["asOfDate"] = new() { Type = "string", Format = "date" }
                }
            },
            Steps =
            [
                new SkillToolStep
                {
                    Id = "fetch_invoices",
                    CapabilityId = listInvoicesCapId,
                    Pagination = new SkillPaginationConfig
                    {
                        Mode = "page",
                        PageParam = "page",
                        ItemsPointer = "/items",
                        HasMorePointer = "/hasMore",
                        PageSize = 50,
                        MaxPages = 10
                    }
                },
                new SkillTransformStep
                {
                    Id = "filter_overdue",
                    Operation = SkillTransformOperation.Filter,
                    Source = new() { Step = "fetch_invoices", Pointer = "/items" },
                    Predicate = new SkillConditionExpr
                    {
                        Combinator = "and",
                        Clauses =
                        [
                            new("overdueDays", "gt", new() { Literal = JsonSerializer.SerializeToElement(30) }),
                            new("unpaidAmount", "gt", new() { Literal = JsonSerializer.SerializeToElement(50_000m) }),
                            new("currency", "eq", new() { Input = "currency" })
                        ]
                    },
                    SortBy = [new("unpaidAmount", Descending: true)]
                },
                new SkillGenerateTextStep
                {
                    Id = "draft_advice",
                    Source = new() { Step = "filter_overdue", Pointer = "/rows" },
                    AllowedInputFields = ["invoiceId", "unpaidAmount", "currency", "overdueDays"],
                    PromptTemplate = "请生成简洁中文催收提醒"
                },
                new SkillApprovalStep
                {
                    Id = "approve_write",
                    TargetCapabilityId = createTaskCapId,
                    ItemsSource = new() { Step = "filter_overdue", Pointer = "/rows" },
                    PlannedMutation = "创建逾期催收任务",
                    KeyAmountField = "unpaidAmount",
                    CurrencyField = "currency",
                    TargetRecordFields = ["customerId", "invoiceId", "unpaidAmount"]
                },
                new SkillForeachStep
                {
                    Id = "dispatch_tasks",
                    Source = new() { Step = "filter_overdue", Pointer = "/rows" },
                    BodyStepIds = ["create_task"],
                    MaxItems = 20,
                    Sequential = true
                },
                new SkillToolStep
                {
                    Id = "create_task",
                    CapabilityId = createTaskCapId,
                    Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["customerId"] = new() { Item = "item", Pointer = "/customerId" },
                        ["invoiceId"] = new() { Item = "item", Pointer = "/invoiceId" },
                        ["unpaidAmount"] = new() { Item = "item", Pointer = "/unpaidAmount" },
                        ["currency"] = new() { Item = "item", Pointer = "/currency" },
                        ["advice"] = new() { Step = "draft_advice", Pointer = "/text" }
                    },
                    ResponseContract = new SkillResponseContract
                    {
                        BusinessCodePointer = "/code",
                        SuccessCodeValues = ["CREATED"],
                        CreatedIdPointer = "/data/taskId",
                        IdempotencyKeyTemplate = "{{item.invoiceId}}:{{inputs.asOfDate}}"
                    }
                },
                new SkillOutputStep
                {
                    Id = "final_output",
                    NamedOutputs = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["qualified"] = new() { Step = "filter_overdue", Pointer = "/rows" },
                        ["created"] = new() { Step = "dispatch_tasks", Pointer = "/results" }
                    }
                }
            ],
            SuccessCriteria = new SkillSuccessCriteriaDto
            {
                RequiredSteps = ["fetch_invoices", "filter_overdue", "approve_write", "dispatch_tasks", "final_output"],
                RequireCompletePagination = true,
                FailOnPartialData = true
            },
            DataPolicy = new SkillDataPolicyDto
            {
                AllowedModelFields = ["invoiceId", "unpaidAmount", "currency", "overdueDays"]
            }
        };

    private static async Task<SkillRunDto> WaitForRunStatusAsync(HttpClient client, string runId, SkillRunStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var dto = await client.GetFromJsonAsync<SkillRunDto>($"/api/skill-runs/{runId}");
            if (dto is not null && dto.Status == expected)
            {
                return dto;
            }

            if (dto is not null && expected == SkillRunStatus.WaitingForApproval && dto.Status is SkillRunStatus.Failed or SkillRunStatus.Cancelled)
            {
                Assert.Fail($"Run terminated early with {dto.Status}: {dto.Error?.Message}");
            }

            await Task.Delay(50);
        }

        var last = await client.GetFromJsonAsync<SkillRunDto>($"/api/skill-runs/{runId}");
        Assert.Fail($"Timed out waiting for run {runId} status {expected}; last status={last?.Status}, error={last?.Error?.Message}");
        return last!;
    }

    private static async Task<SkillRunDto> WaitForAnyTerminalStatusAsync(HttpClient client, string runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var dto = await client.GetFromJsonAsync<SkillRunDto>($"/api/skill-runs/{runId}");
            if (dto is not null && dto.Status is SkillRunStatus.Succeeded or SkillRunStatus.Failed or SkillRunStatus.Cancelled or SkillRunStatus.PartialSucceeded or SkillRunStatus.OutcomeUnknown)
            {
                return dto;
            }

            await Task.Delay(50);
        }

        return (await client.GetFromJsonAsync<SkillRunDto>($"/api/skill-runs/{runId}"))!;
    }

    private static async Task<P3TestHost> StartP3TestHostAsync(string root, string dbPath)
    {
        var credDir = Path.Combine(root, "creds");
        var workspacesRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(credDir);
        Directory.CreateDirectory(workspacesRoot);

        using var pipe = new StringReader(JsonSerializer.Serialize(new
        {
            accessToken = "p3-normal-token-111111111",
            controlToken = "p3-control-token-222222222"
        }) + "\n");

        var creds = LocalHostCredentials.Initialize(
            credDir,
            LocalTokenDeliveryMode.Pipe,
            ExecutionBackend.Local,
            unsafeFileControlToken: false,
            pipeReader: pipe);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var guard = new OutboundTargetPolicyGuard();
        var credStore = new InMemoryBusinessCredentialStore();

        builder.Services.AddCodexFlowGateway();
        builder.Services.AddSingleton(creds);
        builder.Services.AddSingleton<IControlPlaneStateProvider>(creds);
        builder.Services.AddSingleton(guard);
        builder.Services.AddSingleton<IBusinessCredentialStore>(credStore);
        builder.Services.AddSingleton<IOptionsMonitor<BusinessSkillsOptions>>(
            new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<BusinessSkillsOptions>(
                new BusinessSkillsOptions { Enabled = true }));
        builder.Services.AddSingleton<OpenApiBusinessPluginAdapter>();
        builder.Services.AddSingleton<McpBusinessPluginAdapter>();
        builder.Services.AddSingleton<IBusinessCapabilityExecutor, BusinessCapabilityExecutor>();
        builder.Services.AddSingleton<IBusinessSkillApprovalCoordinator, BusinessSkillApprovalCoordinator>();
        builder.Services.AddSingleton<IBusinessSkillGenerator>(_ => new StructuredBusinessSkillGenerator(chatClient: null));
        builder.Services.AddSingleton<SkillWorkflowEngine>(sp => new SkillWorkflowEngine(sp.GetRequiredService<IBusinessCapabilityExecutor>()));
        builder.Services.AddScoped<IBusinessPluginControlService, BusinessPluginControlService>();
        builder.Services.AddScoped<IBusinessSkillBuilderService, BusinessSkillBuilderService>();
        builder.Services.AddScoped<IBusinessSkillRunService, BusinessSkillRunService>();

        builder.Services.AddSingleton<IExecutionPermissionStore>(_ => Mock.Of<IExecutionPermissionStore>());
        builder.Services.AddSingleton<IExecutionApprovalBroker>(_ => Mock.Of<IExecutionApprovalBroker>());
        builder.Services.AddScoped<IChatService>(_ => Mock.Of<IChatService>());

        builder.Services.AddTestSqliteStorage(dbPath);
        // Exercise the EF compatibility adapters in this fixture. P5 uses the default SQLite stores.
        builder.Services.AddScoped<IBusinessPluginStore, EfBusinessPluginStore>();
        builder.Services.AddScoped<IBusinessSkillBuilderStore, EfBusinessSkillExecutionStore>();
        builder.Services.AddScoped<IBusinessSkillExecutionStore, EfBusinessSkillExecutionStore>();
        builder.Services.AddCodexFlowLocalDefaults();
        builder.Services.AddSingleton<IWorkspaceRootProvider>(new WorkspaceRootProvider(workspacesRoot, Path.GetDirectoryName(dbPath)));
        builder.Services.AddScoped<ITaskListService>(sp =>
            new CodexFlow.Infrastructure.Services.RedisTaskListService(
                sp.GetRequiredService<IKeyValueTextStore>(),
                NullLogger<CodexFlow.Infrastructure.Services.RedisTaskListService>.Instance));
        builder.Services.AddSingleton<ICodexGateway, global::CodexFlow.Gateway.CodexGateway>();
        builder.Services.AddScoped<IGatewayMessageProcessor>(_ => Mock.Of<IGatewayMessageProcessor>());

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("ActiveUser", p => p.RequireAuthenticatedUser());
            options.AddPolicy(
                ControlPlaneAuthConstants.ControlPlanePolicy,
                p => p.RequireAuthenticatedUser().RequireClaim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl));
        });

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCodexFlowGatewayBindingErrors();
        app.MapCodexFlowGateway();
        await app.StartAsync();

        var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        guard.RegisterProtectedLocalPort(baseUri.Port);
        return new P3TestHost(app, baseUri, creds);
    }

    private sealed class P3TestHost(WebApplication app, Uri baseUri, LocalHostCredentials creds) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient CreateNormalClient()
        {
            var c = new HttpClient { BaseAddress = baseUri };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            return c;
        }

        public HttpClient CreateControlClient()
        {
            var c = new HttpClient { BaseAddress = baseUri };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ControlToken);
            return c;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
