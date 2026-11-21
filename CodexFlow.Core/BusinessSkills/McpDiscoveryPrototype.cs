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
    private const string InvalidClrArguments =
        "MCP arguments must be JSON-compatible. Serialize custom CLR values to JsonElement and pass them to CallToolWithJsonArgumentsAsync.";

    private const string InvalidJsonArguments = "MCP arguments must contain valid, initialized JsonElement values.";

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

    /// <summary>Calls a tool with JSON-compatible CLR arguments supported by the generated serializer.</summary>
    public static async Task<McpToolInvocationResult> CallToolAsync(
        Uri endpoint,
        HttpClient httpClient,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ValidateInvocation(endpoint, httpClient, toolName);
        var jsonArgs = PrepareArguments(arguments, static value => value is JsonElement element
            ? element
            : ExternalToolArgumentJson.TryWriteElement(value, out var json) ? json : default, InvalidClrArguments);
        return await CallToolCoreAsync(endpoint, httpClient, toolName, jsonArgs, loggerFactory, ct).ConfigureAwait(false);
    }

    /// <summary>Calls a tool with JSON values prepared by the caller, including serialized DTOs.</summary>
    public static async Task<McpToolInvocationResult> CallToolWithJsonArgumentsAsync(
        Uri endpoint,
        HttpClient httpClient,
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ValidateInvocation(endpoint, httpClient, toolName);
        var jsonArgs = PrepareArguments(arguments, static value => value, InvalidJsonArguments);
        return await CallToolCoreAsync(endpoint, httpClient, toolName, jsonArgs, loggerFactory, ct).ConfigureAwait(false);
    }

    private static void ValidateInvocation(Uri endpoint, HttpClient httpClient, string toolName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
    }

    private static Dictionary<string, JsonElement> PrepareArguments<T>(
        IReadOnlyDictionary<string, T> arguments,
        Func<T, JsonElement> prepareValue,
        string invalidArgumentsMessage)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var jsonArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            if (name is null)
            {
                throw new ArgumentException("MCP argument names must be JSON object property names.", nameof(arguments));
            }

            try
            {
                var json = prepareValue(value);
                if (json.ValueKind != JsonValueKind.Undefined)
                {
                    jsonArgs[name] = json.Clone();
                    continue;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // Serialization errors can include details of argument values. Keep the public error independent of their contents.
            }

            throw new ArgumentException(invalidArgumentsMessage, nameof(arguments));
        }

        return jsonArgs;
    }

    private static async Task<McpToolInvocationResult> CallToolCoreAsync(
        Uri endpoint,
        HttpClient httpClient,
        string toolName,
        Dictionary<string, JsonElement> jsonArgs,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
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
