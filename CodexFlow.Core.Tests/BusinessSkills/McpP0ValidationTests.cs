// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class McpP0ValidationTests
{
    [Fact]
    public async Task EndToEndSample_StreamableHttp20251125_PaginatesTools_InvokesStructuredTool_AndDetectsIsError()
    {
        var schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "customerId": { "type": "string" }
          },
          "required": ["customerId"]
        }
        """).RootElement.Clone();

        var outputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "ticketCount": { "type": "integer" }
          }
        }
        """).RootElement.Clone();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services
            .AddMcpServer(options =>
            {
                options.ProtocolVersion = BusinessSkillDefaults.LockedMcpProtocolVersion;
                options.ServerInfo = new Implementation { Name = "SampleMcpServer", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithListToolsHandler((ctx, _) =>
            {
                var cursor = ctx.Params?.Cursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    return ValueTask.FromResult(new ListToolsResult
                    {
                        NextCursor = "page-2",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "query_overdue_tickets",
                                Title = "查询超时工单",
                                Description = "按客户 ID 查询当前超时处理工单",
                                InputSchema = schema,
                                OutputSchema = outputSchema,
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
                            Name = "create_escalation",
                            Title = "升级催办工单",
                            Description = "写入一条催办记录",
                            InputSchema = schema,
                            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }
                        }
                    ]
                });
            })
            .WithCallToolHandler((ctx, _) =>
            {
                var name = ctx.Params?.Name;
                if (string.Equals(name, "query_overdue_tickets", StringComparison.Ordinal))
                {
                    var customerId = ctx.Params?.Arguments is not null &&
                                     ctx.Params.Arguments.TryGetValue("customerId", out var arg)
                        ? arg.GetString()
                        : "unknown";

                    var structured = JsonDocument.Parse($$"""{"customerId":"{{customerId}}","ticketCount":2,"overdueDays":[34,41]}""").RootElement.Clone();
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = false,
                        StructuredContent = structured,
                        Content = [new TextContentBlock { Text = $"Found 2 overdue tickets for {customerId}." }]
                    });
                }

                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "Simulated business rejection: missing supervisor approval." }]
                });
            });

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var mcpEndpoint = new Uri(new Uri(baseUrl), "/mcp");
            using var httpClient = new HttpClient();

            var discovery = await McpDiscoveryPrototype.DiscoverToolsAsync("mcp-erp", "snap-mcp-1", mcpEndpoint, httpClient);

            Assert.True(discovery.Success);
            Assert.Equal(BusinessSkillDefaults.LockedMcpProtocolVersion, discovery.NegotiatedProtocolVersion);
            Assert.NotNull(discovery.Snapshot);
            Assert.True(discovery.Snapshot.Complete);
            Assert.Equal(2, discovery.Snapshot.TotalCapabilities);
            Assert.Equal(2, discovery.Capabilities.Count);

            var queryCap = discovery.Capabilities.Single(c => c.Summary.NativeIdentity == "query_overdue_tickets");
            Assert.Equal("mcp-erp:MCP:query_overdue_tickets", queryCap.Summary.Id);
            Assert.Equal(CapabilityRiskLevel.Unknown, queryCap.Summary.Risk);
            Assert.Equal(CapabilityRiskLevel.Read, queryCap.Summary.SuggestedSourceRiskHint);
            Assert.NotNull(queryCap.OutputSchemaJson);

            var escalateCap = discovery.Capabilities.Single(c => c.Summary.NativeIdentity == "create_escalation");
            Assert.Equal(CapabilityRiskLevel.Destructive, escalateCap.Summary.SuggestedSourceRiskHint);

            var okCall = await McpDiscoveryPrototype.CallToolAsync(
                mcpEndpoint,
                httpClient,
                "query_overdue_tickets",
                new Dictionary<string, object?> { ["customerId"] = "CUST-88" });

            Assert.True(okCall.Success);
            Assert.False(okCall.IsToolError);
            Assert.NotNull(okCall.StructuredContent);
            Assert.Equal(2, okCall.StructuredContent.Value.GetProperty("ticketCount").GetInt32());
            Assert.Contains("Found 2 overdue tickets for CUST-88.", okCall.TextBlocks);

            var errCall = await McpDiscoveryPrototype.CallToolAsync(
                mcpEndpoint,
                httpClient,
                "create_escalation",
                new Dictionary<string, object?> { ["customerId"] = "CUST-88" });

            Assert.False(errCall.Success);
            Assert.True(errCall.IsToolError);
            Assert.Equal(BusinessSkillErrorCodes.ValidationFailed, errCall.ErrorCode);
            Assert.Contains("missing supervisor approval", errCall.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task DiscoverTools_DetectsCursorLoop_AndMarksSnapshotIncomplete()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services
            .AddMcpServer(options =>
            {
                options.ProtocolVersion = BusinessSkillDefaults.LockedMcpProtocolVersion;
            })
            .WithHttpTransport()
            .WithListToolsHandler((ctx, _) => ValueTask.FromResult(new ListToolsResult
            {
                NextCursor = string.IsNullOrEmpty(ctx.Params?.Cursor) ? "loop-token" : "loop-token",
                Tools = string.IsNullOrEmpty(ctx.Params?.Cursor)
                    ? [new Tool { Name = "tool_a", Description = "A", InputSchema = schema }]
                    : []
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult()));

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var mcpEndpoint = new Uri(new Uri(baseUrl), "/mcp");
            using var httpClient = new HttpClient();

            var discovery = await McpDiscoveryPrototype.DiscoverToolsAsync("mcp-loop", "snap-loop", mcpEndpoint, httpClient);

            Assert.True(discovery.Success);
            Assert.NotNull(discovery.Snapshot);
            Assert.False(discovery.Snapshot.Complete);
            Assert.Contains(discovery.Snapshot.Diagnostics, d => d.Contains("Cursor loop detected", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task DiscoverTools_WhenServerNegotiatesNon20251125Protocol_ReturnsUnsupportedProtocolError()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services
            .AddMcpServer(options =>
            {
                options.ProtocolVersion = "2024-11-05";
            })
            .WithHttpTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = "legacy_tool", Description = "Legacy", InputSchema = schema }]
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult()));

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var mcpEndpoint = new Uri(new Uri(baseUrl), "/mcp");
            using var httpClient = new HttpClient();

            var discovery = await McpDiscoveryPrototype.DiscoverToolsAsync("mcp-legacy", "snap-legacy", mcpEndpoint, httpClient);

            Assert.False(discovery.Success);
            Assert.NotNull(discovery.Error);
            Assert.Equal(BusinessSkillErrorCodes.UnsupportedProtocol, discovery.Error.Code);
            Assert.Contains("2024-11-05", discovery.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
