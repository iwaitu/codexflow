// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P2McpAdapterTests
{
    [Fact]
    public async Task DiscoverAndInvoke_Locked20251125_AdvertisesBlankCapabilities_HandlesTransientReadRetry_WriteDisconnect_AndCursorPagination()
    {
        var inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "cursor": { "type": "string" },
            "fail": { "type": "boolean" }
          }
        }
        """).RootElement.Clone();

        ClientCapabilities? observedCapabilities = null;
        string? rawInitializeBody = null;
        var readToolHttpPostHits = 0;
        var writeToolHttpPostHits = 0;
        var failNextReadPost = 1;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ProtocolVersion = BusinessSkillDefaults.LockedMcpProtocolVersion;
                options.ServerInfo = new Implementation { Name = "P2McpTestServer", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithListToolsHandler((ctx, _) =>
            {
                observedCapabilities ??= ctx.Server.ClientCapabilities;
                if (string.IsNullOrEmpty(ctx.Params?.Cursor))
                {
                    return ValueTask.FromResult(new ListToolsResult
                    {
                        NextCursor = "page-2",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "read_items",
                                Title = "分页查询条目",
                                Description = "按游标读取业务条目",
                                InputSchema = inputSchema,
                                Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false }
                            }
                        ]
                    });
                }

                return ValueTask.FromResult(new ListToolsResult
                {
                    NextCursor = null,
                    Tools =
                    [
                        new Tool
                        {
                            Name = "write_record",
                            Title = "写入业务记录",
                            Description = "创建不可幂等重复的业务单据",
                            InputSchema = inputSchema,
                            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false }
                        },
                        new Tool
                        {
                            Name = "broken_schema_tool",
                            Description = "缺少合法 schema",
                            InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                        }
                    ]
                });
            })
            .WithCallToolHandler((ctx, _) =>
            {
                observedCapabilities ??= ctx.Server.ClientCapabilities;
                if (ctx.Params?.Name == "read_items")
                {
                    if (ctx.Params.Arguments is not null &&
                        ctx.Params.Arguments.TryGetValue("fail", out var failEl) &&
                        failEl.ValueKind == JsonValueKind.True)
                    {
                        return ValueTask.FromResult(new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "business rule violated" }]
                        });
                    }

                    var cur = ctx.Params.Arguments is not null &&
                              ctx.Params.Arguments.TryGetValue("cursor", out var cEl) &&
                              cEl.ValueKind == JsonValueKind.String
                        ? cEl.GetString()
                        : null;

                    var pageJson = cur == "c2"
                        ? JsonDocument.Parse("""{"items":[{"id":"r2"}],"nextCursor":null}""").RootElement.Clone()
                        : JsonDocument.Parse("""{"items":[{"id":"r1"}],"nextCursor":"c2"}""").RootElement.Clone();

                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = false,
                        StructuredContent = pageJson
                    });
                }

                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = false,
                    StructuredContent = JsonDocument.Parse("""{"createdId":"rec-1"}""").RootElement.Clone()
                });
            });

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal) &&
                HttpMethods.IsPost(ctx.Request.Method))
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;

                if (body.Contains("\"initialize\"", StringComparison.Ordinal))
                {
                    rawInitializeBody ??= body;
                }

                if (body.Contains("\"tools/call\"", StringComparison.Ordinal))
                {
                    if (body.Contains("\"read_items\"", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref readToolHttpPostHits);
                        if (Interlocked.Exchange(ref failNextReadPost, 0) == 1)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                            return;
                        }
                    }
                    else if (body.Contains("\"write_record\"", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref writeToolHttpPostHits);
                        ctx.Abort();
                        return;
                    }
                }
            }

            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var mcpUri = new Uri(baseUri, "/mcp");
            var origin = OutboundTargetPolicyGuard.GetOriginString(baseUri);
            var policy = new EndpointPolicyDto
            {
                AllowedOrigin = origin,
                AllowedPathPrefix = "/mcp",
                AllowPlainHttpForIntranet = true
            };

            var guard = new OutboundTargetPolicyGuard();
            var mcpAdapter = new McpBusinessPluginAdapter(guard);
            var executor = new BusinessCapabilityExecutor(new OpenApiBusinessPluginAdapter(guard), mcpAdapter);

            // 1. 发现目录 + 锁定协议版本 + 空白 ClientCapabilities 验证
            var disc = await mcpAdapter.DiscoverAsync("plg-mcp", "snap-1", mcpUri, policy);
            Assert.True(disc.Success, disc.Error?.Message);
            Assert.Equal(BusinessSkillDefaults.LockedMcpProtocolVersion, disc.NegotiatedProtocolVersion);
            Assert.Equal(3, disc.Capabilities.Count);

            Assert.False(string.IsNullOrWhiteSpace(rawInitializeBody));
            using (var initDoc = JsonDocument.Parse(rawInitializeBody!))
            {
                var caps = initDoc.RootElement.GetProperty("params").GetProperty("capabilities");
                Assert.False(caps.TryGetProperty("roots", out _));
                Assert.False(caps.TryGetProperty("sampling", out _));
                Assert.False(caps.TryGetProperty("elicitation", out _));
            }

            if (observedCapabilities is not null)
            {
                Assert.Null(observedCapabilities.Roots);
                Assert.Null(observedCapabilities.Sampling);
                Assert.Null(observedCapabilities.Elicitation);
            }

            var readCap = disc.Capabilities.Single(c => c.Invocation.McpToolName == "read_items");
            var writeCap = disc.Capabilities.Single(c => c.Invocation.McpToolName == "write_record");
            Assert.Equal(CapabilityRiskLevel.Read, readCap.Summary.SuggestedSourceRiskHint);
            Assert.Equal(CapabilityRiskLevel.Unknown, readCap.Summary.Risk);

            // 2. Read 工具遭遇首次 503 自动重试并完成 cursor 全分页聚合
            var pageRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mcp",
                SnapshotId = "snap-1",
                Capability = readCap,
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                SourceUrl = mcpUri.ToString(),
                Pagination = new SkillPaginationConfig
                {
                    Mode = "cursor",
                    CursorParam = "cursor",
                    ItemsPointer = "/items",
                    NextCursorPointer = "/nextCursor",
                    MaxPages = 5
                }
            });
            Assert.True(pageRes.Success, pageRes.ErrorMessage);
            Assert.False(pageRes.PartialData);
            Assert.Equal(2, pageRes.PagesFetched);
            Assert.Equal(2, pageRes.CollectedItems.Count);
            Assert.True(pageRes.RetriesConsumed >= 1);
            Assert.True(Volatile.Read(ref readToolHttpPostHits) >= 3);

            // 3. 工具业务错误 isError=true 明确失败
            var errToolRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mcp",
                SnapshotId = "snap-1",
                Capability = readCap,
                EffectiveRisk = CapabilityRiskLevel.Read,
                EndpointPolicy = policy,
                SourceUrl = mcpUri.ToString(),
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["fail"] = JsonSerializer.SerializeToElement(true)
                }
            });
            Assert.False(errToolRes.Success);
            Assert.True(errToolRes.IsToolError);

            // 4. Write 工具发送后断连不得自动重放，且必须返回 OutcomeUnknown
            var writeRes = await executor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = "plg-mcp",
                SnapshotId = "snap-1",
                Capability = writeCap,
                EffectiveRisk = CapabilityRiskLevel.Write,
                EndpointPolicy = policy,
                SourceUrl = mcpUri.ToString()
            });
            Assert.False(writeRes.Success);
            Assert.True(writeRes.OutcomeUnknown);
            Assert.Equal(1, Volatile.Read(ref writeToolHttpPostHits));
            Assert.Equal(0, writeRes.RetriesConsumed);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_UnsupportedEndpointResponse_FailsWithoutHang()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapPost("/bad-mcp", () => Results.BadRequest(new { error = "unsupported protocol version 2024-01-01" }));
        await app.StartAsync();

        try
        {
            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var badUri = new Uri(baseUri, "/bad-mcp");
            var adapter = new McpBusinessPluginAdapter(new OutboundTargetPolicyGuard());
            var res = await adapter.DiscoverAsync("plg-bad", "snap-1", badUri, new EndpointPolicyDto
            {
                AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(baseUri),
                AllowPlainHttpForIntranet = true
            });

            Assert.False(res.Success);
            Assert.NotNull(res.Error);
            Assert.Contains(res.Error!.Code, new[] { BusinessSkillErrorCodes.UnsupportedProtocol, BusinessSkillErrorCodes.ConnectionUnavailable });
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
