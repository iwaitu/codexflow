// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Agents;

internal sealed class SourceGeneratedToolFunction(
    ICodexTool tool,
    string name,
    string description,
    JsonElement schema,
    Func<AIFunctionArguments, Dictionary<string, object?>> bind) : AIFunction
{
    private static readonly SourceGeneratedToolJsonContext JsonContext = SourceGeneratedToolJsonContext.Default;
    private static readonly JsonElement ReturnSchema = ParseElement("true");
    private static readonly JsonElement AnalyzeSchema = ParseElement(
        """{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}""");
    private static readonly JsonElement ReadSchema = ParseElement("""
        {"type":"object","properties":{"path":{"type":"string"},"mode":{"type":["string","null"],"default":null},"start_line":{"type":["integer","null"],"default":null},"end_line":{"type":["integer","null"],"default":null},"window_start_line":{"type":["integer","null"],"default":null},"window_line_count":{"type":["integer","null"],"default":null},"session_id":{"type":["string","null"],"default":null},"workspace_path":{"type":["string","null"],"default":null},"project_root":{"type":["string","null"],"default":null}},"required":["path"]}
        """);
    private static readonly JsonElement SearchSchema = ParseElement("""
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":["string","null"],"default":null},"ignore_case":{"type":["boolean","null"],"default":null},"max_results":{"type":["integer","null"],"default":null},"session_id":{"type":["string","null"],"default":null},"workspace_path":{"type":["string","null"],"default":null},"project_root":{"type":["string","null"],"default":null}},"required":["pattern"]}
        """);

    internal static AIFunction AnalyzeCode(ICodexTool tool, string description)
        => new SourceGeneratedToolFunction(tool, "analyze_code", description, AnalyzeSchema, BindAnalyzeCode);

    internal static AIFunction ReadFile(ICodexTool tool, string description)
        => new SourceGeneratedToolFunction(tool, "ivilson_read", description, ReadSchema, BindReadFile);

    internal static AIFunction SearchFiles(ICodexTool tool, string description)
        => new SourceGeneratedToolFunction(tool, "search_in_files", description, SearchSchema, BindSearchFiles);

    public override string Name => name;
    public override string Description => description;
    public override JsonElement JsonSchema => schema;
    public override JsonElement? ReturnJsonSchema => ReturnSchema;
    public override JsonSerializerOptions JsonSerializerOptions => JsonContext.Options;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, object?> bound;
        try
        {
            bound = bind(arguments);
        }
        catch (JsonException exception)
        {
            return EncodeResult(CodexToolResult.Error(exception.Message));
        }

        return EncodeResult(await tool.ExecuteAsync(bound, cancellationToken).ConfigureAwait(false));
    }

    private static JsonElement EncodeResult(CodexToolResult result)
    {
        if (result.Metadata is { } metadata && JsonContext.GetTypeInfo(metadata.GetType()) is null)
        {
            // Allow existing managed plug-ins to attach metadata outside the built-in tool contracts.
            if (JsonSerializer.IsReflectionEnabledByDefault)
            {
                return ParseElement(CodexToolFunctionAdapterFactory.MarkResult(result).ToJsonString());
            }

            result = CodexToolResult.Error("工具返回了未注册的结果元数据类型。");
        }

        var envelope = (JsonObject)JsonSerializer.SerializeToNode(result, JsonContext.CodexToolResult)!;
        return ParseElement(CodexToolFunctionAdapterFactory.MarkEnvelope(envelope).ToJsonString());
    }

    private static Dictionary<string, object?> BindAnalyzeCode(AIFunctionArguments input)
        => new(StringComparer.OrdinalIgnoreCase) { ["code"] = RequireString(input, "code") };

    private static Dictionary<string, object?> BindReadFile(AIFunctionArguments input)
    {
        var args = WorkspaceArgs(input);
        args["path"] = RequireString(input, "path");
        AddWhenPresent(args, "mode", ReadArgument(input, "mode", JsonContext.String));
        AddWhenPresent(args, "start_line", ReadArgument(input, "start_line", JsonContext.NullableInt32));
        AddWhenPresent(args, "end_line", ReadArgument(input, "end_line", JsonContext.NullableInt32));
        AddWhenPresent(args, "window_start_line", ReadArgument(input, "window_start_line", JsonContext.NullableInt32));
        AddWhenPresent(args, "window_line_count", ReadArgument(input, "window_line_count", JsonContext.NullableInt32));
        return args;
    }

    private static Dictionary<string, object?> BindSearchFiles(AIFunctionArguments input)
    {
        var args = WorkspaceArgs(input);
        args["pattern"] = RequireString(input, "pattern");
        args["path"] = ReadArgument(input, "path", JsonContext.String) ?? ".";
        args["ignore_case"] = ReadArgument(input, "ignore_case", JsonContext.NullableBoolean) ?? true;
        args["max_results"] = ReadArgument(input, "max_results", JsonContext.NullableInt32) ?? 50;
        return args;
    }

    private static Dictionary<string, object?> WorkspaceArgs(AIFunctionArguments input)
        => CodexToolFunctionAdapterFactory.CreateArgs(
            ReadArgument(input, "session_id", JsonContext.String),
            ReadArgument(input, "workspace_path", JsonContext.String),
            ReadArgument(input, "project_root", JsonContext.String));

    private static string RequireString(AIFunctionArguments input, string key)
        => ReadArgument(input, key, JsonContext.String)
            ?? throw new JsonException($"必需的参数 '{key}' 必须是字符串。");

    private static void AddWhenPresent(Dictionary<string, object?> args, string key, object? value)
    {
        if (value is not null)
        {
            args[key] = value;
        }
    }

    private static T? ReadArgument<T>(AIFunctionArguments input, string key, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!input.TryGetValue(key, out var value) || value is null)
        {
            return default;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        try
        {
            if (value is JsonElement element)
            {
                return element.Deserialize(jsonTypeInfo);
            }

            if (value is JsonNode node)
            {
                return node.Deserialize(jsonTypeInfo);
            }

            // QRE commonly supplies JSON integers as boxed Int64. Use the same JSON conversion as the old binder.
            if (JsonContext.GetTypeInfo(value.GetType()) is { } valueTypeInfo)
            {
                return JsonSerializer.SerializeToElement(value, valueTypeInfo).Deserialize(jsonTypeInfo);
            }
        }
        catch (JsonException exception)
        {
            throw new JsonException($"参数 '{key}' 的类型或数值范围不正确。", exception);
        }

        throw new JsonException($"参数 '{key}' 的类型或数值范围不正确。");
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
[JsonSerializable(typeof(TextReadMetadata))]
[JsonSerializable(typeof(DocumentReadMetadata))]
[JsonSerializable(typeof(ImageReadMetadata))]
[JsonSerializable(typeof(HashlineReadMetadata))]
[JsonSerializable(typeof(SearchInFilesMetadata))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
internal sealed partial class SourceGeneratedToolJsonContext : JsonSerializerContext;
