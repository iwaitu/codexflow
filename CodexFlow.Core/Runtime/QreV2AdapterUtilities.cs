// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

internal static class QreV2AdapterUtilities
{
    internal static string ResolveTrustedWorkspace(CodexSession session)
    {
        var execution = new TrustedExecutionContextAccessor().Current;
        if (execution is null)
            return ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);
        if (!string.Equals(execution.SessionId, session.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Trusted execution context belongs to a different session.");
        return execution.WorkspaceRoot;
    }
    private static JsonElement AdaptToolSchema(JsonElement schema)
    {
        // MEAI emits `true` for unconstrained parameters. QRE accepts the
        // equivalent empty object schema, but not boolean schema nodes.
        var node = JsonNode.Parse(schema.GetRawText());
        AdaptSchemaNode(node);
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static JsonNode? AdaptSchemaNode(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var unconstrained) && unconstrained)
        {
            return new JsonObject();
        }

        if (node is JsonObject schema)
        {
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var name in properties.Select(property => property.Key).ToArray())
                {
                    properties[name] = AdaptSchemaNode(properties[name]);
                }
            }
            if (schema.ContainsKey("items"))
            {
                schema["items"] = AdaptSchemaNode(schema["items"]);
            }
        }

        return node;
    }

    public static VllmChatOptions CloneChatOptions(ChatOptions? source)
    {
        var destination = new VllmChatOptions();
        if (source == null)
        {
            return destination;
        }

        destination.ConversationId = source.ConversationId;
        destination.Instructions = source.Instructions;
        destination.Temperature = source.Temperature;
        destination.MaxOutputTokens = source.MaxOutputTokens;
        destination.TopP = source.TopP;
        destination.TopK = source.TopK;
        destination.FrequencyPenalty = source.FrequencyPenalty;
        destination.PresencePenalty = source.PresencePenalty;
        destination.Seed = source.Seed;
        destination.Reasoning = source.Reasoning;
        destination.ResponseFormat = source.ResponseFormat;
        destination.ModelId = source.ModelId;
        destination.StopSequences = source.StopSequences?.ToArray();
        destination.AllowMultipleToolCalls = source.AllowMultipleToolCalls;
        destination.ToolMode = source.ToolMode;
        destination.Tools = source.Tools?.ToList();
        destination.RawRepresentationFactory = source.RawRepresentationFactory;
        destination.AdditionalProperties = source.AdditionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(source.AdditionalProperties);

        if (source is VllmChatOptions vllmSource)
        {
            destination.ThinkingEnabled = vllmSource.ThinkingEnabled;
            destination.EnableSkills = vllmSource.EnableSkills;
            destination.SkillDirectoryPath = vllmSource.SkillDirectoryPath;
            destination.EnableLegacyToolCallTextFallback = vllmSource.EnableLegacyToolCallTextFallback;
        }

        return destination;
    }

    public sealed class TrustedRuntimeArgumentsAIFunction(
        AIFunction inner,
        CodexSession? session) : AIFunction
    {
        private readonly JsonElement _jsonSchema = AdaptToolSchema(inner.JsonSchema);
        private readonly bool _acceptsSessionId = AcceptsArgument(inner.JsonSchema, "session_id");
        private readonly bool _acceptsWorkspacePath = AcceptsArgument(inner.JsonSchema, "workspace_path");
        private readonly bool _acceptsProjectRoot = AcceptsArgument(inner.JsonSchema, "project_root");

        public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        public override JsonElement JsonSchema => _jsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var trustedArguments = new AIFunctionArguments(arguments)
            {
                Services = arguments.Services,
                Context = arguments.Context
            };
            InjectTrustedRuntimeArguments(trustedArguments);
            return await inner.InvokeAsync(trustedArguments, cancellationToken).ConfigureAwait(false);
        }

        private void InjectTrustedRuntimeArguments(AIFunctionArguments args)
        {
            if (session == null)
            {
                return;
            }

            if (_acceptsSessionId)
            {
                args["session_id"] = session.Id;
            }
            if (_acceptsWorkspacePath)
            {
                args["workspace_path"] = new TrustedExecutionContextAccessor().Current is null
                    ? session.WorkspacePath : ResolveTrustedWorkspace(session);
            }
            if (_acceptsProjectRoot)
            {
                args["project_root"] = ResolveTrustedWorkspace(session);
            }
        }

        private static bool AcceptsArgument(JsonElement schema, string argumentName)
            => schema.ValueKind == JsonValueKind.Object &&
               (SchemaPropertiesContain(schema, argumentName) ||
                schema.TryGetProperty("parameters", out var parameters) &&
                SchemaPropertiesContain(parameters, argumentName));

        private static bool SchemaPropertiesContain(JsonElement schema, string argumentName)
            => schema.ValueKind == JsonValueKind.Object &&
               schema.TryGetProperty("properties", out var properties) &&
               properties.ValueKind == JsonValueKind.Object &&
               properties.TryGetProperty(argumentName, out _);
    }
}
