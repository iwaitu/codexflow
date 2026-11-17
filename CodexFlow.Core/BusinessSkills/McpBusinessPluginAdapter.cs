// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// P2 正式 MCP 远程 Streamable HTTP (锁定 2025-11-25) 适配器：
/// 接入 OutboundTargetPolicyGuard，禁用客户端 sampling/roots/elicitation 回调，
/// 区分传输/协议/isError 失败并严格执行只读有限重试与非只读 OutcomeUnknown 规则。
/// </summary>
public sealed class McpBusinessPluginAdapter
{
    private readonly OutboundTargetPolicyGuard _targetGuard;

    public McpBusinessPluginAdapter(OutboundTargetPolicyGuard targetGuard)
    {
        ArgumentNullException.ThrowIfNull(targetGuard);
        _targetGuard = targetGuard;
    }

    public async Task<McpDiscoveryResult> DiscoverAsync(
        string pluginId,
        string snapshotId,
        Uri endpoint,
        EndpointPolicyDto policy,
        BusinessAuthKind docAuthKind = BusinessAuthKind.None,
        string? docSecretJson = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(policy);

        var correlationId = Guid.NewGuid().ToString("N");
        var eval = await _targetGuard.ValidateAndResolveAsync(endpoint, policy, docAuthKind, OutboundRequestPurpose.Discovery, ct).ConfigureAwait(false);
        if (!eval.Allowed)
        {
            return new McpDiscoveryResult(
                false,
                null,
                null,
                [],
                new BusinessSkillErrorDto(eval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation, eval.Message ?? "Target endpoint denied by policy.", [], false, correlationId));
        }

        using var httpClient = _targetGuard.CreateGuardedHttpClient(policy, docAuthKind, OutboundRequestPurpose.Discovery);
        BusinessCapabilityResponseHelpers.ApplyDefaultAuthHeaders(httpClient, docAuthKind, docSecretJson);

        return await DiscoverWithClientAsync(pluginId, snapshotId, endpoint, httpClient, loggerFactory, ct).ConfigureAwait(false);
    }

