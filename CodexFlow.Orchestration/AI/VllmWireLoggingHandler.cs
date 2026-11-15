// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net.Http.Headers;
using System.Text;
using CodexFlow.Logging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.AI;

/// <summary>
/// Logs selected outbound VLLM HTTP payloads so tool-choice translation failures can be diagnosed.
/// </summary>
internal sealed class VllmWireLoggingHandler : DelegatingHandler
{
    private const int MaxLoggedChars = 24_000;
    private readonly ILogger<VllmWireLoggingHandler> _logger;
    private readonly bool _logPayloads;

    public VllmWireLoggingHandler(ILogger<VllmWireLoggingHandler> logger, bool logPayloads)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logPayloads = logPayloads;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = request.Content == null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var wireOverride = VllmToolWireOverrideScope.Current;
        var effectiveRequestBody = ApplyToolWireTransformations(requestBody, wireOverride);
        if (request.Content != null)
        {
            request.Content = CloneStringContent(request.Content.Headers, effectiveRequestBody);
        }

        var shouldLog = _logPayloads && ShouldLogPayload(effectiveRequestBody);
        if (shouldLog)
        {
            LogWirePayload("vllm_wire_request", request, effectiveRequestBody, null, wireOverride);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!shouldLog || response.Content == null)
        {
            return response;
        }

        var responseMediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(responseMediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Content = CloneStringContent(response.Content.Headers, responseBody);

        LogWirePayload("vllm_wire_response", request, responseBody, response, wireOverride);
        return response;
    }

    private static bool ShouldLogPayload(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return false;
        }

