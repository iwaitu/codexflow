// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.Agents;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CodexFlow.Core.BusinessSkills;

public sealed record McpDiscoveryResult(
    bool Success,
    string? NegotiatedProtocolVersion,
    CatalogSnapshotDto? Snapshot,
    IReadOnlyList<CapabilityDetailDto> Capabilities,
    BusinessSkillErrorDto? Error);

public sealed record McpToolInvocationResult(
    bool Success,
    bool IsProtocolError,
    bool IsToolError,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? StructuredContent,
    IReadOnlyList<string> TextBlocks);

/// <summary>
/// P0 阶段 MCP 发现与调用原型门面，内部复用 <see cref="McpBusinessPluginAdapter"/> 锁定实现。
/// </summary>
public static class McpDiscoveryPrototype
{
    public static Task<McpDiscoveryResult> DiscoverToolsAsync(
        string pluginId,
        string snapshotId,
        Uri endpoint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);

        return McpBusinessPluginAdapter.DiscoverWithClientAsync(pluginId, snapshotId, endpoint, httpClient, loggerFactory, ct);
    }

    public static async Task<McpToolInvocationResult> CallToolAsync(
        Uri endpoint,
        HttpClient httpClient,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var client = await McpBusinessPluginAdapter.ConnectLockedClientAsync(endpoint, httpClient, loggerFactory, ct).ConfigureAwait(false);
        await using var clientScope = client;
        if (!string.Equals(client.NegotiatedProtocolVersion, BusinessSkillDefaults.LockedMcpProtocolVersion, StringComparison.Ordinal))
        {
            return new McpToolInvocationResult(
                false,
                true,
                false,
                BusinessSkillErrorCodes.UnsupportedProtocol,
                $"Negotiated MCP version '{client.NegotiatedProtocolVersion}' does not match '{BusinessSkillDefaults.LockedMcpProtocolVersion}'.",
                null,
                []);
        }

        var jsonArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            // Preserve the public CLR argument contract for types without generated metadata.
            jsonArgs[pair.Key] = ExternalToolArgumentJson.TryWriteElement(pair.Value, out var json)
                ? json
                : JsonSerializer.SerializeToElement(pair.Value);
        }

        var result = await client.CallToolAsync(
            new CallToolRequestParams
            {
                Name = toolName,
                Arguments = jsonArgs
            },
            ct).ConfigureAwait(false);

        var textBlocks = result.Content?
            .OfType<TextContentBlock>()
            .Select(static b => b.Text)
            .Where(static t => !string.IsNullOrEmpty(t))
            .ToArray() ?? [];

        JsonElement? structured = result.StructuredContent?.Clone();

        if (result.IsError == true)
        {
            return new McpToolInvocationResult(
                false,
                false,
                true,
                BusinessSkillErrorCodes.ValidationFailed,
                textBlocks.Length > 0 ? string.Join('\n', textBlocks) : "MCP tool reported isError=true.",
                structured,
                textBlocks);
        }

        return new McpToolInvocationResult(true, false, false, null, null, structured, textBlocks);
    }
}