    public async Task<CapabilityExecutionResult> InvokeSingleAsync(
        CapabilityExecutionRequest request,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolName = request.Capability.Invocation.McpToolName;
        if (request.Capability.Summary.Executability != CapabilityExecutabilityStatus.Executable ||
            string.IsNullOrWhiteSpace(toolName))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.UnsupportedSchema,
                ErrorMessage = $"MCP capability '{request.Capability.Summary.Id}' is not executable."
            };
        }

        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var endpoint))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.ValidationFailed,
                ErrorMessage = "MCP connection SourceUrl is invalid."
            };
        }

        var eval = await _targetGuard.ValidateAndResolveAsync(endpoint, request.EndpointPolicy, request.AuthKind, OutboundRequestPurpose.Execution, ct)
            .ConfigureAwait(false);
        if (!eval.Allowed)
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = eval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation,
                ErrorMessage = eval.Message ?? "MCP target endpoint denied by policy."
            };
        }

        var isReadOnly = request.EffectiveRisk == CapabilityRiskLevel.Read;
        var allowedRetries = isReadOnly ? Math.Clamp(request.MaxReadOnlyRetries, 0, BusinessSkillDefaults.MaxReadOnlyRetries) : 0;

        using var httpClient = _targetGuard.CreateGuardedHttpClient(
            request.EndpointPolicy,
            request.AuthKind,
            OutboundRequestPurpose.Execution,
            requestTimeout: TimeSpan.FromSeconds(Math.Clamp(request.RequestTimeoutSeconds, 1, 300)));
        BusinessCapabilityResponseHelpers.ApplyDefaultAuthHeaders(httpClient, request.AuthKind, request.SecretPayloadJson);

        var requestsConsumed = 0;
        var retriesConsumed = 0;

        for (var attempt = 0; attempt <= allowedRetries; attempt++)
        {
            if (requestsConsumed >= request.RemainingRequestBudget)
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    ErrorCode = BusinessSkillErrorCodes.BudgetExceeded,
                    ErrorMessage = "External request budget exhausted.",
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }

            requestsConsumed++;
            if (attempt > 0)
            {
                retriesConsumed++;
            }

            McpClient? client = null;
            try
            {
                client = await ConnectLockedClientAsync(endpoint, httpClient, loggerFactory, ct).ConfigureAwait(false);
                if (!string.Equals(client.NegotiatedProtocolVersion, BusinessSkillDefaults.LockedMcpProtocolVersion, StringComparison.Ordinal))
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        ErrorCode = BusinessSkillErrorCodes.UnsupportedProtocol,
                        ErrorMessage = $"Negotiated MCP version '{client.NegotiatedProtocolVersion}' does not match '{BusinessSkillDefaults.LockedMcpProtocolVersion}'.",
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                var callResult = await client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = toolName,
                        Arguments = request.Arguments.Count > 0
                            ? new Dictionary<string, JsonElement>(request.Arguments, StringComparer.Ordinal)
                            : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    },
                    ct).ConfigureAwait(false);

                var textBlocks = callResult.Content?
                    .OfType<TextContentBlock>()
                    .Select(static b => b.Text)
                    .Where(static t => !string.IsNullOrEmpty(t))
                    .ToArray() ?? [];

                JsonElement? structured = callResult.StructuredContent?.Clone();
                var rawText = textBlocks.Length > 0 ? string.Join('\n', textBlocks) : null;

                if (structured.HasValue)
                {
                    var byteSize = Encoding.UTF8.GetByteCount(structured.Value.GetRawText());
                    if (byteSize > BusinessSkillDefaults.MaxSingleResponseBytes)
                    {
                        return new CapabilityExecutionResult
                        {
                            Success = false,
                            ErrorCode = BusinessSkillErrorCodes.BudgetExceeded,
                            ErrorMessage = "MCP tool structured response exceeded 10 MiB limit.",
                            RequestsConsumed = requestsConsumed,
                            RetriesConsumed = retriesConsumed
                        };
                    }
                }

                if (callResult.IsError == true)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        IsToolError = true,
                        ErrorCode = BusinessSkillErrorCodes.ValidationFailed,
                        ErrorMessage = rawText ?? "MCP tool reported isError=true.",
                        PayloadJson = structured,
                        RawText = rawText,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                BusinessCapabilityResponseHelpers.EvaluateResponseContract(
                    structured,
                    request.ResponseContract,
                    out var contractSatisfied,
                    out var createdId,
                    out var contractFailure);

                if (!contractSatisfied)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        ErrorCode = BusinessSkillErrorCodes.ValidationFailed,
                        ErrorMessage = contractFailure,
                        PayloadJson = structured,
                        RawText = rawText,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                return new CapabilityExecutionResult
                {
                    Success = true,
                    PayloadJson = structured,
                    RawText = rawText,
                    CreatedId = createdId,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed,
                    PagesFetched = 1
                };
            }
            catch (UnsupportedProtocolVersionException ex)
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    ErrorCode = BusinessSkillErrorCodes.UnsupportedProtocol,
                    ErrorMessage = ex.Message,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            catch (McpException ex) when (ex.Message.Contains("protocol version", StringComparison.OrdinalIgnoreCase) ||
                                           ex.Message.Contains("sampling", StringComparison.OrdinalIgnoreCase) ||
                                           ex.Message.Contains("roots", StringComparison.OrdinalIgnoreCase) ||
                                           ex.Message.Contains("elicitation", StringComparison.OrdinalIgnoreCase))
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    ErrorCode = BusinessSkillErrorCodes.UnsupportedProtocol,
                    ErrorMessage = ex.Message,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        HttpStatusCode = (int)ex.StatusCode.Value,
                        ErrorCode = BusinessSkillErrorCodes.AuthRequired,
                        ErrorMessage = ex.Message,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                var isTransient = ex.StatusCode is not { } sc || BusinessCapabilityResponseHelpers.IsTransientHttpStatus((int)sc);
                if (isReadOnly && isTransient && attempt < allowedRetries)
                {
                    await Task.Delay(BusinessCapabilityResponseHelpers.ResolveRetryDelay(null, attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                return new CapabilityExecutionResult
                {
                    Success = false,
                    HttpStatusCode = (int?)ex.StatusCode ?? 0,
                    OutcomeUnknown = !isReadOnly,
                    ErrorCode = isReadOnly ? BusinessSkillErrorCodes.ConnectionUnavailable : BusinessSkillErrorCodes.OutcomeUnknown,
                    ErrorMessage = ex.Message,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (isReadOnly && attempt < allowedRetries)
                {
                    await Task.Delay(BusinessCapabilityResponseHelpers.ResolveRetryDelay(null, attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                return new CapabilityExecutionResult
                {
                    Success = false,
                    OutcomeUnknown = !isReadOnly,
                    ErrorCode = isReadOnly ? BusinessSkillErrorCodes.ConnectionUnavailable : BusinessSkillErrorCodes.OutcomeUnknown,
                    ErrorMessage = $"MCP request timed out: {ex.Message}",
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            catch (IOException ex)
            {
                if (isReadOnly && attempt < allowedRetries)
                {
                    await Task.Delay(BusinessCapabilityResponseHelpers.ResolveRetryDelay(null, attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                return new CapabilityExecutionResult
                {
                    Success = false,
                    OutcomeUnknown = !isReadOnly,
                    ErrorCode = isReadOnly ? BusinessSkillErrorCodes.ConnectionUnavailable : BusinessSkillErrorCodes.OutcomeUnknown,
                    ErrorMessage = $"MCP transport disconnected: {ex.Message}",
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            catch (McpException ex)
            {
                var transientStatus = ex.InnerException is HttpRequestException hre
                    && (!hre.StatusCode.HasValue || BusinessCapabilityResponseHelpers.IsTransientHttpStatus((int)hre.StatusCode.Value));
                var transientIo = ex.InnerException is IOException
                    || ex.Message.Contains("503", StringComparison.Ordinal)
                    || ex.Message.Contains("429", StringComparison.Ordinal)
                    || ex.Message.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase);

                if (isReadOnly && (transientStatus || transientIo) && attempt < allowedRetries)
                {
                    await Task.Delay(BusinessCapabilityResponseHelpers.ResolveRetryDelay(null, attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                return new CapabilityExecutionResult
                {
                    Success = false,
                    OutcomeUnknown = !isReadOnly && (transientStatus || transientIo),
                    ErrorCode = !isReadOnly && (transientStatus || transientIo)
                        ? BusinessSkillErrorCodes.OutcomeUnknown
                        : BusinessSkillErrorCodes.ConnectionUnavailable,
                    ErrorMessage = ex.Message,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            finally
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return new CapabilityExecutionResult
        {
            Success = false,
            ErrorCode = BusinessSkillErrorCodes.ConnectionUnavailable,
            ErrorMessage = "MCP call exhausted bounded retries.",
            RequestsConsumed = requestsConsumed,
            RetriesConsumed = retriesConsumed
        };
    }

    internal static async Task<McpDiscoveryResult> DiscoverWithClientAsync(
        string pluginId,
        string snapshotId,
        Uri endpoint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        McpClient client;
        try
        {
            client = await ConnectLockedClientAsync(endpoint, httpClient, loggerFactory, ct).ConfigureAwait(false);
        }
        catch (UnsupportedProtocolVersionException ex)
        {
            return new McpDiscoveryResult(
                false,
                ex.Requested,
                null,
                [],
                new BusinessSkillErrorDto(BusinessSkillErrorCodes.UnsupportedProtocol, ex.Message, [], false, correlationId));
        }
        catch (McpException ex) when (ex.Message.Contains("protocol version", StringComparison.OrdinalIgnoreCase))
        {
            return new McpDiscoveryResult(
                false,
                null,
                null,
                [],
                new BusinessSkillErrorDto(BusinessSkillErrorCodes.UnsupportedProtocol, ex.Message, [], false, correlationId));
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? BusinessSkillErrorCodes.AuthRequired
                : BusinessSkillErrorCodes.ConnectionUnavailable;
            return new McpDiscoveryResult(
                false,
                null,
                null,
                [],
                new BusinessSkillErrorDto(code, ex.Message, [], code == BusinessSkillErrorCodes.ConnectionUnavailable, correlationId));
        }

        await using var clientScope = client;
        var negotiatedVersion = client.NegotiatedProtocolVersion;

        if (!string.Equals(negotiatedVersion, BusinessSkillDefaults.LockedMcpProtocolVersion, StringComparison.Ordinal))
        {
            return new McpDiscoveryResult(
                false,
                negotiatedVersion,
                null,
                [],
                new BusinessSkillErrorDto(
                    BusinessSkillErrorCodes.UnsupportedProtocol,
                    $"MCP server negotiated protocol '{negotiatedVersion ?? "none"}', but v1 strictly requires '{BusinessSkillDefaults.LockedMcpProtocolVersion}'.",
                    [],
                    false,
                    correlationId));
        }

        var diagnostics = new List<string>();
        var capabilities = new List<CapabilityDetailDto>();
        var seenToolNames = new HashSet<string>(StringComparer.Ordinal);
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);

        string? cursor = null;
        var complete = true;

        do
        {
            ct.ThrowIfCancellationRequested();

            var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct).ConfigureAwait(false);
            if (page.Tools is not null)
            {
                foreach (var tool in page.Tools)
                {
                    if (capabilities.Count >= BusinessSkillDefaults.MaxCatalogCapabilities)
                    {
                        complete = false;
                        diagnostics.Add($"MCP tool catalog reached limit of {BusinessSkillDefaults.MaxCatalogCapabilities}.");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(tool.Name))
                    {
                        complete = false;
                        diagnostics.Add("Encountered MCP tool entry with empty name.");
                        continue;
                    }

                    if (!seenToolNames.Add(tool.Name))
                    {
                        complete = false;
                        diagnostics.Add($"Duplicate MCP tool name detected: '{tool.Name}'.");
                        continue;
                    }

                    capabilities.Add(ConvertToolToCapability(pluginId, snapshotId, tool));
                }
            }

            if (!complete)
            {
                break;
            }

            var nextCursor = page.NextCursor;
            if (string.IsNullOrEmpty(nextCursor))
            {
                cursor = null;
            }
            else if (!visitedCursors.Add(nextCursor))
            {
                complete = false;
                diagnostics.Add($"Cursor loop detected while paginating tools/list at cursor '{nextCursor}'.");
                break;
            }
            else
            {
                cursor = nextCursor;
            }
        } while (cursor is not null);

        var sourceHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(
            string.Join('\n', capabilities.Select(static c => $"{c.Summary.Id}|{c.InputSchemaJson ?? string.Empty}"))));

        var snapshot = new CatalogSnapshotDto
        {
            Id = snapshotId,
            PluginId = pluginId,
            SourceHash = sourceHash,
            SchemaHash = sourceHash,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            ProtocolVersion = negotiatedVersion!,
            Complete = complete,
            TotalCapabilities = capabilities.Count,
            ExecutableCount = capabilities.Count(static c => c.Summary.Executability == CapabilityExecutabilityStatus.Executable),
            NeedsConfigurationCount = 0,
            UnsupportedCount = capabilities.Count(static c => c.Summary.Executability == CapabilityExecutabilityStatus.Unsupported),
            Diagnostics = diagnostics
        };

        return new McpDiscoveryResult(true, negotiatedVersion, snapshot, capabilities, null);
    }

    internal static async Task<McpClient> ConnectLockedClientAsync(
        Uri endpoint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "CodexFlow.BusinessSkills.Mcp",
            ConnectionTimeout = TimeSpan.FromSeconds(BusinessSkillDefaults.ConnectTimeoutSeconds)
        };

        HttpClientTransport? transport = new HttpClientTransport(transportOptions, httpClient, loggerFactory, ownsHttpClient: false);
        try
        {
            // 显式关闭客户端 sampling/roots/elicitation 能力声明，禁止服务端反向回调模型或文件系统
            var clientOptions = new McpClientOptions
            {
                ProtocolVersion = BusinessSkillDefaults.LockedMcpProtocolVersion,
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation
                {
                    Name = "CodexFlow.BusinessSkills",
                    Version = "0.1.3"
                },
                InitializationTimeout = TimeSpan.FromSeconds(BusinessSkillDefaults.ConnectTimeoutSeconds)
            };

            var client = await McpClient.CreateAsync(transport, clientOptions, loggerFactory, ct).ConfigureAwait(false);
            transport = null;
            return client;
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static CapabilityDetailDto ConvertToolToCapability(string pluginId, string snapshotId, Tool tool)
    {
        var capabilityId = $"{pluginId}:MCP:{tool.Name}";
        var title = !string.IsNullOrWhiteSpace(tool.Title) ? tool.Title! : tool.Name;
        var descriptionMissing = string.IsNullOrWhiteSpace(tool.Description);
        var description = descriptionMissing ? "源接口未提供说明" : tool.Description!;

        CapabilityRiskLevel? hint = null;
        if (tool.Annotations?.DestructiveHint == true)
        {
            hint = CapabilityRiskLevel.Destructive;
        }
        else if (tool.Annotations?.ReadOnlyHint == true)
        {
            hint = CapabilityRiskLevel.Read;
        }

        var inputSchemaValid = tool.InputSchema.ValueKind == JsonValueKind.Object;
        var diagnostics = inputSchemaValid ? Array.Empty<string>() : ["MCP tool inputSchema is not a valid JSON Schema object."];

        return new CapabilityDetailDto
        {
            Summary = new CapabilitySummaryDto
            {
                Id = capabilityId,
                SnapshotId = snapshotId,
                NativeIdentity = tool.Name,
                ModelAlias = CreateShortAlias(tool.Name),
                Title = title,
                Description = description,
                SourceDescriptionMissing = descriptionMissing,
                GroupTag = "mcp-tools",
                Risk = CapabilityRiskLevel.Unknown,
                SuggestedSourceRiskHint = hint,
                Executability = inputSchemaValid ? CapabilityExecutabilityStatus.Executable : CapabilityExecutabilityStatus.Unsupported,
                Diagnostics = diagnostics
            },
            InputSchemaJson = tool.InputSchema.GetRawText(),
            OutputSchemaJson = tool.OutputSchema?.GetRawText(),
            Invocation = new CapabilityInvocationSpecDto
            {
                SourceType = BusinessPluginSourceType.Mcp,
                McpToolName = tool.Name,
                RequestContentType = "application/json",
                ResponseContentType = "application/json",
                ResponseSchemaJson = tool.OutputSchema?.GetRawText()
            }
        };
    }

    private static string CreateShortAlias(string toolName)
    {
        var sb = new StringBuilder(toolName.Length);
        foreach (var ch in toolName)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var prefix = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(prefix))
        {
            prefix = "mcp_tool";
        }

        if (prefix.Length > 40)
        {
            prefix = prefix[..40];
        }

        var hash = ComputeSha256Hex(Encoding.UTF8.GetBytes(toolName))[..6];
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}_{hash}");
    }

    private static string ComputeSha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