        return requestBody.Contains("\"tool_choice\"", StringComparison.OrdinalIgnoreCase) ||
               requestBody.Contains("\"tools\"", StringComparison.OrdinalIgnoreCase);
    }

    private void LogWirePayload(
        string eventType,
        HttpRequestMessage request,
        string body,
        HttpResponseMessage? response,
        VllmToolWireOverride? wireOverride)
    {
#pragma warning disable CA1031
        try
        {
            var summary = BuildWirePayloadSummary(body);
            var requiredToolName = wireOverride?.RequiredToolName;
            var expectedToolNames = wireOverride?.ExpectedToolNames ?? [];
            var expectedToolChoice = wireOverride?.ExpectedToolChoice;
            var isRequestPayload = response == null;
            bool? toolChoiceMatchesRequired = string.IsNullOrWhiteSpace(requiredToolName)
                ? null
                : IsToolChoiceForRequiredTool(summary.ToolChoice, requiredToolName);
            bool? toolSurfaceMatchesRequired = string.IsNullOrWhiteSpace(requiredToolName)
                ? null
                : summary.HasTools &&
                  summary.ToolCount == 1 &&
                  summary.ToolNames.Any(name => string.Equals(name, requiredToolName, StringComparison.OrdinalIgnoreCase));
            bool? toolSurfaceMatchesExpected = expectedToolNames.Count == 0
                ? null
                : DoesToolSurfaceMatchExpected(summary, expectedToolNames);
            var mismatchReasons = isRequestPayload
                ? BuildRequiredToolWireMismatchReasons(summary, requiredToolName, toolChoiceMatchesRequired, toolSurfaceMatchesRequired)
                : [];
            var expectedMismatchReasons = isRequestPayload
                ? BuildExpectedWireMismatchReasons(summary, expectedToolNames, toolSurfaceMatchesExpected)
                : [];
            var payload = new
            {
                method = request.Method.Method,
                url = RedactUrl(request.RequestUri),
                statusCode = response == null ? null : (int?)response.StatusCode,
                reasonPhrase = response?.ReasonPhrase,
                requiredToolName,
                expectedToolChoice,
                expectedToolCount = expectedToolNames.Count,
                expectedToolNames,
                promptSnapshotQueryId = wireOverride?.PromptSnapshotQueryId,
                promptSnapshotSessionId = wireOverride?.PromptSnapshotSessionId,
                promptSnapshotRound = wireOverride?.PromptSnapshotRound,
                contentType = response?.Content?.Headers.ContentType?.ToString() ?? request.Content?.Headers.ContentType?.ToString(),
                hasToolChoice = summary.HasToolChoice,
                toolChoice = summary.ToolChoice,
                toolChoiceMatchesRequired,
                hasTools = summary.HasTools,
                toolCount = summary.ToolCount,
                toolNames = summary.ToolNames,
                toolSurfaceMatchesRequired,
                toolSurfaceMatchesExpected,
                requiredToolWireMismatch = mismatchReasons.Length > 0,
                requiredToolWireMismatchReasons = mismatchReasons,
                expectedWireMismatch = expectedMismatchReasons.Length > 0,
                expectedWireMismatchReasons = expectedMismatchReasons,
                responseToolUseCount = summary.ResponseToolUseNames.Count,
                responseToolUseNames = summary.ResponseToolUseNames,
                responseStopReasons = summary.ResponseStopReasons,
                body = Truncate(NormalizeJsonIfPossible(body)),
                bodyLength = body.Length
            };

            FullTextAuditLogger.Log(_logger, eventType, "__vllm_wire__", 0, 0, payload);
            if (mismatchReasons.Length > 0)
            {
                _logger.LogWarning(
                    "Required-tool wire mismatch detected. RequiredTool={RequiredToolName} Reasons={Reasons} ToolCount={ToolCount} ToolNames={ToolNames} ToolChoice={ToolChoice}",
                    requiredToolName,
                    string.Join(", ", mismatchReasons),
                    summary.ToolCount,
                    string.Join(", ", summary.ToolNames),
                    summary.ToolChoice ?? "(none)");
            }

            if (expectedMismatchReasons.Length > 0)
            {
                _logger.LogWarning(
                    "Expected-tool wire mismatch detected. ExpectedToolCount={ExpectedToolCount} ExpectedToolNames={ExpectedToolNames} Reasons={Reasons} WireToolCount={ToolCount} WireToolNames={ToolNames}",
                    expectedToolNames.Count,
                    string.Join(", ", expectedToolNames),
                    string.Join(", ", expectedMismatchReasons),
                    summary.ToolCount,
                    string.Join(", ", summary.ToolNames));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write VLLM wire log. eventType={EventType}", eventType);
        }
#pragma warning restore CA1031
    }

    private static string RedactUrl(Uri? uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static HttpContent CloneStringContent(HttpContentHeaders headers, string body)
    {
        var mediaType = headers.ContentType?.MediaType ?? "application/json";
        var charset = headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var content = new StringContent(body, encoding, mediaType);
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return content;
    }

    private static string NormalizeJsonIfPossible(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        try
        {
            var token = JToken.Parse(body);
            return token.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Truncate(string text)
        => string.IsNullOrEmpty(text) || text.Length <= MaxLoggedChars
            ? text
            : text[..MaxLoggedChars] + "...[truncated]";

    private static WirePayloadSummary BuildWirePayloadSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return WirePayloadSummary.Empty;
        }

#pragma warning disable CA1031
        try
        {
            if (JToken.Parse(body) is JObject root)
            {
                return BuildJsonPayloadSummary(root);
            }
        }
        catch (JsonException)
        {
            // Anthropic streaming responses are server-sent events, not a single JSON document.
        }
        catch (Exception)
        {
            return WirePayloadSummary.Empty;
        }
#pragma warning restore CA1031

        return BuildServerSentEventPayloadSummary(body);
    }

    private static WirePayloadSummary BuildJsonPayloadSummary(JObject root)
    {
        var toolChoice = root["tool_choice"]?.ToString(Formatting.None);
        var tools = root["tools"] as JArray;
        var toolNames = tools?
            .OfType<JObject>()
            .Select(GetToolName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Take(100)
            .ToArray() ?? [];

        var responseToolUseNames = new List<string>();
        CollectToolUseNames(root["content"], responseToolUseNames);
        CollectToolUseNames(root["message"]?["content"], responseToolUseNames);

        var responseStopReasons = new List<string>();
        AddStringValue(root["stop_reason"], responseStopReasons);
        AddStringValue(root["message"]?["stop_reason"], responseStopReasons);

        return new WirePayloadSummary(
            HasToolChoice: root["tool_choice"] != null,
            ToolChoice: toolChoice,
            HasTools: tools != null,
            ToolCount: tools?.Count,
            ToolNames: toolNames,
            ResponseToolUseNames: responseToolUseNames.Distinct(StringComparer.Ordinal).ToArray(),
            ResponseStopReasons: responseStopReasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static WirePayloadSummary BuildServerSentEventPayloadSummary(string body)
    {
        var responseToolUseNames = new List<string>();
        var responseStopReasons = new List<string>();

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

#pragma warning disable CA1031
            try
            {
                if (JToken.Parse(data) is not JObject item)
                {
                    continue;
                }

                CollectToolUseNames(item["content_block"], responseToolUseNames);
                CollectToolUseNames(item["delta"], responseToolUseNames);
                CollectToolUseNames(item["message"]?["content"], responseToolUseNames);
                AddStringValue(item["message"]?["stop_reason"], responseStopReasons);
                AddStringValue(item["delta"]?["stop_reason"], responseStopReasons);
            }
            catch (Exception)
            {
                // Keep logging resilient; raw payload remains available in the truncated body field.
            }
#pragma warning restore CA1031
        }

        return new WirePayloadSummary(
            HasToolChoice: false,
            ToolChoice: null,
            HasTools: false,
            ToolCount: null,
            ToolNames: [],
            ResponseToolUseNames: responseToolUseNames.Distinct(StringComparer.Ordinal).ToArray(),
            ResponseStopReasons: responseStopReasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void CollectToolUseNames(JToken? token, List<string> names)
    {
        if (token == null)
        {
            return;
        }

        if (token is JArray array)
        {
            foreach (var item in array)
            {
                CollectToolUseNames(item, names);
            }

            return;
        }

        if (token is not JObject obj)
        {
            return;
        }

        if (string.Equals(obj["type"]?.Value<string>(), "tool_use", StringComparison.OrdinalIgnoreCase))
        {
            AddStringValue(obj["name"], names);
        }

        CollectToolUseNames(obj["content"], names);
        CollectToolUseNames(obj["content_block"], names);
    }

    private static void AddStringValue(JToken? token, List<string> values)
    {
        var value = token?.Type == JTokenType.String ? token.Value<string>() : null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static string ApplyToolWireTransformations(string body, VllmToolWireOverride? wireOverride)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var needsRequiredToolOverride = wireOverride != null &&
            !string.IsNullOrWhiteSpace(wireOverride.RequiredToolName);
        var needsToolSchemaNormalization = body.Contains("\"tools\"", StringComparison.OrdinalIgnoreCase);
        if (!needsRequiredToolOverride && !needsToolSchemaNormalization)
        {
            return body;
        }

        try
        {
            var root = JObject.Parse(body);
            if (needsRequiredToolOverride)
            {
                var requiredToolName = wireOverride!.RequiredToolName!.Trim();
                var matchedTool = TryNarrowTools(root, requiredToolName);
                if (matchedTool != null)
                {
                    root["tool_choice"] = BuildToolChoice(matchedTool, requiredToolName);
                }

                DisableThinkingForRequiredToolChoice(root);
            }

            NormalizeToolSchemasForAnthropicCompatibility(root);
            return root.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void DisableThinkingForRequiredToolChoice(JObject root)
    {
        root.Remove("reasoning_effort");
        root.Remove("output_config");

        if (IsDeepSeekV4Model(root))
        {
            root["thinking"] = new JObject
            {
                ["type"] = "disabled"
            };
            return;
        }

        root.Remove("thinking");
    }

    private static bool IsDeepSeekV4Model(JObject root)
    {
        var model = root["model"]?.Value<string>();
        return model != null &&
            model.StartsWith("deepseek-v4-", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeToolSchemasForAnthropicCompatibility(JObject root)
    {
        if (root["tools"] is not JArray tools)
        {
            return;
        }

        foreach (var tool in tools.OfType<JObject>())
        {
            NormalizeToolSchema(tool["input_schema"]);
            NormalizeToolSchema(tool["parameters"]);
            NormalizeToolSchema(tool["function"]?["input_schema"]);
            NormalizeToolSchema(tool["function"]?["parameters"]);
        }
    }

    private static void NormalizeToolSchema(JToken? token)
    {
        switch (token)
        {
            case JObject obj:
                if (obj["type"] is JArray types)
                {
                    var normalizedType = types
                        .OfType<JValue>()
                        .Select(static value => value.Value as string)
                        .FirstOrDefault(static value =>
                            !string.IsNullOrWhiteSpace(value) &&
                            !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(normalizedType))
                    {
                        obj["type"] = normalizedType;
                    }
                    else
                    {
                        obj.Remove("type");
                    }
                }

                foreach (var property in obj.Properties().ToList())
                {
                    NormalizeToolSchema(property.Value);
                }
                break;

            case JArray array:
                foreach (var item in array)
                {
                    NormalizeToolSchema(item);
                }
                break;
        }
    }

    private static JObject? TryNarrowTools(JObject root, string requiredToolName)
    {
        if (root["tools"] is not JArray tools || tools.Count == 0)
        {
            return null;
        }

        foreach (var item in tools.OfType<JObject>())
        {
            if (string.Equals(GetToolName(item), requiredToolName, StringComparison.OrdinalIgnoreCase))
            {
                root["tools"] = new JArray(item.DeepClone());
                return item;
            }
        }

        return null;
    }

    private static JToken BuildToolChoice(JObject matchedTool, string requiredToolName)
    {
        if (matchedTool["function"] is JObject)
        {
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = requiredToolName
                }
            };
        }

        // Responses API: flat function tool, tool_choice is {type:"function", name}. Anthropic tools carry type "tool" or an input_schema.
        if (string.Equals(matchedTool["type"]?.Value<string>(), "function", StringComparison.Ordinal) &&
            matchedTool["input_schema"] == null)
        {
            return new JObject
            {
                ["type"] = "function",
                ["name"] = requiredToolName
            };
        }

        return new JObject
        {
            ["type"] = "tool",
            ["name"] = requiredToolName
        };
    }

    private static string? GetToolName(JObject tool)
    {
        if (tool["name"]?.Type == JTokenType.String)
        {
            return tool["name"]!.Value<string>();
        }

        if (tool["function"] is JObject function &&
            function["name"]?.Type == JTokenType.String)
        {
            return function["name"]!.Value<string>();
        }

        return null;
    }

    private static string[] BuildRequiredToolWireMismatchReasons(
        WirePayloadSummary summary,
        string? requiredToolName,
        bool? toolChoiceMatchesRequired,
        bool? toolSurfaceMatchesRequired)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName))
        {
            return [];
        }

        var reasons = new List<string>();
        if (!summary.HasTools)
        {
            reasons.Add("missing_tools_array");
        }
        else if (toolSurfaceMatchesRequired != true)
        {
            reasons.Add("tools_not_narrowed_to_required_tool");
        }

        if (!summary.HasToolChoice)
        {
            reasons.Add("missing_tool_choice");
        }
        else if (toolChoiceMatchesRequired != true)
        {
            reasons.Add("tool_choice_not_required_tool");
        }

        return reasons.ToArray();
    }

    private static string[] BuildExpectedWireMismatchReasons(
        WirePayloadSummary summary,
        IReadOnlyList<string> expectedToolNames,
        bool? toolSurfaceMatchesExpected)
    {
        if (expectedToolNames.Count == 0)
        {
            return [];
        }

        var reasons = new List<string>();
        if (!summary.HasTools)
        {
            reasons.Add("missing_tools_array_for_expected_tools");
        }
        else if (toolSurfaceMatchesExpected != true)
        {
            reasons.Add("wire_tools_missing_expected_tools");
        }

        return reasons.ToArray();
    }

    private static bool DoesToolSurfaceMatchExpected(
        WirePayloadSummary summary,
        IReadOnlyList<string> expectedToolNames)
    {
        if (!summary.HasTools || !summary.ToolCount.HasValue)
        {
            return false;
        }

        var expected = expectedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actual = summary.ToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return expected.All(actual.Contains);
    }

    private static bool IsToolChoiceForRequiredTool(string? toolChoice, string requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(toolChoice))
        {
            return false;
        }

        try
        {
            var token = JToken.Parse(toolChoice);
            return ContainsRequiredToolName(token, requiredToolName);
        }
        catch (JsonException)
        {
            return toolChoice.Contains(requiredToolName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool ContainsRequiredToolName(JToken token, string requiredToolName)
    {
        if (token.Type == JTokenType.String)
        {
            return string.Equals(token.Value<string>(), requiredToolName, StringComparison.OrdinalIgnoreCase);
        }

        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (ContainsRequiredToolName(property.Value, requiredToolName))
                {
                    return true;
                }
            }

            return false;
        }

        if (token is JArray array)
        {
            return array.Any(item => ContainsRequiredToolName(item, requiredToolName));
        }

        return false;
    }

    private sealed record WirePayloadSummary(
        bool HasToolChoice,
        string? ToolChoice,
        bool HasTools,
        int? ToolCount,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> ResponseToolUseNames,
        IReadOnlyList<string> ResponseStopReasons)
    {
        public static WirePayloadSummary Empty { get; } = new(
            HasToolChoice: false,
            ToolChoice: null,
            HasTools: false,
            ToolCount: null,
            ToolNames: [],
            ResponseToolUseNames: [],
            ResponseStopReasons: []);
    }
}
