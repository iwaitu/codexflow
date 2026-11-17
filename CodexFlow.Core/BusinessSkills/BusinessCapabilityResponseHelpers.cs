// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

internal sealed record BoundedHttpBodyResult(
    bool WithinLimit,
    byte[] Bytes,
    string? ErrorMessage);

internal static class BusinessCapabilityResponseHelpers
{
    public static async Task<BoundedHttpBodyResult> ReadBoundedContentAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            return new BoundedHttpBodyResult(
                false,
                [],
                string.Format(CultureInfo.InvariantCulture, "Response Content-Length {0} exceeds maximum allowed {1} bytes.", contentLength, maxBytes));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return new BoundedHttpBodyResult(
                    false,
                    [],
                    string.Format(CultureInfo.InvariantCulture, "Response stream exceeded maximum allowed {0} bytes and was aborted instead of truncating.", maxBytes));
            }

            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return new BoundedHttpBodyResult(true, ms.ToArray(), null);
    }

    public static bool TryEvaluateJsonPointer(JsonElement root, string? pointer, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return true;
        }

        if (!pointer.StartsWith('/'))
        {
            return false;
        }

        var segments = pointer.Split('/');
        var current = root;

        for (var i = 1; i < segments.Length; i++)
        {
            var token = segments[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out var next))
                {
                    value = default;
                    return false;
                }

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                if (idx < 0 || idx >= current.GetArrayLength())
                {
                    value = default;
                    return false;
                }

                current = current[idx];
            }
            else
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    public static void EvaluateResponseContract(
        JsonElement? payload,
        SkillResponseContract? contract,
        out bool contractSatisfied,
        out string? createdId,
        out string? failureReason)
    {
        contractSatisfied = true;
        createdId = null;
        failureReason = null;

        if (contract is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(contract.BusinessCodePointer))
        {
            if (payload is null || !TryEvaluateJsonPointer(payload.Value, contract.BusinessCodePointer, out var codeElem))
            {
                contractSatisfied = false;
                failureReason = $"Response envelope is missing required business code at '{contract.BusinessCodePointer}'.";
                return;
            }

            var codeText = JsonElementToScalarString(codeElem);
            if (contract.SuccessCodeValues.Count > 0 &&
                !contract.SuccessCodeValues.Contains(codeText, StringComparer.Ordinal))
            {
                contractSatisfied = false;
                failureReason = $"Business envelope code '{codeText}' did not match expected success codes [{string.Join(", ", contract.SuccessCodeValues)}].";
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(contract.CreatedIdPointer))
        {
            if (payload is null ||
                !TryEvaluateJsonPointer(payload.Value, contract.CreatedIdPointer, out var idElem) ||
                string.IsNullOrWhiteSpace(JsonElementToScalarString(idElem)))
            {
                contractSatisfied = false;
                failureReason = $"Response is missing required created identifier at '{contract.CreatedIdPointer}'.";
                return;
            }

            createdId = JsonElementToScalarString(idElem);
        }
    }

    public static string JsonElementToScalarString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText()
    };

    public static bool IsTransientHttpStatus(int statusCode)
        => statusCode is 408 or 429 or 502 or 503 or 504;

    public static TimeSpan ResolveRetryDelay(HttpResponseMessage? response, int retryAttempt)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delta;
            }

            if (retryAfter.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    return diff > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : diff;
                }
            }
        }

        var ms = Math.Min(1000, 50 * retryAttempt);
        return TimeSpan.FromMilliseconds(ms);
    }

    public static bool TryApplyConfiguredAuth(
        HttpRequestMessage request,
        List<(string Name, string Value)> extraQueryParams,
        BusinessAuthKind authKind,
        string? secretPayloadJson,
        IReadOnlyList<CapabilitySecurityRequirementDto> securityAlternatives,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(extraQueryParams);
        errorMessage = null;

        // 若 OpenAPI 定义了 security requirement，则先核对多组备选方案（OR 内含 AND 组合）
        if (securityAlternatives is { Count: > 0 })
        {
            // 若显式存在空安全方案 `{}`，允许无认证访问（但若调用方主动配置了有效凭据仍可注入）
            var allowsAnonymous = securityAlternatives.Any(static req => req.Schemes.Count == 0);

            if (authKind == BusinessAuthKind.None || string.IsNullOrWhiteSpace(secretPayloadJson))
            {
                if (allowsAnonymous)
                {
                    return true;
                }

                errorMessage = "Operation requires authentication, but no credential is configured.";
                return false;
            }

            // 解析组合凭据对象：{ "schemes": { "<schemeId>": { ... } } } 或扁平单认证对象
            JsonElement? schemesMap = null;
            using var doc = TryParseJsonObject(secretPayloadJson);
            if (doc is not null &&
                doc.RootElement.TryGetProperty("schemes", out var sObj) &&
                sObj.ValueKind == JsonValueKind.Object)
            {
                schemesMap = sObj;
            }

            foreach (var alternative in securityAlternatives)
            {
                if (alternative.Schemes.Count == 0)
                {
                    return true;
                }

                if (schemesMap.HasValue)
                {
                    if (TryApplySchemeGroupFromMap(request, extraQueryParams, alternative.Schemes, schemesMap.Value))
                    {
                        return true;
                    }
                }
                else if (doc is not null &&
                         TryApplyFlatCombinedAlternative(request, extraQueryParams, alternative.Schemes, authKind, doc.RootElement))
                {
                    return true;
                }
            }

            errorMessage = "Configured credential does not satisfy any OpenAPI security alternative requirement (AND/OR).";
            return false;
        }

        // 未声明 OpenAPI SecurityAlternatives（如 MCP 或无 security 字段的 OpenAPI）时，按显式配置的 AuthKind 注入
        if (authKind == BusinessAuthKind.None || string.IsNullOrWhiteSpace(secretPayloadJson))
        {
            return true;
        }

        using var fallbackDoc = TryParseJsonObject(secretPayloadJson);
        if (fallbackDoc is null)
        {
            return true;
        }

        ApplyAuthFromFlatJson(request, authKind, fallbackDoc.RootElement);
        return true;
    }

    public static void ApplyDefaultAuthHeaders(HttpClient client, BusinessAuthKind authKind, string? secretPayloadJson)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (authKind == BusinessAuthKind.None || string.IsNullOrWhiteSpace(secretPayloadJson))
        {
            return;
        }

        using var doc = TryParseJsonObject(secretPayloadJson);
        if (doc is null)
        {
            return;
        }

        var root = doc.RootElement;
        switch (authKind)
        {
            case BusinessAuthKind.Bearer or BusinessAuthKind.OAuth2AuthCodePkce
                when root.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String:
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenProp.GetString());
                break;
            case BusinessAuthKind.ApiKey
                when root.TryGetProperty("headerName", out var headerProp) &&
                     root.TryGetProperty("apiKey", out var keyProp) &&
                     headerProp.ValueKind == JsonValueKind.String &&
                     keyProp.ValueKind == JsonValueKind.String:
                client.DefaultRequestHeaders.TryAddWithoutValidation(headerProp.GetString()!, keyProp.GetString());
                break;
            case BusinessAuthKind.BasicTlsOnly
                when root.TryGetProperty("username", out var u) &&
                     root.TryGetProperty("password", out var p) &&
                     u.ValueKind == JsonValueKind.String &&
                     p.ValueKind == JsonValueKind.String:
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u.GetString()}:{p.GetString()}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                break;
        }
    }

    private static bool TryApplySchemeGroupFromMap(
        HttpRequestMessage request,
        List<(string Name, string Value)> extraQueryParams,
        IReadOnlyList<CapabilitySecuritySchemeRequirementDto> schemes,
        JsonElement schemesMap)
        => TryApplySchemeGroupWithResolver(
            request,
            extraQueryParams,
            schemes,
            scheme => schemesMap.TryGetProperty(scheme.SchemeId, out var entry) && entry.ValueKind == JsonValueKind.Object
                ? (true, entry)
                : (false, default),
            enforceHeaderNameMatch: false);

    private static bool TryApplyFlatCombinedAlternative(
        HttpRequestMessage request,
        List<(string Name, string Value)> extraQueryParams,
        IReadOnlyList<CapabilitySecuritySchemeRequirementDto> schemes,
        BusinessAuthKind primaryAuthKind,
        JsonElement root)
    {
        if (schemes.Count == 1 && !SchemeMatchesSingleAuthKind(schemes[0], primaryAuthKind))
        {
            return false;
        }

        return TryApplySchemeGroupWithResolver(
            request,
            extraQueryParams,
            schemes,
            _ => (true, root),
            enforceHeaderNameMatch: true);
    }

    private static bool TryApplySchemeGroupWithResolver(
        HttpRequestMessage request,
        List<(string Name, string Value)> extraQueryParams,
        IReadOnlyList<CapabilitySecuritySchemeRequirementDto> schemes,
        Func<CapabilitySecuritySchemeRequirementDto, (bool Found, JsonElement Entry)> entryResolver,
        bool enforceHeaderNameMatch)
    {
        var pendingHeaders = new List<(string Name, string Value)>();
        var pendingQueries = new List<(string Name, string Value)>();
        AuthenticationHeaderValue? pendingAuthHeader = null;

        foreach (var scheme in schemes)
        {
            var (found, entry) = entryResolver(scheme);
            if (!found)
            {
                return false;
            }

            if (string.Equals(scheme.SchemeType, "ApiKey", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(scheme.InLocation, "cookie", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var keyVal = entry.TryGetProperty("apiKey", out var kp) && kp.ValueKind == JsonValueKind.String ? kp.GetString() : null;
                var targetName = scheme.HeaderOrParamName;
                if (string.IsNullOrWhiteSpace(keyVal) || string.IsNullOrWhiteSpace(targetName))
                {
                    return false;
                }

                if (enforceHeaderNameMatch &&
                    entry.TryGetProperty("headerName", out var hp) &&
                    hp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(hp.GetString()) &&
                    !string.Equals(hp.GetString(), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(scheme.InLocation, "query", StringComparison.OrdinalIgnoreCase))
                {
                    pendingQueries.Add((targetName, keyVal));
                }
                else
                {
                    pendingHeaders.Add((targetName, keyVal));
                }
            }
            else if (string.Equals(scheme.SchemeType, "Http", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(scheme.HttpScheme, "basic", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(request.RequestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var u = entry.TryGetProperty("username", out var up) ? up.GetString() : null;
                    var p = entry.TryGetProperty("password", out var pp) ? pp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(u) || p is null)
                    {
                        return false;
                    }

                    pendingAuthHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u}:{p}")));
                }
                else
                {
                    var tok = entry.TryGetProperty("token", out var tp) ? tp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        return false;
                    }

                    pendingAuthHeader = new AuthenticationHeaderValue("Bearer", tok);
                }
            }
            else if (string.Equals(scheme.SchemeType, "OAuth2", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scheme.SchemeType, "OpenIdConnect", StringComparison.OrdinalIgnoreCase))
            {
                var tok = entry.TryGetProperty("token", out var tp) ? tp.GetString() : null;
                if (string.IsNullOrWhiteSpace(tok))
                {
                    return false;
                }

                pendingAuthHeader = new AuthenticationHeaderValue("Bearer", tok);
            }
            else
            {
                return false;
            }
        }

        if (pendingAuthHeader is not null)
        {
            request.Headers.Authorization = pendingAuthHeader;
        }

        foreach (var h in pendingHeaders)
        {
            request.Headers.TryAddWithoutValidation(h.Name, h.Value);
        }

        extraQueryParams.AddRange(pendingQueries);
        return true;
    }

    private static bool SchemeMatchesSingleAuthKind(CapabilitySecuritySchemeRequirementDto scheme, BusinessAuthKind authKind)
        => (authKind, scheme.SchemeType.ToLowerInvariant(), scheme.HttpScheme?.ToLowerInvariant()) switch
        {
            (BusinessAuthKind.ApiKey, "apikey", _) => true,
            (BusinessAuthKind.Bearer, "http", "bearer") => true,
            (BusinessAuthKind.Bearer, "oauth2", _) => true,
            (BusinessAuthKind.OAuth2AuthCodePkce, "oauth2", _) => true,
            (BusinessAuthKind.OAuth2AuthCodePkce, "http", "bearer") => true,
            (BusinessAuthKind.OAuth2AuthCodePkce, "openidconnect", _) => true,
            (BusinessAuthKind.BasicTlsOnly, "http", "basic") => true,
            _ => false
        };

    private static void ApplyAuthFromFlatJson(HttpRequestMessage request, BusinessAuthKind authKind, JsonElement root)
    {
        switch (authKind)
        {
            case BusinessAuthKind.Bearer or BusinessAuthKind.OAuth2AuthCodePkce
                when root.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenProp.GetString());
                break;
            case BusinessAuthKind.ApiKey
                when root.TryGetProperty("headerName", out var headerProp) &&
                     root.TryGetProperty("apiKey", out var keyProp) &&
                     headerProp.ValueKind == JsonValueKind.String &&
                     keyProp.ValueKind == JsonValueKind.String:
                request.Headers.TryAddWithoutValidation(headerProp.GetString()!, keyProp.GetString());
                break;
            case BusinessAuthKind.BasicTlsOnly
                when root.TryGetProperty("username", out var u) &&
                     root.TryGetProperty("password", out var p) &&
                     u.ValueKind == JsonValueKind.String &&
                     p.ValueKind == JsonValueKind.String &&
                     string.Equals(request.RequestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase):
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u.GetString()}:{p.GetString()}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                break;
        }
    }

    private static JsonDocument? TryParseJsonObject(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc;
            }

            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
