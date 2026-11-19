// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// P2 正式 OpenAPI (Swagger 2.0 / OpenAPI 3.0.x / 3.1.x, JSON+YAML) 适配器：
/// 覆盖 Swagger UI 静态配置解析、受控外部 $ref 加载、Server 优先级与模板变量、
/// Security OR/AND 组合、参数 style/explode 序列化、10 MiB 响应限制及只读有限重试。
/// </summary>
public sealed partial class OpenApiBusinessPluginAdapter
{
    private static readonly HashSet<string> UnsupportedBodyContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multipart/form-data",
        "application/octet-stream",
        "application/x-www-form-urlencoded"
    };

    [GeneratedRegex(@"(?:""openapi""\s*:\s*""|^\s*openapi\s*:\s*['""]?)(3\.2(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Multiline, 500)]
    private static partial Regex UnsupportedOpenApi32Regex();

    [GeneratedRegex(@"^\s*(?:<!doctype\s+html|<html[\s>])", RegexOptions.IgnoreCase, 500)]
    private static partial Regex HtmlDocumentPrefixRegex();

    [GeneratedRegex(@"<input[^>]+type\s*=\s*['""]?password['""]?|<form[^>]+action\s*=\s*['""][^'""]*login", RegexOptions.IgnoreCase, 500)]
    private static partial Regex HtmlLoginFormRegex();

    [GeneratedRegex(@"<script[^>]+src\s*=\s*['""]([^'""]*swagger-initializer\.js[^'""]*)['""]", RegexOptions.IgnoreCase, 500)]
    private static partial Regex SwaggerInitializerScriptRegex();

    [GeneratedRegex(@"(?:configUrl|url)\s*:\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase, 500)]
    private static partial Regex SwaggerConfigPropertyRegex();

    private readonly OutboundTargetPolicyGuard _targetGuard;

    public OpenApiBusinessPluginAdapter(OutboundTargetPolicyGuard targetGuard)
    {
        ArgumentNullException.ThrowIfNull(targetGuard);
        _targetGuard = targetGuard;
    }

    public async Task<OpenApiDiscoveryResult> DiscoverFromUriAsync(
        string pluginId,
        string snapshotId,
        Uri documentUri,
        EndpointPolicyDto policy,
        BusinessAuthKind docAuthKind = BusinessAuthKind.None,
        string? docSecretJson = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(policy);

        var correlationId = Guid.NewGuid().ToString("N");
        var uriEval = await _targetGuard.ValidateAndResolveAsync(documentUri, policy, docAuthKind, OutboundRequestPurpose.Discovery, ct).ConfigureAwait(false);
        if (!uriEval.Allowed)
        {
            return FailedResult(uriEval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation, uriEval.Message ?? "Target URI blocked by policy.", correlationId);
        }

        using var httpClient = _targetGuard.CreateGuardedHttpClient(policy, docAuthKind, OutboundRequestPurpose.Discovery);
        BusinessCapabilityResponseHelpers.ApplyDefaultAuthHeaders(httpClient, docAuthKind, docSecretJson);

        var budget = new RefLoadBudgetState(BusinessSkillDefaults.MaxDescriptionBytes, BusinessSkillDefaults.MaxExternalRefFiles);
        var fetched = await FetchBoundedTextAsync(httpClient, documentUri, budget, ct).ConfigureAwait(false);
        if (!fetched.Success)
        {
            return FailedResult(fetched.ErrorCode!, fetched.ErrorMessage!, correlationId);
        }

        var activeUri = documentUri;
        var rawText = fetched.Text!;

        if (HtmlDocumentPrefixRegex().IsMatch(rawText))
        {
            var resolvedFromUi = await TryResolveSwaggerUiDocumentAsync(
                rawText,
                documentUri,
                httpClient,
                policy,
                docAuthKind,
                budget,
                ct).ConfigureAwait(false);

            if (!resolvedFromUi.Success)
            {
                return FailedResult(resolvedFromUi.ErrorCode!, resolvedFromUi.ErrorMessage!, correlationId);
            }

            activeUri = resolvedFromUi.ResolvedUri!;
            rawText = resolvedFromUi.SpecText!;
        }

        IStreamLoader? externalLoader = policy.AllowExternalOpenApiRefs
            ? new PolicyGuardedExternalRefLoader(_targetGuard, policy, docAuthKind, docSecretJson, activeUri, budget)
            : null;

        var discoveryRes = await DiscoverFromContentCoreAsync(
            pluginId,
            snapshotId,
            rawText,
            formatHint: null,
            baseUri: activeUri,
            allowExternalRefs: policy.AllowExternalOpenApiRefs,
            externalLoader: externalLoader,
            ct: ct).ConfigureAwait(false);

        if (budget.LastFailureCode is not null)
        {
            return FailedResult(budget.LastFailureCode, budget.LastFailureMessage!, correlationId);
        }

        return discoveryRes;
    }

    public static Task<OpenApiDiscoveryResult> DiscoverFromContentAsync(
        string pluginId,
        string snapshotId,
        string rawContent,
        string? formatHint = null,
        CancellationToken ct = default)
        => DiscoverFromContentCoreAsync(
            pluginId,
            snapshotId,
            rawContent,
            formatHint,
            baseUri: null,
            allowExternalRefs: false,
            externalLoader: null,
            ct: ct);

    public async Task<CapabilityExecutionResult> InvokeSingleAsync(
        CapabilityExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var spec = request.Capability.Invocation;
        if (request.Capability.Summary.Executability != CapabilityExecutabilityStatus.Executable ||
            string.IsNullOrWhiteSpace(spec.HttpMethod) ||
            string.IsNullOrWhiteSpace(spec.NormalizedPath))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.UnsupportedSchema,
                ErrorMessage = $"Capability '{request.Capability.Summary.Id}' is marked {request.Capability.Summary.Executability} and cannot be executed."
            };
        }

        // 1. 解析并校验 Server 地址与模板变量
        if (!TryResolveServerUri(request, spec, out var resolvedServerUri, out var serverErrCode, out var serverErrMsg))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = serverErrCode,
                ErrorMessage = serverErrMsg
            };
        }

        // 2. 按 style/explode 编译参数
        if (!TryBuildRequestComponents(spec, request.Arguments, resolvedServerUri, out var builtUri, out var extraHeaders, out var paramErrCode, out var paramErrMsg))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = paramErrCode,
                ErrorMessage = paramErrMsg
            };
        }

        // 3. 构造认证（检查 SecurityAlternatives OR/AND）并与出站策略核对
        using var probeReq = new HttpRequestMessage(new HttpMethod(spec.HttpMethod), builtUri);
        var authQueryParams = new List<(string Name, string Value)>();
        if (!BusinessCapabilityResponseHelpers.TryApplyConfiguredAuth(
                probeReq,
                authQueryParams,
                request.AuthKind,
                request.SecretPayloadJson,
                spec.SecurityAlternatives,
                out var authErr))
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.AuthRequired,
                ErrorMessage = authErr ?? "Authentication requirements are not satisfied."
            };
        }

        var finalUri = AppendQueryParameters(builtUri, authQueryParams);
        var policyEval = await _targetGuard.ValidateAndResolveAsync(finalUri, request.EndpointPolicy, request.AuthKind, OutboundRequestPurpose.Execution, ct)
            .ConfigureAwait(false);
        if (!policyEval.Allowed)
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = policyEval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation,
                ErrorMessage = policyEval.Message ?? "Target URI denied by endpoint policy."
            };
        }

        // 4. 校验并准备 Body
        string? bodyJsonText = null;
        if (request.RequestBodyJson.HasValue &&
            request.RequestBodyJson.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            if (!string.IsNullOrWhiteSpace(spec.RequestContentType) &&
                !string.Equals(spec.RequestContentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    ErrorCode = BusinessSkillErrorCodes.UnsupportedSchema,
                    ErrorMessage = $"Request content-type '{spec.RequestContentType}' is not supported in v1."
                };
            }

            bodyJsonText = request.RequestBodyJson.Value.GetRawText();
        }

        // 5. 执行并应用严格只读重试规则（Write/Unknown 绝对不自动重试）
        var isReadOnly = request.EffectiveRisk == CapabilityRiskLevel.Read;
        var allowedRetries = isReadOnly ? Math.Clamp(request.MaxReadOnlyRetries, 0, BusinessSkillDefaults.MaxReadOnlyRetries) : 0;
        using var httpClient = _targetGuard.CreateGuardedHttpClient(
            request.EndpointPolicy,
            request.AuthKind,
            OutboundRequestPurpose.Execution,
            requestTimeout: TimeSpan.FromSeconds(Math.Clamp(request.RequestTimeoutSeconds, 1, 300)));

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

            using var attemptReq = new HttpRequestMessage(new HttpMethod(spec.HttpMethod), finalUri);
            if (probeReq.Headers.Authorization is not null)
            {
                attemptReq.Headers.Authorization = probeReq.Headers.Authorization;
            }

            foreach (var h in probeReq.Headers.Where(static x => !string.Equals(x.Key, "Authorization", StringComparison.OrdinalIgnoreCase)))
            {
                attemptReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            foreach (var h in extraHeaders)
            {
                attemptReq.Headers.TryAddWithoutValidation(h.Name, h.Value);
            }

            if (bodyJsonText is not null)
            {
                attemptReq.Content = new StringContent(bodyJsonText, Encoding.UTF8, "application/json");
            }

            requestsConsumed++;
            if (attempt > 0)
            {
                retriesConsumed++;
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await httpClient.SendAsync(attemptReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is >= 300 and <= 399)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        HttpStatusCode = status,
                        ErrorCode = BusinessSkillErrorCodes.SelectionViolation,
                        ErrorMessage = $"HTTP redirect ({status}) blocked by endpoint security policy.",
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                if (BusinessCapabilityResponseHelpers.IsTransientHttpStatus(status) && isReadOnly && attempt < allowedRetries)
                {
                    var delay = BusinessCapabilityResponseHelpers.ResolveRetryDelay(response, attempt + 1);
                    response.Dispose();
                    response = null;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var boundedBody = await BusinessCapabilityResponseHelpers.ReadBoundedContentAsync(
                    response,
                    BusinessSkillDefaults.MaxSingleResponseBytes,
                    ct).ConfigureAwait(false);

                if (!boundedBody.WithinLimit)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        HttpStatusCode = status,
                        ErrorCode = BusinessSkillErrorCodes.BudgetExceeded,
                        ErrorMessage = boundedBody.ErrorMessage,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                var rawText = boundedBody.Bytes.Length == 0 ? null : Encoding.UTF8.GetString(boundedBody.Bytes);
                JsonElement? parsedJson = TryParseJsonPayload(rawText);

                if (!response.IsSuccessStatusCode)
                {
                    var errCode = status switch
                    {
                        401 or 403 => BusinessSkillErrorCodes.AuthRequired,
                        429 => BusinessSkillErrorCodes.BudgetExceeded,
                        _ => BusinessSkillErrorCodes.ConnectionUnavailable
                    };

                    var errorSnippet = FormatErrorResponseSnippet(rawText);
                    var errorMessage = string.IsNullOrEmpty(errorSnippet)
                        ? $"HTTP {status}: {response.ReasonPhrase}"
                        : $"HTTP {status}: {response.ReasonPhrase} - {errorSnippet}";

                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        HttpStatusCode = status,
                        ErrorCode = errCode,
                        ErrorMessage = errorMessage,
                        PayloadJson = parsedJson,
                        RawText = rawText,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                BusinessCapabilityResponseHelpers.EvaluateResponseContract(
                    parsedJson,
                    request.ResponseContract,
                    out var contractSatisfied,
                    out var createdId,
                    out var contractFailure);

                if (!contractSatisfied)
                {
                    return new CapabilityExecutionResult
                    {
                        Success = false,
                        HttpStatusCode = status,
                        ErrorCode = BusinessSkillErrorCodes.ValidationFailed,
                        ErrorMessage = contractFailure,
                        PayloadJson = parsedJson,
                        RawText = rawText,
                        RequestsConsumed = requestsConsumed,
                        RetriesConsumed = retriesConsumed
                    };
                }

                return new CapabilityExecutionResult
                {
                    Success = true,
                    HttpStatusCode = status,
                    PayloadJson = parsedJson,
                    RawText = rawText,
                    CreatedId = createdId,
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed,
                    PagesFetched = 1
                };
            }
            catch (HttpRequestException ex)
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
                    ErrorMessage = $"HTTP request timed out: {ex.Message}",
                    RequestsConsumed = requestsConsumed,
                    RetriesConsumed = retriesConsumed
                };
            }
            finally
            {
                response?.Dispose();
            }
        }

        return new CapabilityExecutionResult
        {
            Success = false,
            ErrorCode = BusinessSkillErrorCodes.ConnectionUnavailable,
            ErrorMessage = "Exhausted bounded retries without receiving a valid response.",
            RequestsConsumed = requestsConsumed,
            RetriesConsumed = retriesConsumed
        };
    }

    private static async Task<OpenApiDiscoveryResult> DiscoverFromContentCoreAsync(
        string pluginId,
        string snapshotId,
        string rawContent,
        string? formatHint,
        Uri? baseUri,
        bool allowExternalRefs,
        IStreamLoader? externalLoader,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(rawContent);

        var correlationId = Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes(rawContent);
        if (bytes.Length > BusinessSkillDefaults.MaxDescriptionBytes)
        {
            return FailedResult(BusinessSkillErrorCodes.BudgetExceeded,
                $"OpenAPI document exceeds {BusinessSkillDefaults.MaxDescriptionBytes} bytes limit.", correlationId);
        }

        if (HtmlDocumentPrefixRegex().IsMatch(rawContent))
        {
            return FailedResult(BusinessSkillErrorCodes.AuthRequired,
                "Target URL returned an HTML document (for example a login page or unconfigured Swagger UI) instead of an OpenAPI JSON/YAML document.", correlationId);
        }

        var v32Match = UnsupportedOpenApi32Regex().Match(rawContent);
        if (v32Match.Success)
        {
            return FailedResult(BusinessSkillErrorCodes.UnsupportedProtocol,
                $"OpenAPI specification version '{v32Match.Groups[1].Value}' is not supported in v1 (supported: 2.0, 3.0.x, 3.1.x).", correlationId);
        }

        var sourceHash = ComputeSha256Hex(bytes);
        var trimmed = rawContent.AsSpan().TrimStart();
        var effectiveFormat = string.IsNullOrWhiteSpace(formatHint)
            ? (trimmed.Length > 0 && trimmed[0] == '{' ? "json" : "yaml")
            : formatHint.Trim().ToLowerInvariant();

        var settings = new OpenApiReaderSettings
        {
            LoadExternalRefs = allowExternalRefs && externalLoader is not null,
            CustomExternalLoader = externalLoader,
            BaseUrl = baseUri,
            LeaveStreamOpen = true
        };
        settings.AddYamlReader(new OpenApiYamlReaderSettings
        {
            MaxDepth = (uint)BusinessSkillDefaults.MaxParseDepth,
            MaxInputByteCount = (uint)BusinessSkillDefaults.MaxDescriptionBytes
        });

        ReadResult readResult;
        try
        {
            using var stream = new MemoryStream(bytes);
            readResult = await OpenApiDocument.LoadAsync(stream, effectiveFormat, settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OpenApiReaderException)
        {
            return FailedResult(BusinessSkillErrorCodes.UnsupportedSchema, ex.Message, correlationId);
        }

        if (readResult.Document is null)
        {
            var errMsg = readResult.Diagnostic?.Errors is { Count: > 0 }
                ? string.Join("; ", readResult.Diagnostic.Errors.Select(static e => e.Message))
                : "Unable to parse OpenAPI document.";
            return FailedResult(BusinessSkillErrorCodes.UnsupportedSchema, errMsg, correlationId);
        }

        var doc = readResult.Document;
        var specVersion = readResult.Diagnostic?.SpecificationVersion switch
        {
            OpenApiSpecVersion.OpenApi2_0 => "2.0",
            OpenApiSpecVersion.OpenApi3_0 => "3.0",
            OpenApiSpecVersion.OpenApi3_1 => "3.1",
            _ => "unknown"
        };

        var diagnostics = new List<string>();
        if (readResult.Diagnostic?.Errors is { Count: > 0 })
        {
            foreach (var err in readResult.Diagnostic.Errors)
            {
                diagnostics.Add($"parse_error: {err.Message}");
            }
        }

        var docServers = doc.Servers?
            .Where(static s => !string.IsNullOrWhiteSpace(s.Url))
            .ToArray() ?? [];
        var docServerUrls = docServers.Select(static s => s.Url!).ToArray();

        var capabilities = new List<CapabilityDetailDto>();
        var usedAliases = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        if (doc.Paths is not null)
        {
            foreach (var pathEntry in doc.Paths)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedPath = NormalizePath(pathEntry.Key);
                var pathItem = pathEntry.Value;
                if (pathItem?.Operations is null)
                {
                    continue;
                }

                foreach (var opEntry in pathItem.Operations)
                {
                    if (capabilities.Count >= BusinessSkillDefaults.MaxCatalogCapabilities)
                    {
                        truncated = true;
                        diagnostics.Add($"Catalog capability count reached the limit of {BusinessSkillDefaults.MaxCatalogCapabilities}.");
                        break;
                    }

                    var method = opEntry.Key.Method.ToUpperInvariant();
                    var operation = opEntry.Value;
                    var detail = BuildCapabilityDetail(
                        pluginId,
                        snapshotId,
                        method,
                        normalizedPath,
                        doc,
                        pathItem,
                        operation,
                        usedAliases);
                    capabilities.Add(detail);
                }

                if (truncated)
                {
                    break;
                }
            }
        }

        var executableCount = capabilities.Count(static c => c.Summary.Executability == CapabilityExecutabilityStatus.Executable);
        var needsConfigCount = capabilities.Count(static c => c.Summary.Executability == CapabilityExecutabilityStatus.NeedsConfiguration);
        var unsupportedCount = capabilities.Count(static c => c.Summary.Executability == CapabilityExecutabilityStatus.Unsupported);

        var schemaHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(
            string.Join('\n', capabilities.Select(static c => $"{c.Summary.Id}|{c.Summary.Executability}|{c.Invocation.HttpMethod}|{c.Invocation.NormalizedPath}"))));

        var snapshot = new CatalogSnapshotDto
        {
            Id = snapshotId,
            PluginId = pluginId,
            SourceHash = sourceHash,
            SchemaHash = schemaHash,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            ProtocolVersion = specVersion,
            Complete = !truncated,
            TotalCapabilities = capabilities.Count,
            ExecutableCount = executableCount,
            NeedsConfigurationCount = needsConfigCount,
            UnsupportedCount = unsupportedCount,
            Diagnostics = diagnostics
        };

        return new OpenApiDiscoveryResult(true, snapshot, capabilities, null, docServerUrls);
    }

    private static CapabilityDetailDto BuildCapabilityDetail(
        string pluginId,
        string snapshotId,
        string method,
        string normalizedPath,
        OpenApiDocument doc,
        IOpenApiPathItem pathItem,
        OpenApiOperation operation,
        HashSet<string> usedAliases)
    {
        var capabilityId = $"{pluginId}:{method}:{normalizedPath}";
        var nativeIdentity = $"{method} {normalizedPath}";
        var modelAlias = CreateUniqueModelAlias(operation.OperationId, method, normalizedPath, usedAliases);

        var title = !string.IsNullOrWhiteSpace(operation.Summary)
            ? operation.Summary.Trim()
            : (!string.IsNullOrWhiteSpace(operation.OperationId) ? operation.OperationId.Trim() : nativeIdentity);

        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(operation.Summary))
        {
            descriptionParts.Add(operation.Summary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(operation.Description) &&
            !string.Equals(operation.Summary?.Trim(), operation.Description.Trim(), StringComparison.Ordinal))
        {
            descriptionParts.Add(operation.Description.Trim());
        }

        var descriptionMissing = descriptionParts.Count == 0;
        var description = descriptionMissing ? "源接口未提供说明" : string.Join("\n\n", descriptionParts);
        var groupTag = operation.Tags?.FirstOrDefault()?.Name ?? "default";

        var diagnostics = new List<string>();
        var executability = CapabilityExecutabilityStatus.Executable;

        // Server 优先级：Operation -> PathItem -> Document
        var effectiveServer = operation.Servers?.FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s.Url))
                              ?? pathItem.Servers?.FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s.Url))
                              ?? doc.Servers?.FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s.Url));
        var selectedServerUrl = effectiveServer?.Url ?? string.Empty;
        var serverVars = ExtractServerVariables(effectiveServer);

        // Security 优先级：Operation 声明时整体覆盖 Document 级声明
        var effectiveSecurity = operation.Security is { Count: > 0 }
            ? operation.Security
            : doc.Security;
        var (securityAlternatives, authSchemeRefs) = ExtractSecurityAlternatives(effectiveSecurity, diagnostics, ref executability);

        var mergedParameters = MergeParameters(pathItem.Parameters, operation.Parameters);

        if (operation.Callbacks is { Count: > 0 })
        {
            executability = CapabilityExecutabilityStatus.Unsupported;
            diagnostics.Add("Callbacks/webhooks are not supported in v1.");
        }

        string? requestContentType = null;
        string? requestBodySchemaJson = null;
        if (operation.RequestBody?.Content is { Count: > 0 } bodyContents)
        {
            if (bodyContents.TryGetValue("application/json", out var jsonMedia))
            {
                requestContentType = "application/json";
                requestBodySchemaJson = SerializeSchemaJson(jsonMedia.Schema);
                if (HasComplexUnionSchema(jsonMedia.Schema))
                {
                    executability = CapabilityExecutabilityStatus.Unsupported;
                    diagnostics.Add("Request body uses unsupported union schema (oneOf/anyOf).");
                }
                else if (HasUnresolvedExternalRef(jsonMedia.Schema) && executability == CapabilityExecutabilityStatus.Executable)
                {
                    executability = CapabilityExecutabilityStatus.NeedsConfiguration;
                    diagnostics.Add("Request body contains an unresolved external $ref (enable AllowExternalOpenApiRefs or check external target).");
                }
            }
            else
            {
                requestContentType = bodyContents.Keys.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(requestContentType) && UnsupportedBodyContentTypes.Contains(requestContentType))
                {
                    executability = CapabilityExecutabilityStatus.Unsupported;
                    diagnostics.Add($"Request content-type '{requestContentType}' is not supported in v1.");
                }
                else
                {
                    executability = CapabilityExecutabilityStatus.NeedsConfiguration;
                    diagnostics.Add($"Non-standard request content-type '{requestContentType}' requires review.");
                }
            }
        }

        var (responseContentType, responseSchemaJson, responseHasUnresolvedExtRef) = ExtractSuccessResponseSchema(operation.Responses);
        if (responseHasUnresolvedExtRef && executability == CapabilityExecutabilityStatus.Executable)
        {
            executability = CapabilityExecutabilityStatus.NeedsConfiguration;
            diagnostics.Add("Response schema contains an unresolved external $ref (enable AllowExternalOpenApiRefs or check external target).");
        }

        foreach (var param in mergedParameters)
        {
            if (param.In == ParameterLocation.Cookie)
            {
                executability = CapabilityExecutabilityStatus.Unsupported;
                diagnostics.Add($"Cookie parameter '{param.Name}' is not supported in v1.");
            }
            else if (param.Style is { } style && !IsSupportedStyle(param.In, style))
            {
                executability = CapabilityExecutabilityStatus.Unsupported;
                diagnostics.Add($"Parameter '{param.Name}' serialization style '{style}' is not supported in v1.");
            }

            if (HasComplexUnionSchema(param.Schema))
            {
                executability = CapabilityExecutabilityStatus.Unsupported;
                diagnostics.Add($"Parameter '{param.Name}' uses unsupported oneOf/anyOf schema.");
            }
            else if (HasUnresolvedExternalRef(param.Schema) && executability == CapabilityExecutabilityStatus.Executable)
            {
                executability = CapabilityExecutabilityStatus.NeedsConfiguration;
                diagnostics.Add($"Parameter '{param.Name}' contains an unresolved external $ref.");
            }
        }

        var paramDtos = mergedParameters.Select(static p => new CapabilityParameterSpecDto
        {
            Name = p.Name ?? string.Empty,
            Location = p.In?.ToString().ToLowerInvariant() ?? "query",
            SchemaType = p.Schema?.Type?.ToString().ToLowerInvariant() ?? "string",
            Required = p.Required,
            Style = p.Style?.ToString().ToLowerInvariant(),
            Explode = p.Explode,
            Description = p.Description
        }).ToArray();

        var suggestedHint = method is "GET" or "HEAD"
            ? CapabilityRiskLevel.Read
            : (method is "DELETE" ? CapabilityRiskLevel.Destructive : CapabilityRiskLevel.Write);

        return new CapabilityDetailDto
        {
            Summary = new CapabilitySummaryDto
            {
                Id = capabilityId,
                SnapshotId = snapshotId,
                NativeIdentity = nativeIdentity,
                ModelAlias = modelAlias,
                Title = title,
                Description = description,
                SourceDescriptionMissing = descriptionMissing,
                GroupTag = groupTag,
                Risk = CapabilityRiskLevel.Unknown,
                SuggestedSourceRiskHint = suggestedHint,
                Executability = executability,
                Diagnostics = diagnostics
            },
            InputSchemaJson = BuildCompositeInputSchemaJson(paramDtos, requestBodySchemaJson),
            OutputSchemaJson = responseSchemaJson,
            Invocation = new CapabilityInvocationSpecDto
            {
                SourceType = BusinessPluginSourceType.OpenApi,
                HttpMethod = method,
                NormalizedPath = normalizedPath,
                SelectedServerUrl = selectedServerUrl,
                Parameters = paramDtos,
                RequestContentType = requestContentType,
                ResponseContentType = responseContentType,
                AuthSchemeRefs = authSchemeRefs,
                SecurityAlternatives = securityAlternatives,
                RequestBodySchemaJson = requestBodySchemaJson,
                ResponseSchemaJson = responseSchemaJson,
                ServerVariableAllowedValues = serverVars
            }
        };
    }

    private static (IReadOnlyList<CapabilitySecurityRequirementDto> Alternatives, IReadOnlyList<string> SchemeRefs)
        ExtractSecurityAlternatives(
            IList<OpenApiSecurityRequirement>? requirements,
            List<string> diagnostics,
            ref CapabilityExecutabilityStatus executability)
    {
        if (requirements is null || requirements.Count == 0)
        {
            return ([], []);
        }

        var alts = new List<CapabilitySecurityRequirementDto>(requirements.Count);
        var refs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var req in requirements)
        {
            var group = new List<CapabilitySecuritySchemeRequirementDto>(req.Count);
            foreach (var pair in req)
            {
                var schemeRef = pair.Key;
                var schemeId = schemeRef.Reference?.Id ?? schemeRef.Name ?? schemeRef.Scheme ?? "auth";
                refs.Add(schemeId);

                if (schemeRef.UnresolvedReference)
                {
                    diagnostics.Add($"Security scheme '{schemeId}' could not be resolved.");
                    if (executability == CapabilityExecutabilityStatus.Executable)
                    {
                        executability = CapabilityExecutabilityStatus.NeedsConfiguration;
                    }
                }

                var schemeTypeStr = schemeRef.Type?.ToString() ?? "Unknown";
                var inStr = schemeRef.In?.ToString().ToLowerInvariant();
                if (schemeRef.Type == SecuritySchemeType.MutualTLS)
                {
                    diagnostics.Add($"MutualTLS security scheme '{schemeId}' requires external gateway or custom CA configuration.");
                    executability = CapabilityExecutabilityStatus.Unsupported;
                }

                group.Add(new CapabilitySecuritySchemeRequirementDto(
                    schemeId,
                    schemeTypeStr,
                    inStr,
                    schemeRef.Name,
                    schemeRef.Scheme,
                    pair.Value?.ToArray() ?? []));
            }

            alts.Add(new CapabilitySecurityRequirementDto(group));
        }

        return (alts, refs.ToArray());
    }

    private static Dictionary<string, IReadOnlyList<string>> ExtractServerVariables(OpenApiServer? server)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (server?.Variables is null)
        {
            return dict;
        }

        foreach (var pair in server.Variables)
        {
            var allowed = new List<string>();
            if (pair.Value.Enum is { Count: > 0 })
            {
                allowed.AddRange(pair.Value.Enum);
            }
            else if (!string.IsNullOrWhiteSpace(pair.Value.Default))
            {
                allowed.Add(pair.Value.Default);
            }

            dict[pair.Key] = allowed;
        }

        return dict;
    }

    private static bool IsSupportedStyle(ParameterLocation? location, ParameterStyle style)
        => location switch
        {
            ParameterLocation.Path => style == ParameterStyle.Simple,
            ParameterLocation.Query => style == ParameterStyle.Form,
            ParameterLocation.Header => style == ParameterStyle.Simple,
            _ => false
        };

    private static (string? ContentType, string? SchemaJson, bool HasUnresolvedExternalRef) ExtractSuccessResponseSchema(OpenApiResponses? responses)
    {
        if (responses is null)
        {
            return (null, null, false);
        }

        foreach (var code in new[] { "200", "201", "202", "204", "default" })
        {
            if (responses.TryGetValue(code, out var resp))
            {
                if (resp.Content is null || resp.Content.Count == 0)
                {
                    if (code == "204")
                    {
                        return ("empty", null, false);
                    }

                    continue;
                }

                if (resp.Content.TryGetValue("application/json", out var jsonMedia))
                {
                    return ("application/json", SerializeSchemaJson(jsonMedia.Schema), HasUnresolvedExternalRef(jsonMedia.Schema));
                }

                var first = resp.Content.First();
                return (first.Key, SerializeSchemaJson(first.Value.Schema), HasUnresolvedExternalRef(first.Value.Schema));
            }
        }

        return (null, null, false);
    }

    private static string? SerializeSchemaJson(IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        // 若 schema 为已解析的引用，优先序列化目标 Schema 结构以便上层表单与契约校验直接使用字段定义
        var effective = schema;
        var hops = 0;
        while (effective is OpenApiSchemaReference schemaRef && schemaRef.Target is not null && hops++ < 8)
        {
            effective = schemaRef.Target;
        }

        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new OpenApiJsonWriter(sw);
        effective.SerializeAsV31(writer);
        sw.Flush();
        return sw.ToString();
    }

    private static string BuildCompositeInputSchemaJson(
        IReadOnlyList<CapabilityParameterSpecDto> parameters,
        string? requestBodySchemaJson)
    {
        var model = new
        {
            parameters,
            hasJsonBody = requestBodySchemaJson is not null
        };
        return JsonSerializer.Serialize(model);
    }

    private static bool TryResolveServerUri(
        CapabilityExecutionRequest request,
        CapabilityInvocationSpecDto spec,
        out Uri resolvedBaseUri,
        out string? errorCode,
        out string? errorMessage)
    {
        resolvedBaseUri = default!;
        errorCode = null;
        errorMessage = null;

        var rawServer = !string.IsNullOrWhiteSpace(request.SelectedServerUrl)
            ? request.SelectedServerUrl.Trim()
            : (!string.IsNullOrWhiteSpace(spec.SelectedServerUrl)
                ? spec.SelectedServerUrl.Trim()
                : request.EndpointPolicy.AllowedOrigin);

        // 模板变量只能从经确认的配置/允许枚举中取值，防止注入任意 URL 字符
        if (rawServer.Contains('{', StringComparison.Ordinal))
        {
            foreach (var varEntry in spec.ServerVariableAllowedValues)
            {
                var token = "{" + varEntry.Key + "}";
                if (!rawServer.Contains(token, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!request.ServerVariables.TryGetValue(varEntry.Key, out var supplied) || string.IsNullOrWhiteSpace(supplied))
                {
                    supplied = varEntry.Value.Count > 0 ? varEntry.Value[0] : null;
                }

                if (string.IsNullOrWhiteSpace(supplied) ||
                    (varEntry.Value.Count > 0 && !varEntry.Value.Contains(supplied, StringComparer.Ordinal)))
                {
                    errorCode = BusinessSkillErrorCodes.SelectionViolation;
                    errorMessage = $"Server template variable '{varEntry.Key}' value '{supplied ?? "(missing)"}' is not in allowed configuration values.";
                    return false;
                }

                if (supplied.Contains('/', StringComparison.Ordinal) || supplied.Contains("..", StringComparison.Ordinal))
                {
                    errorCode = BusinessSkillErrorCodes.SelectionViolation;
                    errorMessage = $"Server template variable '{varEntry.Key}' contains forbidden path characters.";
                    return false;
                }

                rawServer = rawServer.Replace(token, supplied, StringComparison.Ordinal);
            }

            if (rawServer.Contains('{', StringComparison.Ordinal) || rawServer.Contains('}', StringComparison.Ordinal))
            {
                errorCode = BusinessSkillErrorCodes.ValidationFailed;
                errorMessage = "Server URL still contains unresolved template variables.";
                return false;
            }
        }

        if (Uri.TryCreate(rawServer, UriKind.Absolute, out var absUri))
        {
            resolvedBaseUri = absUri;
            return true;
        }

        if (Uri.TryCreate(request.EndpointPolicy.AllowedOrigin, UriKind.Absolute, out var originUri) &&
            Uri.TryCreate(originUri, rawServer, out var combined))
        {
            resolvedBaseUri = combined;
            return true;
        }

        errorCode = BusinessSkillErrorCodes.ValidationFailed;
        errorMessage = $"Unable to resolve valid absolute server URI from '{rawServer}'.";
        return false;
    }

    private static bool TryBuildRequestComponents(
        CapabilityInvocationSpecDto spec,
        IReadOnlyDictionary<string, JsonElement> arguments,
        Uri serverBaseUri,
        out Uri builtUri,
        out List<(string Name, string Value)> headers,
        out string? errorCode,
        out string? errorMessage)
    {
        builtUri = default!;
        headers = [];
        errorCode = null;
        errorMessage = null;

        var path = spec.NormalizedPath ?? "/";
        var queryItems = new List<string>();

        foreach (var param in spec.Parameters)
        {
            var hasArg = arguments.TryGetValue(param.Name, out var rawArg) &&
                         rawArg.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;

            if (!hasArg)
            {
                if (param.Required)
                {
                    errorCode = BusinessSkillErrorCodes.ValidationFailed;
                    errorMessage = $"Missing required parameter '{param.Name}' ({param.Location}).";
                    return false;
                }

                continue;
            }

            switch (param.Location.ToLowerInvariant())
            {
                case "path":
                    if (param.Style is not (null or "simple"))
                    {
                        errorCode = BusinessSkillErrorCodes.UnsupportedSchema;
                        errorMessage = $"Unsupported path serialization style '{param.Style}' on parameter '{param.Name}'.";
                        return false;
                    }

                    var pathVal = SerializeSimpleValue(rawArg);
                    if (pathVal.Contains("..", StringComparison.Ordinal))
                    {
                        errorCode = BusinessSkillErrorCodes.SelectionViolation;
                        errorMessage = $"Path parameter '{param.Name}' contains forbidden traversal segments.";
                        return false;
                    }

                    path = path.Replace($"{{{param.Name}}}", Uri.EscapeDataString(pathVal), StringComparison.Ordinal);
                    break;

                case "query":
                    if (param.Style is not (null or "form"))
                    {
                        errorCode = BusinessSkillErrorCodes.UnsupportedSchema;
                        errorMessage = $"Unsupported query serialization style '{param.Style}' on parameter '{param.Name}'.";
                        return false;
                    }

                    // 非必填查询参数若为空/纯空白字符串或空数组，跳过不拼接（避免向 Guid?/uuid/int? 后端发送 ?tenantId= 引发 400/404）
                    if (!param.Required && IsOptionalEmptyValue(rawArg))
                    {
                        continue;
                    }

                    if (rawArg.ValueKind == JsonValueKind.Array)
                    {
                        var parts = rawArg.EnumerateArray().Select(BusinessCapabilityResponseHelpers.JsonElementToScalarString).ToArray();
                        var explode = param.Explode ?? true; // OpenAPI 3 query form defaults to explode=true
                        if (explode)
                        {
                            foreach (var p in parts)
                            {
                                queryItems.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(p)}");
                            }
                        }
                        else
                        {
                            queryItems.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(string.Join(',', parts))}");
                        }
                    }
                    else
                    {
                        queryItems.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(BusinessCapabilityResponseHelpers.JsonElementToScalarString(rawArg))}");
                    }
                    break;

                case "header":
                    if (param.Style is not (null or "simple"))
                    {
                        errorCode = BusinessSkillErrorCodes.UnsupportedSchema;
                        errorMessage = $"Unsupported header serialization style '{param.Style}' on parameter '{param.Name}'.";
                        return false;
                    }

                    if (!param.Required && IsOptionalEmptyValue(rawArg))
                    {
                        continue;
                    }

                    headers.Add((param.Name, SerializeSimpleValue(rawArg)));
                    break;

                default:
                    errorCode = BusinessSkillErrorCodes.UnsupportedSchema;
                    errorMessage = $"Unsupported parameter location '{param.Location}' on parameter '{param.Name}'.";
                    return false;
            }
        }

        // 拼接 server 基准路径与操作 path
        var basePath = serverBaseUri.AbsolutePath.TrimEnd('/');
        var fullPath = (basePath + path).Replace("//", "/", StringComparison.Ordinal);
        var builder = new UriBuilder(serverBaseUri)
        {
            Path = fullPath,
            Query = queryItems.Count > 0 ? string.Join('&', queryItems) : string.Empty
        };

        builtUri = builder.Uri;
        return true;
    }

    private static bool IsOptionalEmptyValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.GetArrayLength() == 0;
        }

        return false;
    }

    private static string FormatErrorResponseSnippet(string? rawText, int maxChars = 240)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(rawText.Length, maxChars + 8));
        var prevSpace = false;
        foreach (var ch in rawText)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }

            if (sb.Length > maxChars)
            {
                break;
            }
        }

        var compact = sb.ToString().Trim();
        return compact.Length <= maxChars ? compact : compact[..maxChars] + "...";
    }

    private static string SerializeSimpleValue(JsonElement element)
        => element.ValueKind == JsonValueKind.Array
            ? string.Join(',', element.EnumerateArray().Select(BusinessCapabilityResponseHelpers.JsonElementToScalarString))
            : BusinessCapabilityResponseHelpers.JsonElementToScalarString(element);

    private static Uri AppendQueryParameters(Uri uri, List<(string Name, string Value)> extraQuery)
    {
        if (extraQuery.Count == 0)
        {
            return uri;
        }

        var added = string.Join('&', extraQuery.Select(static q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
        var ub = new UriBuilder(uri);
        var existing = ub.Query.TrimStart('?');
        ub.Query = string.IsNullOrEmpty(existing) ? added : $"{existing}&{added}";
        return ub.Uri;
    }

    private async Task<SwaggerUiResolveResult> TryResolveSwaggerUiDocumentAsync(
        string htmlText,
        Uri uiPageUri,
        HttpClient httpClient,
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        RefLoadBudgetState budget,
        CancellationToken ct)
    {
        if (HtmlLoginFormRegex().IsMatch(htmlText))
        {
            return new SwaggerUiResolveResult(false, BusinessSkillErrorCodes.AuthRequired,
                "Target URL returned an HTML login page instead of an OpenAPI specification.", null, null);
        }

        Uri? candidateSpecUri = null;
        Uri? configJsonUri = null;

        var inlineMatch = SwaggerConfigPropertyRegex().Match(htmlText);
        if (inlineMatch.Success)
        {
            var raw = inlineMatch.Groups[1].Value;
            if (raw.Contains("config", StringComparison.OrdinalIgnoreCase) && raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                Uri.TryCreate(uiPageUri, raw, out configJsonUri);
            }
            else
            {
                Uri.TryCreate(uiPageUri, raw, out candidateSpecUri);
            }
        }

        if (candidateSpecUri is null && configJsonUri is null)
        {
            var initScript = SwaggerInitializerScriptRegex().Match(htmlText);
            if (initScript.Success && Uri.TryCreate(uiPageUri, initScript.Groups[1].Value, out var initJsUri))
            {
                var initEval = await _targetGuardStaticCheck(httpClient, initJsUri, budget, ct).ConfigureAwait(false);
                if (initEval.Success && initEval.Text is not null)
                {
                    var jsProp = SwaggerConfigPropertyRegex().Match(initEval.Text);
                    if (jsProp.Success)
                    {
                        var val = jsProp.Groups[1].Value;
                        if (initJsUri.AbsolutePath.Contains("initializer", StringComparison.OrdinalIgnoreCase) &&
                            val.Contains("config", StringComparison.OrdinalIgnoreCase))
                        {
                            Uri.TryCreate(uiPageUri, val, out configJsonUri);
                        }
                        else
                        {
                            Uri.TryCreate(uiPageUri, val, out candidateSpecUri);
                        }
                    }
                }
            }
        }

        if (configJsonUri is not null)
        {
            var cfgCheck = await ValidateSameOriginSubFetchAsync(configJsonUri, policy, authKind, ct).ConfigureAwait(false);
            if (cfgCheck.Allowed)
            {
                var cfgFetch = await FetchBoundedTextAsync(httpClient, configJsonUri, budget, ct).ConfigureAwait(false);
                if (cfgFetch.Success && cfgFetch.Text is not null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(cfgFetch.Text);
                        string? urlStr = null;
                        if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            urlStr = u.GetString();
                        }
                        else if (doc.RootElement.TryGetProperty("urls", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                        {
                            if (arr[0].TryGetProperty("url", out var firstUrl) && firstUrl.ValueKind == JsonValueKind.String)
                            {
                                urlStr = firstUrl.GetString();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(urlStr))
                        {
                            Uri.TryCreate(configJsonUri, urlStr, out candidateSpecUri);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        if (candidateSpecUri is null)
        {
            return new SwaggerUiResolveResult(false, BusinessSkillErrorCodes.UnsupportedProtocol,
                "Target URL is a Swagger UI HTML page whose static OpenAPI JSON/YAML document URL could not be determined without executing scripts. Please provide the raw OpenAPI JSON/YAML URL.",
                null,
                null);
        }

        var specCheck = await ValidateSameOriginSubFetchAsync(candidateSpecUri, policy, authKind, ct).ConfigureAwait(false);
        if (!specCheck.Allowed)
        {
            return new SwaggerUiResolveResult(false, specCheck.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation,
                $"Swagger UI static document target '{candidateSpecUri}' was blocked: {specCheck.Message}", null, null);
        }

        var specFetch = await FetchBoundedTextAsync(httpClient, candidateSpecUri, budget, ct).ConfigureAwait(false);
        if (!specFetch.Success)
        {
            return new SwaggerUiResolveResult(false, specFetch.ErrorCode, specFetch.ErrorMessage, null, null);
        }

        return new SwaggerUiResolveResult(true, null, null, candidateSpecUri, specFetch.Text);
    }

    private Task<TargetPolicyEvaluationResult> ValidateSameOriginSubFetchAsync(
        Uri subUri,
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        CancellationToken ct)
        => _targetGuard.ValidateAndResolveAsync(subUri, policy, authKind, OutboundRequestPurpose.Discovery, ct);

    private static async Task<BoundedTextFetchResult> _targetGuardStaticCheck(
        HttpClient client,
        Uri uri,
        RefLoadBudgetState budget,
        CancellationToken ct)
    {
        return await FetchBoundedTextAsync(client, uri, budget, ct).ConfigureAwait(false);
    }

    private static async Task<BoundedTextFetchResult> FetchBoundedTextAsync(
        HttpClient httpClient,
        Uri targetUri,
        RefLoadBudgetState budget,
        CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is 401 or 403)
            {
                return new BoundedTextFetchResult(false, BusinessSkillErrorCodes.AuthRequired,
                    $"HTTP {status} while fetching OpenAPI document.", null);
            }

            if (status is >= 300 and <= 399)
            {
                return new BoundedTextFetchResult(false, BusinessSkillErrorCodes.SelectionViolation,
                    $"Automatic HTTP redirect ({status}) is blocked by endpoint security policy.", null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new BoundedTextFetchResult(false, BusinessSkillErrorCodes.ConnectionUnavailable,
                    $"HTTP {status}: {response.ReasonPhrase}", null);
            }

            var body = await BusinessCapabilityResponseHelpers.ReadBoundedContentAsync(response, budget.RemainingBytes, ct).ConfigureAwait(false);
            if (!body.WithinLimit)
            {
                return new BoundedTextFetchResult(false, BusinessSkillErrorCodes.BudgetExceeded, body.ErrorMessage, null);
            }

            budget.ConsumeBytes(body.Bytes.Length);
            return new BoundedTextFetchResult(true, null, null, Encoding.UTF8.GetString(body.Bytes));
        }
        catch (HttpRequestException ex)
        {
            return new BoundedTextFetchResult(false, BusinessSkillErrorCodes.ConnectionUnavailable, ex.Message, null);
        }
    }

    private static List<IOpenApiParameter> MergeParameters(
        IList<IOpenApiParameter>? pathParams,
        IList<IOpenApiParameter>? opParams)
    {
        var merged = new Dictionary<(string Name, ParameterLocation? In), IOpenApiParameter>();

        if (pathParams is not null)
        {
            foreach (var p in pathParams)
            {
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    merged[(p.Name, p.In)] = p;
                }
            }
        }

        if (opParams is not null)
        {
            foreach (var p in opParams)
            {
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    merged[(p.Name, p.In)] = p;
                }
            }
        }

        return merged.Values.ToList();
    }

    private static bool HasComplexUnionSchema(IOpenApiSchema? schema)
        => schema is not null && (schema.OneOf is { Count: > 0 } || schema.AnyOf is { Count: > 0 });

    private static bool HasUnresolvedExternalRef(IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return false;
        }

        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        return HasUnresolvedExternalRefCore(schema, visited);
    }

    private static bool HasUnresolvedExternalRefCore(IOpenApiSchema schema, HashSet<IOpenApiSchema> visited)
    {
        if (!visited.Add(schema))
        {
            return false;
        }

        if (schema is OpenApiSchemaReference schemaRef)
        {
            if (!string.IsNullOrWhiteSpace(schemaRef.Reference?.ExternalResource) && schemaRef.Target is null)
            {
                return true;
            }

            if (schemaRef.Target is not null && HasUnresolvedExternalRefCore(schemaRef.Target, visited))
            {
                return true;
            }
        }

        if (schema.Properties is not null)
        {
            foreach (var prop in schema.Properties.Values)
            {
                if (HasUnresolvedExternalRefCore(prop, visited))
                {
                    return true;
                }
            }
        }

        if (schema.Items is not null && HasUnresolvedExternalRefCore(schema.Items, visited))
        {
            return true;
        }

        if (schema.AdditionalProperties is not null && HasUnresolvedExternalRefCore(schema.AdditionalProperties, visited))
        {
            return true;
        }

        if (schema.AllOf is not null)
        {
            foreach (var sub in schema.AllOf)
            {
                if (HasUnresolvedExternalRefCore(sub, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        var trimmed = rawPath.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string CreateUniqueModelAlias(
        string? operationId,
        string method,
        string normalizedPath,
        HashSet<string> usedAliases)
    {
        var raw = string.IsNullOrWhiteSpace(operationId)
            ? $"{method.ToLowerInvariant()}_{normalizedPath}"
            : operationId.Trim();

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var baseName = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "op";
        }

        if (baseName.Length > 40)
        {
            baseName = baseName[..40];
        }

        var hashSuffix = ComputeSha256Hex(Encoding.UTF8.GetBytes($"{method}:{normalizedPath}"))[..6];
        var candidate = $"{baseName}_{hashSuffix}";
        var counter = 1;
        while (!usedAliases.Add(candidate))
        {
            candidate = $"{baseName}_{hashSuffix}_{counter++}";
        }

        return candidate;
    }

    private static string ComputeSha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static JsonElement? TryParseJsonPayload(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var trimmed = rawText.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(rawText);
        return doc.RootElement.Clone();
    }

    private static OpenApiDiscoveryResult FailedResult(string code, string message, string correlationId)
        => new(
            false,
            null,
            [],
            new BusinessSkillErrorDto(code, message, [], false, correlationId),
            []);

    private sealed class RefLoadBudgetState(int maxBytes, int maxFiles)
    {
        private readonly Dictionary<string, byte[]> _externalFileCache = new(StringComparer.Ordinal);
        private readonly int _maxInvocations = Math.Max(64, maxFiles * 20);
        private int _remainingBytes = maxBytes;
        private int _invocations;

        public int RemainingBytes => Math.Max(0, _remainingBytes);
        public string? LastFailureCode { get; private set; }
        public string? LastFailureMessage { get; private set; }

        public bool TryGetCachedFile(string normalizedUri, out byte[] bytes)
        {
            if (++_invocations > _maxInvocations)
            {
                var msg = $"External $ref resolution exceeded recursion safety limit of {_maxInvocations} lookups.";
                RecordFailure(BusinessSkillErrorCodes.BudgetExceeded, msg);
                throw new InvalidOperationException(msg);
            }

            return _externalFileCache.TryGetValue(normalizedUri, out bytes!);
        }

        public void CacheFile(string normalizedUri, byte[] bytes)
            => _externalFileCache[normalizedUri] = bytes;

        public void RecordFailure(string code, string message)
        {
            LastFailureCode ??= code;
            LastFailureMessage ??= message;
        }

        public void ConsumeBytes(int count)
            => _remainingBytes = Math.Max(0, _remainingBytes - count);

        public void ReserveNewExternalFile(string normalizedUri)
        {
            if (_externalFileCache.ContainsKey(normalizedUri))
            {
                return;
            }

            if (_externalFileCache.Count + 1 > maxFiles)
            {
                var msg = $"External $ref file count exceeded maximum limit of {maxFiles}.";
                RecordFailure(BusinessSkillErrorCodes.BudgetExceeded, msg);
                throw new InvalidOperationException(msg);
            }
        }
    }

    private sealed class PolicyGuardedExternalRefLoader(
        OutboundTargetPolicyGuard guard,
        EndpointPolicyDto policy,
        BusinessAuthKind docAuthKind,
        string? docSecretJson,
        Uri defaultBaseUri,
        RefLoadBudgetState budget) : IStreamLoader
    {
        public async Task<Stream> LoadAsync(Uri baseOrTargetUri, Uri? targetUri, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(baseOrTargetUri);

            // Microsoft.OpenApi 2.13 IStreamLoader 传入 (baseUrl, externalResourceUri)
            Uri resolved;
            if (targetUri is not null)
            {
                var originBase = baseOrTargetUri.IsAbsoluteUri ? baseOrTargetUri : defaultBaseUri;
                resolved = targetUri.IsAbsoluteUri ? targetUri : new Uri(originBase, targetUri);
            }
            else
            {
                resolved = baseOrTargetUri.IsAbsoluteUri ? baseOrTargetUri : new Uri(defaultBaseUri, baseOrTargetUri);
            }

            var normalizedKey = new UriBuilder(resolved) { Fragment = string.Empty }.Uri.AbsoluteUri;
            if (budget.TryGetCachedFile(normalizedKey, out var cachedBytes))
            {
                return new MemoryStream(cachedBytes, writable: false);
            }

            budget.ReserveNewExternalFile(normalizedKey);

            var eval = await guard.ValidateAndResolveAsync(resolved, policy, docAuthKind, OutboundRequestPurpose.ExternalRef, cancellationToken)
                .ConfigureAwait(false);
            if (!eval.Allowed)
            {
                var msg = eval.Message ?? $"External $ref target '{resolved}' denied by endpoint policy.";
                budget.RecordFailure(eval.ErrorCode ?? BusinessSkillErrorCodes.SelectionViolation, msg);
                throw new InvalidOperationException(msg);
            }

            using var client = guard.CreateGuardedHttpClient(policy, docAuthKind, OutboundRequestPurpose.ExternalRef);

            // 跨 Origin 的外部 $ref 绝不转发源系统的文档认证凭据
            if (OutboundTargetPolicyGuard.TryParseOrigin(policy.AllowedOrigin, out var allowedOrigin) &&
                string.Equals(OutboundTargetPolicyGuard.GetOriginString(resolved), OutboundTargetPolicyGuard.GetOriginString(allowedOrigin), StringComparison.OrdinalIgnoreCase))
            {
                BusinessCapabilityResponseHelpers.ApplyDefaultAuthHeaders(client, docAuthKind, docSecretJson);
            }

            using var response = await client.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var msg = $"Failed to fetch external $ref '{resolved}': HTTP {(int)response.StatusCode}.";
                budget.RecordFailure(BusinessSkillErrorCodes.ConnectionUnavailable, msg);
                throw new HttpRequestException(msg);
            }

            var body = await BusinessCapabilityResponseHelpers.ReadBoundedContentAsync(response, budget.RemainingBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!body.WithinLimit)
            {
                var msg = body.ErrorMessage ?? "External $ref exceeded document byte budget.";
                budget.RecordFailure(BusinessSkillErrorCodes.BudgetExceeded, msg);
                throw new InvalidOperationException(msg);
            }

            budget.ConsumeBytes(body.Bytes.Length);
            budget.CacheFile(normalizedKey, body.Bytes);
            return new MemoryStream(body.Bytes, writable: false);
        }
    }

    private sealed record BoundedTextFetchResult(
        bool Success,
        string? ErrorCode,
        string? ErrorMessage,
        string? Text);

    private sealed record SwaggerUiResolveResult(
        bool Success,
        string? ErrorCode,
        string? ErrorMessage,
        Uri? ResolvedUri,
        string? SpecText);
}
