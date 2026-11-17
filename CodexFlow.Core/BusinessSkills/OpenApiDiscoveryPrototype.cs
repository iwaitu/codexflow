// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

public sealed record OpenApiDiscoveryResult(
    bool Success,
    CatalogSnapshotDto? Snapshot,
    IReadOnlyList<CapabilityDetailDto> Capabilities,
    BusinessSkillErrorDto? Error,
    IReadOnlyList<string> Servers);

public sealed record OpenApiHttpInvocationResult(
    bool Success,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? BodyJson,
    string? RawText);

/// <summary>
/// P0 阶段对外原型门面，内部统一委托 <see cref="OpenApiBusinessPluginAdapter"/> 保持单套实现。
/// </summary>
public static class OpenApiDiscoveryPrototype
{
    public static Task<OpenApiDiscoveryResult> DiscoverFromContentAsync(
        string pluginId,
        string snapshotId,
        string rawContent,
        string? formatHint = null,
        CancellationToken ct = default)
        => OpenApiBusinessPluginAdapter.DiscoverFromContentAsync(pluginId, snapshotId, rawContent, formatHint, ct);

    public static async Task<OpenApiHttpInvocationResult> InvokeOperationAsync(
        HttpClient httpClient,
        Uri baseServerUri,
        CapabilityDetailDto capability,
        IReadOnlyDictionary<string, string?> parameters,
        JsonElement? jsonBody = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseServerUri);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(parameters);

        var spec = capability.Invocation;
        if (capability.Summary.Executability != CapabilityExecutabilityStatus.Executable ||
            string.IsNullOrWhiteSpace(spec.HttpMethod) ||
            string.IsNullOrWhiteSpace(spec.NormalizedPath))
        {
            return new OpenApiHttpInvocationResult(
                false,
                0,
                BusinessSkillErrorCodes.UnsupportedSchema,
                $"Capability '{capability.Summary.Id}' is not executable.",
                null,
                null);
        }

        var path = spec.NormalizedPath;
        var queryItems = new List<string>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var param in spec.Parameters)
        {
            parameters.TryGetValue(param.Name, out var val);
            if (param.Required && string.IsNullOrWhiteSpace(val))
            {
                return new OpenApiHttpInvocationResult(
                    false,
                    0,
                    BusinessSkillErrorCodes.ValidationFailed,
                    $"Missing required parameter '{param.Name}' ({param.Location}).",
                    null,
                    null);
            }

            if (val is null)
            {
                continue;
            }

            switch (param.Location.ToLowerInvariant())
            {
                case "path":
                    path = path.Replace($"{{{param.Name}}}", Uri.EscapeDataString(val), StringComparison.Ordinal);
                    break;
                case "query":
                    queryItems.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(val)}");
                    break;
                case "header":
                    headers[param.Name] = val;
                    break;
            }
        }

        var relative = path.TrimStart('/');
        if (queryItems.Count > 0)
        {
            relative = $"{relative}?{string.Join('&', queryItems)}";
        }

        var baseNormalized = baseServerUri.AbsoluteUri.EndsWith('/')
            ? baseServerUri
            : new Uri(baseServerUri.AbsoluteUri + "/", UriKind.Absolute);
        var requestUri = new Uri(baseNormalized, relative);

        if (!string.Equals(requestUri.Scheme, baseNormalized.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, baseNormalized.Host, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != baseNormalized.Port)
        {
            return new OpenApiHttpInvocationResult(
                false,
                0,
                BusinessSkillErrorCodes.SelectionViolation,
                "Resolved request URI escaped authorized server origin.",
                null,
                null);
        }

        using var request = new HttpRequestMessage(new HttpMethod(spec.HttpMethod), requestUri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (jsonBody.HasValue && jsonBody.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            request.Content = new StringContent(jsonBody.Value.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var bounded = await BusinessCapabilityResponseHelpers.ReadBoundedContentAsync(
            response,
            BusinessSkillDefaults.MaxSingleResponseBytes,
            ct).ConfigureAwait(false);
        if (!bounded.WithinLimit)
        {
            return new OpenApiHttpInvocationResult(
                false,
                (int)response.StatusCode,
                BusinessSkillErrorCodes.BudgetExceeded,
                bounded.ErrorMessage,
                null,
                null);
        }

        var rawText = bounded.Bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bounded.Bytes);

        JsonElement? parsedJson = null;
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            var trimmed = rawText.AsSpan().TrimStart();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                using var doc = JsonDocument.Parse(rawText);
                parsedJson = doc.RootElement.Clone();
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            return new OpenApiHttpInvocationResult(
                false,
                (int)response.StatusCode,
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? BusinessSkillErrorCodes.AuthRequired
                    : BusinessSkillErrorCodes.ConnectionUnavailable,
                $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                parsedJson,
                rawText);
        }

        return new OpenApiHttpInvocationResult(true, (int)response.StatusCode, null, null, parsedJson, rawText);
    }
}
