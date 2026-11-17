// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class OpenApiP0ValidationTests
{
    [Fact]
    public async Task DiscoverFromContent_SupportsSwagger20Json_PreservesNotesAndDuplicateOperationIds()
    {
        const string swagger20 = """
        {
          "swagger": "2.0",
          "info": { "title": "Legacy ERP", "version": "1.0.0" },
          "host": "erp.internal",
          "basePath": "/v1",
          "schemes": ["https"],
          "paths": {
            "/customers": {
              "get": {
                "operationId": "listItems",
                "summary": "查询客户列表",
                "description": "返回企业客户档案分页数据",
                "tags": ["customers"],
                "parameters": [
                  { "name": "page", "in": "query", "required": false, "type": "integer" }
                ],
                "responses": { "200": { "description": "OK" } }
              }
            },
            "/invoices": {
              "get": {
                "operationId": "listItems",
                "tags": ["invoices"],
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

        var result = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("erp-v2", "snap-20", swagger20, "json");

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("2.0", result.Snapshot.ProtocolVersion);
        Assert.True(result.Snapshot.Complete);
        Assert.Equal(2, result.Snapshot.TotalCapabilities);
        Assert.Equal(2, result.Capabilities.Count);

        var customersCap = result.Capabilities.Single(c => c.Invocation.NormalizedPath == "/customers");
        Assert.Equal("erp-v2:GET:/customers", customersCap.Summary.Id);
        Assert.Equal("查询客户列表\n\n返回企业客户档案分页数据", customersCap.Summary.Description);
        Assert.False(customersCap.Summary.SourceDescriptionMissing);
        Assert.Equal(CapabilityRiskLevel.Unknown, customersCap.Summary.Risk);
        Assert.Equal(CapabilityRiskLevel.Read, customersCap.Summary.SuggestedSourceRiskHint);

        var invoicesCap = result.Capabilities.Single(c => c.Invocation.NormalizedPath == "/invoices");
        Assert.Equal("erp-v2:GET:/invoices", invoicesCap.Summary.Id);
        Assert.True(invoicesCap.Summary.SourceDescriptionMissing);
        Assert.Equal("源接口未提供说明", invoicesCap.Summary.Description);
        Assert.NotEqual(customersCap.Summary.ModelAlias, invoicesCap.Summary.ModelAlias);
    }

    [Fact]
    public async Task DiscoverFromContent_SupportsOpenApi30Yaml_WithParameterInheritanceAndUnsupportedSiblingIsolation()
    {
        const string yaml30 = """
        openapi: 3.0.3
        info:
          title: ERP Collection API
          version: 1.2.0
        servers:
          - url: https://erp.corp.local/api
        paths:
          /orgs/{orgId}/invoices:
            parameters:
              - name: orgId
                in: path
                required: true
                description: Path-level organization identifier
                schema:
                  type: string
              - name: currency
                in: query
                required: false
                description: Path default currency filter
                schema:
                  type: string
            get:
              operationId: queryInvoices
              summary: 查询应收账款
              parameters:
                - name: currency
                  in: query
                  required: true
                  description: Operation overrides currency as required
                  schema:
                    type: string
              responses:
                '200':
                  description: Invoice list
          /attachments/upload:
            post:
              operationId: uploadEvidence
              summary: 上传附件
              requestBody:
                required: true
                content:
                  multipart/form-data:
                    schema:
                      type: object
              responses:
                '201':
                  description: Created
        """;

        var result = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("erp-yaml", "snap-30", yaml30, "yaml");

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("3.0", result.Snapshot.ProtocolVersion);
        Assert.Equal(2, result.Snapshot.TotalCapabilities);
        Assert.Equal(1, result.Snapshot.ExecutableCount);
        Assert.Equal(1, result.Snapshot.UnsupportedCount);

        var queryCap = result.Capabilities.Single(c => c.Summary.Id == "erp-yaml:GET:/orgs/{orgId}/invoices");
        Assert.Equal(CapabilityExecutabilityStatus.Executable, queryCap.Summary.Executability);
        Assert.Equal(2, queryCap.Invocation.Parameters.Count);
        Assert.Contains(queryCap.Invocation.Parameters, p => p.Name == "orgId" && p.Location == "path" && p.Required);
        Assert.Contains(queryCap.Invocation.Parameters, p => p.Name == "currency" && p.Required && p.Description == "Operation overrides currency as required");

        var uploadCap = result.Capabilities.Single(c => c.Summary.Id == "erp-yaml:POST:/attachments/upload");
        Assert.Equal(CapabilityExecutabilityStatus.Unsupported, uploadCap.Summary.Executability);
        Assert.NotEmpty(uploadCap.Summary.Diagnostics);
    }

    [Fact]
    public async Task DiscoverFromContent_SupportsOpenApi31Json_AndIsolatesOneOfSchema()
    {
        const string json31 = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Collection OpenAPI 3.1", "version": "1.0.0" },
          "paths": {
            "/repayments": {
              "get": {
                "summary": "分页查询回款",
                "parameters": [
                  { "name": "page", "in": "query", "schema": { "type": "integer" } }
                ],
                "responses": { "200": { "description": "OK" } }
              }
            },
            "/mixed": {
              "post": {
                "summary": "复杂多态写入",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "oneOf": [
                          { "type": "object" },
                          { "type": "string" }
                        ]
                      }
                    }
                  }
                },
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

        var result = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("erp-31", "snap-31", json31, "json");

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("3.1", result.Snapshot.ProtocolVersion);
        Assert.Equal(1, result.Snapshot.ExecutableCount);
        Assert.Equal(1, result.Snapshot.UnsupportedCount);
    }

    [Fact]
    public async Task DiscoverFromContent_RejectsOpenApi32_AndHtmlLoginPage()
    {
        const string openApi32 = """
        openapi: 3.2.0
        info:
          title: Future Spec
          version: 1.0.0
        paths: {}
        """;

        var v32Result = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("erp-32", "snap-32", openApi32, "yaml");
        Assert.False(v32Result.Success);
        Assert.NotNull(v32Result.Error);
        Assert.Equal(BusinessSkillErrorCodes.UnsupportedProtocol, v32Result.Error.Code);

        const string htmlLogin = "<!DOCTYPE html><html><body><form action='/login'></form></body></html>";
        var htmlResult = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("erp-html", "snap-html", htmlLogin);
        Assert.False(htmlResult.Success);
        Assert.NotNull(htmlResult.Error);
        Assert.Equal(BusinessSkillErrorCodes.AuthRequired, htmlResult.Error.Code);
    }

    [Fact]
    public async Task EndToEndSample_DiscoversOpenApiFromLocalKestrelServer_AndExecutesRealGetAndPostCalls()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        const string openApiDoc = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Sample ERP Test Server", "version": "1.0.0" },
          "paths": {
            "/api/customers/{customerId}/invoices": {
              "get": {
                "operationId": "getCustomerInvoices",
                "summary": "获取指定客户应收账款",
                "parameters": [
                  { "name": "customerId", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "currency", "in": "query", "required": true, "schema": { "type": "string" } }
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
                      "schema": { "type": "object" }
                    }
                  }
                },
                "responses": { "201": { "description": "Created" } }
              }
            }
          }
        }
        """;

        app.MapGet("/swagger/v1/swagger.json", () => Results.Text(openApiDoc, "application/json", Encoding.UTF8));
        app.MapGet("/api/customers/{customerId}/invoices", (string customerId, string currency) => Results.Json(new
        {
            customerId,
            currency,
            items = new[]
            {
                new { invoiceId = "INV-1001", amount = 120000m }
            }
        }));
        app.MapPost("/api/collection-tasks", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var customerId = doc.RootElement.GetProperty("customerId").GetString();
            return Results.Created("/api/collection-tasks/TASK-901", new
            {
                taskId = "TASK-901",
                customerId
            });
        });

        await app.StartAsync();
        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var swaggerText = await httpClient.GetStringAsync("/swagger/v1/swagger.json");
            var discovery = await OpenApiDiscoveryPrototype.DiscoverFromContentAsync("sample-erp", "snap-live", swaggerText, "json");
            Assert.True(discovery.Success);
            Assert.Equal(2, discovery.Capabilities.Count);

            var getCap = discovery.Capabilities.Single(c => c.Summary.Id == "sample-erp:GET:/api/customers/{customerId}/invoices");
            var getInvocation = await OpenApiDiscoveryPrototype.InvokeOperationAsync(
                httpClient,
                new Uri(baseUrl),
                getCap,
                new Dictionary<string, string?>
                {
                    ["customerId"] = "CUST-01",
                    ["currency"] = "CNY"
                });

            Assert.True(getInvocation.Success);
            Assert.Equal((int)HttpStatusCode.OK, getInvocation.StatusCode);
            Assert.NotNull(getInvocation.BodyJson);
            Assert.Equal("INV-1001", getInvocation.BodyJson.Value.GetProperty("items")[0].GetProperty("invoiceId").GetString());

            var postCap = discovery.Capabilities.Single(c => c.Summary.Id == "sample-erp:POST:/api/collection-tasks");
            using var payload = JsonDocument.Parse("""{"customerId":"CUST-01","amount":70000}""");
            var postInvocation = await OpenApiDiscoveryPrototype.InvokeOperationAsync(
                httpClient,
                new Uri(baseUrl),
                postCap,
                new Dictionary<string, string?>(),
                payload.RootElement);

            Assert.True(postInvocation.Success);
            Assert.Equal((int)HttpStatusCode.Created, postInvocation.StatusCode);
            Assert.Equal("TASK-901", postInvocation.BodyJson!.Value.GetProperty("taskId").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
