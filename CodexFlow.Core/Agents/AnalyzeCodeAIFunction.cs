// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Agents;

internal sealed class AnalyzeCodeAIFunction(ICodexTool tool, string description) : AIFunction
{
    private static readonly JsonElement ParameterSchema = ParseElement(
        """{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}""");

    public override string Name => "analyze_code";
    public override string Description => description;
    public override JsonElement JsonSchema => ParameterSchema;
    public override JsonSerializerOptions JsonSerializerOptions => AnalyzeCodeToolJsonContext.Default.Options;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        arguments.TryGetValue("code", out var value);
        var code = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        var result = code is null
            ? CodexToolResult.Error("必需的参数 'code' 必须是字符串。")
            : await tool.ExecuteAsync(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["code"] = code },
                cancellationToken).ConfigureAwait(false);
        var envelope = (JsonObject)JsonSerializer.SerializeToNode(
            result, AnalyzeCodeToolJsonContext.Default.CodexToolResult)!;
        var bounded = CodexToolFunctionAdapterFactory.MarkEnvelope(envelope);
        return ParseElement(bounded.ToJsonString());
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CodexToolResult))]
[JsonSerializable(typeof(List<CodeDiagnostic>))]
[JsonSerializable(typeof(CodeDiagnostic[]))]
internal sealed partial class AnalyzeCodeToolJsonContext : JsonSerializerContext;
