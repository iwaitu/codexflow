// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Services;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P1ControlPlaneAndSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p1-cp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task FeatureSwitch_DisabledByDefault_Returns503_AndLeavesExistingTablesReadableAfterMigration()
    {
        var dbPath = Path.Combine(_root, "data", "state.db");
        await using var host = await StartP1TestHostAsync(_root, dbPath, featureEnabled: false);

        // 先在现有表写入数据验证迁移兼容
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
            db.Sessions.Add(new CodexFlow.Domain.Chat.IvilsonSession
            {
                Id = "legacy-sess-1",
                UserId = LocalUser.Id,
                Name = "Legacy Workspace",
                WorkspacePath = _root
            });
            await db.SaveChangesAsync();
            Assert.NotNull(await db.Sessions.FirstOrDefaultAsync(s => s.Id == "legacy-sess-1"));
        }

        using var client = host.CreateNormalClient();
        var resp = await client.GetAsync("/api/plugins");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

        var err = await resp.Content.ReadFromJsonAsync<BusinessSkillErrorDto>();
        Assert.NotNull(err);
        Assert.Equal(BusinessSkillErrorCodes.ConnectionUnavailable, err!.Code);
    }

    [Fact]
    public async Task ControlPlaneEndpoints_RejectUnauthenticatedAndNormalTokens_Enforce401And403()
    {
        var dbPath = Path.Combine(_root, "data", "state.db");
        await using var host = await StartP1TestHostAsync(_root, dbPath, featureEnabled: true);

        using var anon = new HttpClient { BaseAddress = host.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/plugins")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/plugins", new { })).StatusCode);

        using var normal = host.CreateNormalClient();
        Assert.Equal(HttpStatusCode.OK, (await normal.GetAsync("/api/plugins")).StatusCode);

        var createReq = new CreateBusinessPluginRequest(
            "ERP",
            BusinessPluginSourceType.OpenApi,
            "https://erp.example/swagger.json",
            "prod",
            new EndpointPolicyDto { AllowedOrigin = "https://erp.example" });

        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PostAsJsonAsync("/api/plugins", createReq)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PutAsJsonAsync("/api/plugins/plg-1/connection", new UpdatePluginConnectionRequest(1, "https://erp.example", "prod", new EndpointPolicyDto()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PutAsJsonAsync("/api/plugins/plg-1/credentials/document", new SetPluginCredentialRequest(BusinessAuthKind.Bearer, "{\"token\":\"abc\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.DeleteAsync("/api/plugins/plg-1/credentials/document")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PostAsync("/api/plugins/plg-1/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PostAsJsonAsync("/api/plugins/plg-1/discover", new DiscoverPluginRequest("req-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await normal.PutAsJsonAsync("/api/plugins/plg-1/capabilities/risk", new UpdateCapabilityRiskRequest("c1", CapabilityRiskLevel.Read))).StatusCode);
    }

    [Fact]
    public async Task EndToEndControlPlane_CredentialIsolation_IdempotentDiscover_SnapshotPaginationAndDiff_AndDisable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string secretDocToken = "doc-secret-token-enterprise-7788";
        var specVersion = 1;
        var erpHitCount = 0;

        const string specV1 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Enterprise ERP", "version": "1.0" },
          "paths": {
            "/api/customers": {
              "get": {
                "summary": "查询客户档案",
                "tags": ["customers"],
                "responses": { "200": { "description": "OK" } }
              }
            },
            "/api/invoices": {
              "get": {
                "summary": "查询应收账款",
                "tags": ["receivables"],
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

        const string specV2 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Enterprise ERP", "version": "2.0" },
          "paths": {
            "/api/customers": {
              "get": {
                "summary": "查询客户档案",
                "tags": ["customers"],
                "parameters": [
                  { "name": "orgScope", "in": "query", "required": true, "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "OK" } }
              }
            },
            "/api/invoices": {
              "get": {
                "summary": "查询应收账款",
                "tags": ["receivables"],
                "responses": { "200": { "description": "OK" } }
              }
            },
            "/api/collection-tasks": {
              "post": {
                "summary": "创建催收任务",
                "tags": ["receivables"],
                "requestBody": {
                  "content": { "application/json": { "schema": { "type": "object" } } }
                },
                "responses": { "201": { "description": "Created" } }
              }
            }
          }
        }
        """;

        var erpBuilder = WebApplication.CreateSlimBuilder();
        erpBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var erpApp = erpBuilder.Build();
        erpApp.MapGet("/api/swagger.json", (HttpRequest req) =>
        {
            if (!string.Equals(req.Headers.Authorization.ToString(), $"Bearer {secretDocToken}", StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            Interlocked.Increment(ref erpHitCount);
            return Results.Text(specVersion == 1 ? specV1 : specV2, "application/json", Encoding.UTF8);
        });
        await erpApp.StartAsync();

        try
        {
            var erpBase = new Uri(erpApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var erpSpecUrl = new Uri(erpBase, "/api/swagger.json").AbsoluteUri;

            var dbPath = Path.Combine(_root, "data", "state.db");
            await using var host = await StartP1TestHostAsync(_root, dbPath, featureEnabled: true);
            using var control = host.CreateControlClient();
            using var normal = host.CreateNormalClient();

            // 1. 试图配置指向 CodexFlow 自身控制端口的插件连接必须被拒绝
            var selfPortCreate = await control.PostAsJsonAsync("/api/plugins", new CreateBusinessPluginRequest(
                "SelfProbe",
                BusinessPluginSourceType.OpenApi,
                new Uri(host.BaseUri, "/openapi/v1.json").AbsoluteUri,
                "local",
                new EndpointPolicyDto
                {
                    AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(host.BaseUri),
                    AllowPlainHttpForIntranet = true
                }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, selfPortCreate.StatusCode);

            // 2. 创建合法企业插件连接
            var createResp = await control.PostAsJsonAsync("/api/plugins", new CreateBusinessPluginRequest(
                "Sample ERP",
                BusinessPluginSourceType.OpenApi,
                erpSpecUrl,
                "staging",
                new EndpointPolicyDto
                {
                    AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(erpBase),
                    AllowedPathPrefix = "/api",
                    AllowPlainHttpForIntranet = true
                }));
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var plugin = await createResp.Content.ReadFromJsonAsync<BusinessPluginDto>();
            Assert.NotNull(plugin);

            // 3. 设置文档凭据：响应与 SQLite 数据库文件都不能包含明文密钥
            var credResp = await control.PutAsJsonAsync(
                $"/api/plugins/{plugin!.Id}/credentials/document",
                new SetPluginCredentialRequest(BusinessAuthKind.Bearer, $$"""{"token":"{{secretDocToken}}"}"""));
            var credBody = await credResp.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, credResp.StatusCode);
            Assert.DoesNotContain(secretDocToken, credBody, StringComparison.Ordinal);

            var credSummary = await credResp.Content.ReadFromJsonAsync<CredentialRefSummaryDto>();
            Assert.NotNull(credSummary);
            Assert.Equal("token:****7788", credSummary!.MaskedHint);

            await using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MaskedHint, CipherFileName FROM BusinessCredentialMetas";
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("token:****7788", reader.GetString(0));
                Assert.EndsWith(".dpapi", reader.GetString(1), StringComparison.Ordinal);
            }

            // 4. 连接 Revision 乐观并发冲突检查
            var conflictConn = await control.PutAsJsonAsync(
                $"/api/plugins/{plugin.Id}/connection",
                new UpdatePluginConnectionRequest(
                    ExpectedRevision: 999,
                    erpSpecUrl,
                    "staging",
                    plugin.Connection!.EndpointPolicy));
            Assert.Equal(HttpStatusCode.Conflict, conflictConn.StatusCode);

            // 5. 首次 discover (v1) + 同 clientRequestId 幂等复重验证
            var discoverResp1 = await control.PostAsJsonAsync(
                $"/api/plugins/{plugin.Id}/discover",
                new DiscoverPluginRequest("discover-v1-req"));
            Assert.Equal(HttpStatusCode.Accepted, discoverResp1.StatusCode);
            var discover1 = await discoverResp1.Content.ReadFromJsonAsync<DiscoverPluginResultDto>();
            Assert.NotNull(discover1);
            Assert.False(discover1!.Operation.ReusedFromClientRequestId);
            Assert.Equal(2, discover1.Snapshot!.TotalCapabilities);
            Assert.Equal(1, Volatile.Read(ref erpHitCount));

            var discoverDup = await control.PostAsJsonAsync(
                $"/api/plugins/{plugin.Id}/discover",
                new DiscoverPluginRequest("discover-v1-req"));
            Assert.Equal(HttpStatusCode.Accepted, discoverDup.StatusCode);
            var discoverDupDto = await discoverDup.Content.ReadFromJsonAsync<DiscoverPluginResultDto>();
            Assert.True(discoverDupDto!.Operation.ReusedFromClientRequestId);
            Assert.Equal(discover1.Operation.OperationId, discoverDupDto.Operation.OperationId);
            Assert.Equal(1, Volatile.Read(ref erpHitCount));

            // 普通用户查询异步操作状态与分页目录
            var opStatusResp = await normal.GetFromJsonAsync<PluginOperationStatusDto>(
                $"/api/plugin-operations/{discover1.Operation.OperationId}");
            Assert.Equal(PluginOperationStatus.Succeeded, opStatusResp!.Status);

            var capPage = await normal.GetFromJsonAsync<CapabilityPageResponseDto>(
                $"/api/plugins/{plugin.Id}/capabilities?snapshotId={discover1.Snapshot.Id}&groupTag=receivables&page=1&pageSize=1");
            Assert.NotNull(capPage);
            Assert.Equal(2, capPage!.TotalCount);
            Assert.Equal(1, capPage.FilteredCount);
            Assert.Single(capPage.Items);
            Assert.Equal(CapabilityRiskLevel.Unknown, capPage.Items[0].Risk);

            // 6. 控制面审核确认风险等级 & P2 详情接口查询
            var riskResp = await control.PutAsJsonAsync(
                $"/api/plugins/{plugin.Id}/capabilities/risk",
                new UpdateCapabilityRiskRequest(capPage.Items[0].Id, CapabilityRiskLevel.Read, "Verified read-only list endpoint"));
            Assert.Equal(HttpStatusCode.OK, riskResp.StatusCode);
            var updatedCap = await riskResp.Content.ReadFromJsonAsync<CapabilitySummaryDto>();
            Assert.Equal(CapabilityRiskLevel.Read, updatedCap!.Risk);

            var detailResp = await normal.GetAsync(
                $"/api/plugins/{plugin.Id}/capabilities/detail?capabilityId={Uri.EscapeDataString(capPage.Items[0].Id)}");
            Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
            var detailDto = await detailResp.Content.ReadFromJsonAsync<CapabilityDetailDto>();
            Assert.NotNull(detailDto);
            Assert.Equal(CapabilityRiskLevel.Read, detailDto!.Summary.Risk);
            Assert.Equal(BusinessPluginSourceType.OpenApi, detailDto.Invocation.SourceType);

            var missingDetailResp = await normal.GetAsync(
                $"/api/plugins/{plugin.Id}/capabilities/detail?capabilityId={Uri.EscapeDataString("missing-cap")}");
            Assert.Equal(HttpStatusCode.NotFound, missingDetailResp.StatusCode);

            // 7. v2 刷新目录，验证 Diff（Added + InputChanged -> TriggersNeedsRevalidation=true）
            specVersion = 2;
            var discoverResp2 = await control.PostAsJsonAsync(
                $"/api/plugins/{plugin.Id}/discover",
                new DiscoverPluginRequest("discover-v2-req"));
            Assert.Equal(HttpStatusCode.Accepted, discoverResp2.StatusCode);
            var discover2 = await discoverResp2.Content.ReadFromJsonAsync<DiscoverPluginResultDto>();
            Assert.NotNull(discover2!.Diff);
            Assert.True(discover2.Diff!.TriggersNeedsRevalidation);
            Assert.Contains(discover2.Diff.Entries, e => e.ChangeKind == CatalogDiffChangeKind.Added);
            Assert.Contains(discover2.Diff.Entries, e => e.ChangeKind == CatalogDiffChangeKind.InputChanged);

            // 8. 停用插件后立即阻止后续发现派发
            var disableResp = await control.PostAsync($"/api/plugins/{plugin.Id}/disable", null);
            Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

            var postDisableDiscover = await control.PostAsJsonAsync(
                $"/api/plugins/{plugin.Id}/discover",
                new DiscoverPluginRequest("discover-after-disable"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, postDisableDiscover.StatusCode);
        }
        finally
        {
            await erpApp.StopAsync();
        }
    }

    private static async Task<P1TestHost> StartP1TestHostAsync(string root, string dbPath, bool featureEnabled)
    {
        var credDir = Path.Combine(root, "creds");
        var workspacesRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(credDir);
        Directory.CreateDirectory(workspacesRoot);

        using var pipe = new StringReader(System.Text.Json.JsonSerializer.Serialize(new
        {
            accessToken = "p1-normal-token-111111111",
            controlToken = "p1-control-token-222222222"
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
        var credStore = new DpapiBusinessCredentialStore(Path.Combine(credDir, "business-plugins"));

        builder.Services.AddControllers().AddApplicationPart(typeof(BusinessPluginsController).Assembly);
        builder.Services.AddSingleton(creds);
        builder.Services.AddSingleton<IControlPlaneStateProvider>(creds);
        builder.Services.AddSingleton(guard);
        builder.Services.AddSingleton<IBusinessCredentialStore>(credStore);
        builder.Services.AddSingleton<IOptionsMonitor<BusinessSkillsOptions>>(
            new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<BusinessSkillsOptions>(
                new BusinessSkillsOptions { Enabled = featureEnabled }));
        builder.Services.AddScoped<IBusinessPluginControlService, CodexFlow.Orchestration.BusinessSkills.BusinessPluginControlService>();

        builder.Services.AddCodexFlowSqliteStorage(dbPath);
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
        app.MapControllers();
        await app.StartAsync();

        var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        guard.RegisterProtectedLocalPort(baseUri.Port);
        return new P1TestHost(app, baseUri, creds);
    }

    private sealed class P1TestHost(WebApplication app, Uri baseUri, LocalHostCredentials creds) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public Uri BaseUri { get; } = baseUri;

        public HttpClient CreateNormalClient()
        {
            var c = new HttpClient { BaseAddress = BaseUri };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            return c;
        }

        public HttpClient CreateControlClient()
        {
            var c = new HttpClient { BaseAddress = BaseUri };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ControlToken);
            return c;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
