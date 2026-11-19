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
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P2OpenApiAdapterTests
{
    [Fact]
    public async Task Discover_SwaggerUiHtml_ResolvesInitializerAndConfigJson_AndRejectsScriptDependentOrLoginHtml()
    {
        var adapter = new OpenApiBusinessPluginAdapter(new OutboundTargetPolicyGuard());
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/swagger/index.html", () => Results.Content("""
            <!DOCTYPE html>
            <html>
              <head><title>Swagger UI</title></head>
              <body>
                <div id="swagger-ui"></div>
                <script src="./swagger-initializer.js"></script>
              </body>
            </html>
            """, "text/html"));

        app.MapGet("/swagger/swagger-initializer.js", () => Results.Content("""
            window.onload = function() {
              window.ui = SwaggerUIBundle({
                configUrl: "/swagger/config.json"
              });
            };
            """, "application/javascript"));

        app.MapGet("/swagger/config.json", () => Results.Json(new
        {
            urls = new[]
            {
                new { url = "/v1/openapi.json", name = "v1" }
            }
        }));

        app.MapGet("/v1/openapi.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "SwaggerResolvedAPI", "version": "1.0.0" },
              "paths": {
                "/api/orders": {
                  "get": {
                    "operationId": "listOrders",
                    "summary": "查询订单",
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """, "application/json"));

        app.MapGet("/dynamic-swagger/index.html", () => Results.Content("""
            <!DOCTYPE html>
            <html><body><script>window.ui = SwaggerUIBundle({ dom_id: '#swagger-ui' });</script></body></html>
            """, "text/html"));

        app.MapGet("/login.html", () => Results.Content("""
            <!DOCTYPE html>
            <html><body><form method="post"><input type="text" name="user" /><input type="password" name="pwd" /></form></body></html>
            """, "text/html"));

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var policy = new EndpointPolicyDto
            {
                AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(baseUri),
                AllowedPathPrefix = "/",
                AllowPlainHttpForIntranet = true
            };

            var ok = await adapter.DiscoverFromUriAsync("plg-swg", "snap-1", new Uri(baseUri, "/swagger/index.html"), policy);
            Assert.True(ok.Success, ok.Error?.Message);
            Assert.Single(ok.Capabilities);
            Assert.Equal("plg-swg:GET:/api/orders", ok.Capabilities[0].Summary.Id);

            var dyn = await adapter.DiscoverFromUriAsync("plg-swg", "snap-2", new Uri(baseUri, "/dynamic-swagger/index.html"), policy);
            Assert.False(dyn.Success);
            Assert.Equal(BusinessSkillErrorCodes.UnsupportedProtocol, dyn.Error!.Code);

            var login = await adapter.DiscoverFromUriAsync("plg-swg", "snap-3", new Uri(baseUri, "/login.html"), policy);
            Assert.False(login.Success);
            Assert.Equal(BusinessSkillErrorCodes.AuthRequired, login.Error!.Code);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_ExternalRefs_DisabledByDefault_AllowsWhenEnabled_AndBlocksCrossOriginOrExceedingFileLimit()
    {
        var adapter = new OpenApiBusinessPluginAdapter(new OutboundTargetPolicyGuard());
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/spec/same-origin.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "RefApi", "version": "1.0.0" },
              "paths": {
                "/api/invoices": {
                  "post": {
                    "operationId": "createInvoice",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "/schemas/invoice.json#/components/schemas/InvoiceReq" }
                        }
                      }
                    },
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "/schemas/invoice.json#/components/schemas/InvoiceResp" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """, "application/json"));

        app.MapGet("/schemas/invoice.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "InvoiceSchemas", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "InvoiceReq": {
                    "type": "object",
                    "properties": { "customerId": { "type": "string" } },
                    "required": ["customerId"]
                  },
                  "InvoiceResp": {
                    "type": "object",
                    "properties": { "invoiceId": { "type": "string" } }
                  }
                }
              }
            }
            """, "application/json"));

        app.MapGet("/spec/many-refs.json", () =>
        {
            var props = new StringBuilder();
            for (var i = 0; i < 22; i++)
            {
                if (i > 0) props.Append(',');
                props.Append($"\"p{i}\": {{ \"$ref\": \"/schemas/p{i}.json#/components/schemas/Item\" }}");
            }

            var json = $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "TooManyRefs", "version": "1.0.0" },
              "paths": {
                "/api/many": {
                  "post": {
                    "operationId": "many",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": { {{props}} }
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
            return Results.Content(json, "application/json");
        });

        app.MapGet("/schemas/p{idx}.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "SharedSchema", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Item": { "type": "string" }
                }
              }
            }
            """, "application/json"));

        app.MapGet("/spec/cross-origin.json", () => Results.Content("""
            {
              "openapi": "3.0.3",
              "info": { "title": "CrossRefApi", "version": "1.0.0" },
              "paths": {
                "/api/cross": {
                  "post": {
                    "operationId": "cross",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "http://127.0.0.1:65432/ext.json#/components/schemas/Req" }
                        }
                      }
                    },
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """, "application/json"));

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);

            var disabledPolicy = new EndpointPolicyDto
            {
                AllowedOrigin = origin,
                AllowPlainHttpForIntranet = true,
                AllowExternalOpenApiRefs = false
            };
            var disabledRes = await adapter.DiscoverFromUriAsync("plg-ref", "snap-disabled", new Uri(baseUri, "/spec/same-origin.json"), disabledPolicy);
            Assert.True(disabledRes.Success, disabledRes.Error?.Message);
            Assert.Single(disabledRes.Capabilities);
            Assert.Equal(CapabilityExecutabilityStatus.NeedsConfiguration, disabledRes.Capabilities[0].Summary.Executability);

            var disabledExecRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-ref",
                SnapshotId = "snap-disabled",
                Capability = disabledRes.Capabilities[0],
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = disabledPolicy
            });
            Assert.False(disabledExecRes.Success);
            Assert.Equal(BusinessSkillErrorCodes.UnsupportedSchema, disabledExecRes.ErrorCode);

            var enabledPolicy = disabledPolicy with { AllowExternalOpenApiRefs = true };
            var enabledRes = await adapter.DiscoverFromUriAsync("plg-ref", "snap-enabled", new Uri(baseUri, "/spec/same-origin.json"), enabledPolicy);
            Assert.True(enabledRes.Success, enabledRes.Error?.Message);
            Assert.Single(enabledRes.Capabilities);
            Assert.True(
                enabledRes.Capabilities[0].Summary.Executability == CapabilityExecutabilityStatus.Executable,
                $"SnapDiags=[{string.Join(" | ", enabledRes.Snapshot!.Diagnostics)}]; CapDiags=[{string.Join(" | ", enabledRes.Capabilities[0].Summary.Diagnostics)}]");
            Assert.Contains("customerId", enabledRes.Capabilities[0].Invocation.RequestBodySchemaJson, StringComparison.Ordinal);
            Assert.Contains("invoiceId", enabledRes.Capabilities[0].Invocation.ResponseSchemaJson, StringComparison.Ordinal);

            var tooManyRes = await adapter.DiscoverFromUriAsync("plg-ref", "snap-many", new Uri(baseUri, "/spec/many-refs.json"), enabledPolicy);
            Assert.False(tooManyRes.Success,
                $"Expected failure for >20 external $ref files. Caps={tooManyRes.Capabilities.Count}, Diags=[{string.Join(" | ", tooManyRes.Snapshot?.Diagnostics ?? [])}], ReqSchema={tooManyRes.Capabilities.FirstOrDefault()?.Invocation.RequestBodySchemaJson}");

            var crossRes = await adapter.DiscoverFromUriAsync("plg-ref", "snap-cross", new Uri(baseUri, "/spec/cross-origin.json"), enabledPolicy);
            Assert.False(crossRes.Success);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Invoke_ServerPriorityAndTemplateVars_SecurityOrAnd_ParameterSerialization_AndUnsupportedRejections()
    {
        var adapter = new OpenApiBusinessPluginAdapter(new OutboundTargetPolicyGuard());
        string? observedRequestLine = null;
        string? observedBearer = null;
        string? observedApiKey = null;
        string? observedTraceHeader = null;
        string? observedBody = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapPost("/v2/tenants/{tenantId}/items", async (HttpContext ctx, string tenantId) =>
        {
            observedRequestLine = $"{ctx.Request.Path}{ctx.Request.QueryString}";
            observedBearer = ctx.Request.Headers.Authorization.ToString();
            observedApiKey = ctx.Request.Headers["X-Tenant-Key"].ToString();
            observedTraceHeader = ctx.Request.Headers["X-Correlation"].ToString();
            using var reader = new StreamReader(ctx.Request.Body);
            observedBody = await reader.ReadToEndAsync();
            return Results.Json(new { tenantId, accepted = true });
        });

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);

            var yamlSpec = $$"""
                openapi: 3.0.3
                info:
                  title: MatrixAPI
                  version: 1.0.0
                servers:
                  - url: {{origin}}/v1
                components:
                  securitySchemes:
                    BearerAuth:
                      type: http
                      scheme: bearer
                    TenantKey:
                      type: apiKey
                      in: header
                      name: X-Tenant-Key
                    QueryKey:
                      type: apiKey
                      in: query
                      name: api_key
                paths:
                  /tenants/{tenantId}/items:
                    servers:
                      - url: {{origin}}/ignored-path-level
                    post:
                      operationId: updateTenantItems
                      summary: 写入租户明细
                      servers:
                        - url: {{origin}}/{env}
                          variables:
                            env:
                              default: v2
                              enum: [v2, staging]
                      security:
                        - BearerAuth: []
                          TenantKey: []
                        - QueryKey: []
                      parameters:
                        - name: tenantId
                          in: path
                          required: true
                          schema: { type: string }
                        - name: tags
                          in: query
                          explode: false
                          schema:
                            type: array
                            items: { type: string }
                        - name: filters
                          in: query
                          explode: true
                          schema:
                            type: array
                            items: { type: string }
                        - name: X-Correlation
                          in: header
                          required: true
                          schema: { type: string }
                      requestBody:
                        required: true
                        content:
                          application/json:
                            schema:
                              type: object
                              properties:
                                count: { type: integer }
                      responses:
                        "200":
                          description: OK
                  /unsupported-cookie:
                    get:
                      parameters:
                        - name: sid
                          in: cookie
                          schema: { type: string }
                      responses:
                        "200": { description: OK }
                  /unsupported-multipart:
                    post:
                      requestBody:
                        content:
                          multipart/form-data:
                            schema: { type: object }
                      responses:
                        "200": { description: OK }
                """;

            var disc = await OpenApiBusinessPluginAdapter.DiscoverFromContentAsync("plg-mat", "snap-1", yamlSpec, "yaml");
            Assert.True(disc.Success);

            var cookieCap = disc.Capabilities.Single(c => c.Summary.NativeIdentity.Contains("unsupported-cookie"));
            Assert.Equal(CapabilityExecutabilityStatus.Unsupported, cookieCap.Summary.Executability);

            var multiCap = disc.Capabilities.Single(c => c.Summary.NativeIdentity.Contains("unsupported-multipart"));
            Assert.Equal(CapabilityExecutabilityStatus.Unsupported, multiCap.Summary.Executability);

            var opCap = disc.Capabilities.Single(c => c.Summary.NativeIdentity.Contains("/tenants/{tenantId}/items"));
            Assert.Equal(CapabilityExecutabilityStatus.Executable, opCap.Summary.Executability);
            Assert.Equal($"{origin}/{{env}}", opCap.Invocation.SelectedServerUrl);
            Assert.Equal(2, opCap.Invocation.SecurityAlternatives.Count);

            var policy = new EndpointPolicyDto
            {
                AllowedOrigin = origin,
                AllowPlainHttpForIntranet = true
            };

            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["tenantId"] = JsonSerializer.SerializeToElement("t/01"),
                ["tags"] = JsonSerializer.SerializeToElement(new[] { "a", "b" }),
                ["filters"] = JsonSerializer.SerializeToElement(new[] { "f1", "f2" }),
                ["X-Correlation"] = JsonSerializer.SerializeToElement("corr-99")
            };

            // 非法 server variable 值必须被拒绝
            var invalidVarRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mat",
                SnapshotId = "snap-1",
                Capability = opCap,
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = policy,
                AuthKind = BusinessAuthKind.Bearer,
                SecretPayloadJson = """{"token":"tok-1","headerName":"X-Tenant-Key","apiKey":"key-9"}""",
                ServerVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["env"] = "evil/../prod" },
                Arguments = args,
                RequestBodyJson = JsonSerializer.SerializeToElement(new { count = 7 })
            });
            Assert.False(invalidVarRes.Success);
            Assert.Equal(BusinessSkillErrorCodes.SelectionViolation, invalidVarRes.ErrorCode);

            // 缺少 Security AND 组所需的第二凭据时拒绝调用
            var incompleteAuthRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mat",
                SnapshotId = "snap-1",
                Capability = opCap,
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = policy,
                AuthKind = BusinessAuthKind.Bearer,
                SecretPayloadJson = """{"token":"tok-1"}""",
                Arguments = args,
                RequestBodyJson = JsonSerializer.SerializeToElement(new { count = 7 })
            });
            Assert.False(incompleteAuthRes.Success);
            Assert.Equal(BusinessSkillErrorCodes.AuthRequired, incompleteAuthRes.ErrorCode);

            // 满足 AND 安全组合 (BearerAuth + TenantKey)，并且 Operation 级 server 优先级生效
            var okRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mat",
                SnapshotId = "snap-1",
                Capability = opCap,
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = policy,
                AuthKind = BusinessAuthKind.Bearer,
                SecretPayloadJson = """{"token":"tok-1","headerName":"X-Tenant-Key","apiKey":"key-9"}""",
                Arguments = args,
                RequestBodyJson = JsonSerializer.SerializeToElement(new { count = 7 })
            });
            Assert.True(okRes.Success, okRes.ErrorMessage);
            Assert.Equal(200, okRes.HttpStatusCode);
            Assert.Equal("Bearer tok-1", observedBearer);
            Assert.Equal("key-9", observedApiKey);
            Assert.Equal("corr-99", observedTraceHeader);
            Assert.Contains("/v2/tenants/t%2F01/items", observedRequestLine, StringComparison.Ordinal);
            Assert.Contains("tags=a%2Cb", observedRequestLine, StringComparison.Ordinal);
            Assert.Contains("filters=f1&filters=f2", observedRequestLine, StringComparison.Ordinal);
            Assert.Contains("\"count\":7", observedBody, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Invoke_RetryPolicy_ReadRetriesOn503And429_WhileWriteDisconnectReturnsOutcomeUnknownWithoutReplay()
    {
        var adapter = new OpenApiBusinessPluginAdapter(new OutboundTargetPolicyGuard());
        var readAttempts = 0;
        var writeAttempts = 0;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/api/items", (HttpContext ctx) =>
        {
            var current = Interlocked.Increment(ref readAttempts);
            if (current == 1)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (current == 2)
            {
                ctx.Response.Headers["Retry-After"] = "0";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            return Results.Json(new { ok = true, attempt = current });
        });

        app.MapPost("/api/items", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref writeAttempts);
            ctx.Abort();
            return Task.CompletedTask;
        });

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);

            var specJson = """
            {
              "openapi": "3.0.3",
              "info": { "title": "RetryAPI", "version": "1.0.0" },
              "paths": {
                "/api/items": {
                  "get": { "operationId": "getItems", "responses": { "200": { "description": "OK" } } },
                  "post": { "operationId": "postItem", "responses": { "201": { "description": "Created" } } }
                }
              }
            }
            """;

            var disc = await OpenApiBusinessPluginAdapter.DiscoverFromContentAsync("plg-ret", "snap-1", specJson);
            var readCap = disc.Capabilities.Single(c => c.Invocation.HttpMethod == "GET");
            var writeCap = disc.Capabilities.Single(c => c.Invocation.HttpMethod == "POST");
            var policy = new EndpointPolicyDto { AllowedOrigin = origin, AllowPlainHttpForIntranet = true };

            var readRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-ret",
                SnapshotId = "snap-1",
                Capability = readCap,
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy
            });

            Assert.True(readRes.Success, readRes.ErrorMessage);
            Assert.Equal(3, Volatile.Read(ref readAttempts));
            Assert.Equal(3, readRes.RequestsConsumed);
            Assert.Equal(2, readRes.RetriesConsumed);

            var writeRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-ret",
                SnapshotId = "snap-1",
                Capability = writeCap,
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = policy
            });

            Assert.False(writeRes.Success);
            Assert.True(writeRes.OutcomeUnknown);
            Assert.Equal(BusinessSkillErrorCodes.OutcomeUnknown, writeRes.ErrorCode);
            Assert.Equal(1, Volatile.Read(ref writeAttempts));
            Assert.Equal(1, writeRes.RequestsConsumed);
            Assert.Equal(0, writeRes.RetriesConsumed);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Executor_ResponseContractsAndBoundedPagination_HandleMaxBytes_204_BizCode_PageAndCursorLoop()
    {
        var guard = new OutboundTargetPolicyGuard();
        var executor = new BusinessCapabilityExecutor(new OpenApiBusinessPluginAdapter(guard), new McpBusinessPluginAdapter(guard));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/api/huge", async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var i = 0; i < 11; i++)
            {
                await ctx.Response.Body.WriteAsync(chunk);
            }
        });

        app.MapGet("/api/empty", () => Results.NoContent());
        app.MapGet("/api/biz-err", () => Results.Json(new { code = "ERR_LIMIT", message = "quota denied" }));

        app.MapGet("/api/paged", (int page) => page switch
        {
            1 => Results.Json(new { items = new[] { new { id = 1 }, new { id = 2 } }, hasMore = true }),
            2 => Results.Json(new { items = new[] { new { id = 3 } }, hasMore = false }),
            _ => Results.Json(new { items = Array.Empty<object>(), hasMore = false })
        });

        app.MapGet("/api/cursor-loop", (string? cursor) =>
            Results.Json(new
            {
                items = new[] { new { id = cursor ?? "init" } },
                nextCursor = "same-cursor"
            }));

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);

            var specJson = """
            {
              "openapi": "3.0.3",
              "info": { "title": "RespAPI", "version": "1.0.0" },
              "paths": {
                "/api/huge": { "get": { "operationId": "huge", "responses": { "200": { "description": "OK" } } } },
                "/api/empty": { "get": { "operationId": "empty", "responses": { "204": { "description": "No Content" } } } },
                "/api/biz-err": { "get": { "operationId": "bizErr", "responses": { "200": { "description": "OK" } } } },
                "/api/paged": {
                  "get": {
                    "operationId": "paged",
                    "parameters": [{ "name": "page", "in": "query", "schema": { "type": "integer" } }],
                    "responses": { "200": { "description": "OK" } }
                  }
                },
                "/api/cursor-loop": {
                  "get": {
                    "operationId": "cursorLoop",
                    "parameters": [{ "name": "cursor", "in": "query", "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """;

            var disc = await OpenApiBusinessPluginAdapter.DiscoverFromContentAsync("plg-resp", "snap-1", specJson);
            var policy = new EndpointPolicyDto { AllowedOrigin = origin, AllowPlainHttpForIntranet = true };

            var hugeRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/huge"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy
            });
            Assert.False(hugeRes.Success);
            Assert.Equal(BusinessSkillErrorCodes.BudgetExceeded, hugeRes.ErrorCode);

            var emptyRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/empty"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy
            });
            Assert.True(emptyRes.Success, emptyRes.ErrorMessage);
            Assert.Equal(204, emptyRes.HttpStatusCode);
            Assert.Null(emptyRes.PayloadJson);

            var bizErrRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/biz-err"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                ResponseContract = new SkillResponseContract
                {
                    BusinessCodePointer = "/code",
                    SuccessCodeValues = ["0", "OK"]
                }
            });
            Assert.False(bizErrRes.Success);
            Assert.Equal(BusinessSkillErrorCodes.ValidationFailed, bizErrRes.ErrorCode);

            var pagedComplete = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/paged"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                Pagination = new SkillPaginationConfig
                {
                    Mode = "page",
                    PageParam = "page",
                    ItemsPointer = "/items",
                    HasMorePointer = "/hasMore",
                    PageSize = 2,
                    MaxPages = 5
                }
            });
            Assert.True(pagedComplete.Success, pagedComplete.ErrorMessage);
            Assert.False(pagedComplete.PartialData);
            Assert.Equal(3, pagedComplete.CollectedItems.Count);
            Assert.Equal(2, pagedComplete.PagesFetched);

            var pagedMaxCap = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/paged"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                Pagination = new SkillPaginationConfig
                {
                    Mode = "page",
                    PageParam = "page",
                    ItemsPointer = "/items",
                    HasMorePointer = "/hasMore",
                    PageSize = 2,
                    MaxPages = 1
                }
            });
            Assert.False(pagedMaxCap.Success);
            Assert.True(pagedMaxCap.PartialData);
            Assert.Equal(BusinessSkillErrorCodes.PartialData, pagedMaxCap.ErrorCode);

            var loopRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-resp",
                SnapshotId = "snap-1",
                Capability = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/cursor-loop"),
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                Pagination = new SkillPaginationConfig
                {
                    Mode = "cursor",
                    CursorParam = "cursor",
                    ItemsPointer = "/items",
                    NextCursorPointer = "/nextCursor",
                    MaxPages = 5
                }
            });
            Assert.False(loopRes.Success);
            Assert.True(loopRes.PartialData);
            Assert.Equal(BusinessSkillErrorCodes.PartialData, loopRes.ErrorCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenApiAdapter_SkipsEmptyOptionalQueryAndHeaderParams_AndIncludesErrorBodySnippet()
    {
        var guard = new OutboundTargetPolicyGuard();
        var adapter = new OpenApiBusinessPluginAdapter(guard);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        string? observedQueryString = null;
        bool observedOptionalHeader = false;
        app.MapGet("/api/kb/list", (HttpRequest req) =>
        {
            observedQueryString = req.QueryString.Value;
            observedOptionalHeader = req.Headers.ContainsKey("X-Optional-Trace");
            if (req.Query.ContainsKey("tenantId") && string.IsNullOrEmpty(req.Query["tenantId"].ToString()))
            {
                return Results.NotFound(new { message = "Invalid empty tenantId" });
            }

            return Results.Json(new { items = new[] { "kb-1" } });
        });

        app.MapGet("/api/kb/missing", () =>
            Results.NotFound(new { code = "KB_NOT_FOUND", message = "Tenant knowledge base does not exist" }));

        await app.StartAsync();
        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);

            var specJson = $$"""
            {
              "openapi": "3.0.4",
              "info": { "title": "KB", "version": "1.0.0" },
              "paths": {
                "/api/kb/list": {
                  "get": {
                    "parameters": [
                      { "name": "tenantId", "in": "query", "required": false, "schema": { "type": "string", "format": "uuid" } },
                      { "name": "keyword", "in": "query", "required": false, "schema": { "type": "string" } },
                      { "name": "tags", "in": "query", "required": false, "schema": { "type": "array", "items": { "type": "string" } } },
                      { "name": "X-Optional-Trace", "in": "header", "required": false, "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "OK" } }
                  }
                },
                "/api/kb/missing": {
                  "get": {
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """;

            var disc = await OpenApiBusinessPluginAdapter.DiscoverFromContentAsync("plg-kb", "snap-1", specJson, "json");
            Assert.True(disc.Success);

            var policy = new EndpointPolicyDto
            {
                AllowedOrigin = origin,
                AllowPlainHttpForIntranet = true
            };

            var listCap = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/kb/list");
            var okRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-kb",
                SnapshotId = "snap-1",
                Capability = listCap,
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["tenantId"] = JsonSerializer.SerializeToElement(""),
                    ["keyword"] = JsonSerializer.SerializeToElement("规划"),
                    ["tags"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                    ["X-Optional-Trace"] = JsonSerializer.SerializeToElement("   ")
                }
            });

            Assert.True(okRes.Success, okRes.ErrorMessage);
            Assert.Equal("?keyword=%E8%A7%84%E5%88%92", observedQueryString);
            Assert.False(observedOptionalHeader);

            var missingCap = disc.Capabilities.Single(c => c.Invocation.NormalizedPath == "/api/kb/missing");
            var errRes = await adapter.InvokeSingleAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-kb",
                SnapshotId = "snap-1",
                Capability = missingCap,
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy
            });

            Assert.False(errRes.Success);
            Assert.Equal(404, errRes.HttpStatusCode);
            Assert.Contains("HTTP 404", errRes.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("KB_NOT_FOUND", errRes.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("Tenant knowledge base does not exist", errRes.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
