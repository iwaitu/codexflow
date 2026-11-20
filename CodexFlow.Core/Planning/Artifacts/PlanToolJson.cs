// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Patching;
using Newtonsoft.Json.Linq;
using LegacyJson = Newtonsoft.Json;

namespace CodexFlow.Core.Planning.Artifacts;

internal static class PlanToolJson
{
    private static readonly JsonSerializerOptions Compact = CreateOptions(indented: false);
    private static readonly JsonSerializerOptions Indented = CreateOptions(indented: true);

    public static string Write<T>(T value, bool indented = false)
        => JsonSerializer.Serialize(value, TypeInfo<T>(indented ? Indented : Compact));

    public static PlanArtifact Clone(PlanArtifact artifact)
    {
        var type = TypeInfo<PlanArtifact>(Compact);
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(artifact, type), type) ?? new PlanArtifact();
    }

    public static T? Read<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        var type = TypeInfo<T>(Compact);
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A low-level reader retains support for the legacy single-quoted JSON and the planner's syntax-repair diagnostics.
            using var reader = new LegacyJson.JsonTextReader(new StringReader(json))
            {
                DateParseHandling = LegacyJson.DateParseHandling.None
            };
            var token = JToken.ReadFrom(reader);
            while (reader.Read())
            {
                if (reader.TokenType != LegacyJson.JsonToken.Comment)
                {
                    throw new LegacyJson.JsonSerializationException("Unexpected content after the planning payload.");
                }
            }

            if (token is JValue { Type: JTokenType.String, Value: "" })
            {
                return default;
            }

            IgnoreNullCollections<T>(token);
            try
            {
                return JsonSerializer.Deserialize(token.ToString(LegacyJson.Formatting.None), type);
            }
            catch (Exception fallbackError) when (fallbackError is JsonException or InvalidOperationException)
            {
                throw new LegacyJson.JsonSerializationException(fallbackError.Message, fallbackError);
            }
        }
    }

    private static void IgnoreNullCollections<T>(JToken token)
    {
        if (token is not JArray rows)
        {
            return;
        }

        if (typeof(T) == typeof(List<CodexTask>))
        {
            foreach (var task in rows.OfType<JObject>())
            {
                // Json.NET keeps getter-only collections initialized when the payload explicitly supplies null.
                RemoveNullCollections(task,
                    nameof(CodexTask.Dependencies), nameof(CodexTask.Inputs), nameof(CodexTask.Outputs),
                    nameof(CodexTask.ChecklistItems), nameof(CodexTask.RequiredArtifacts), nameof(CodexTask.ForbiddenStates));
                if (task.GetValue(nameof(CodexTask.ChecklistItems), StringComparison.OrdinalIgnoreCase) is JArray checklist)
                {
                    IgnoreNullChecklistCollections(checklist);
                }
            }
        }
        else if (typeof(T) == typeof(List<TaskChecklistItem>))
        {
            IgnoreNullChecklistCollections(rows);
        }
    }

    private static void IgnoreNullChecklistCollections(JArray checklist)
    {
        foreach (var item in checklist.OfType<JObject>())
        {
            RemoveNullCollections(item, nameof(TaskChecklistItem.Evidence), nameof(TaskChecklistItem.RelatedAssertions));
        }
    }

    private static void RemoveNullCollections(JObject value, params string[] names)
    {
        foreach (var property in value.Properties().Where(property =>
            property.Value.Type == JTokenType.Null && names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            property.Remove();
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions options)
        => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = Environment.NewLine,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            TypeInfoResolver = PlanToolJsonContext.Default.WithAddedModifier(PopulateCollections),
            WriteIndented = indented
        };
        options.Converters.Add(new PlanningEnumConverter<CodexTaskStatus>());
        options.Converters.Add(new PlanningEnumConverter<TaskChecklistItemStatus>());
        options.Converters.Add(new PlanningStringConverter());
        options.Converters.Add(new PlanningBooleanConverter());
        options.MakeReadOnly();
        return options;
    }

    private static void PopulateCollections(JsonTypeInfo type)
    {
        if (type.Type != typeof(CodexTask) && type.Type != typeof(TaskChecklistItem) &&
            type.Type != typeof(PlanArtifact) && type.Type != typeof(PlanProjectionRecord))
        {
            return;
        }

        foreach (var property in type.Properties)
        {
            if (property.Set is null)
            {
                property.ObjectCreationHandling = JsonObjectCreationHandling.Populate;
            }
        }
    }

    private sealed class PlanningEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), number);
            }

            throw new JsonException($"Invalid {typeToConvert.Name} value.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            => writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private sealed class PlanningStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
                _ => throw new JsonException("Expected a string or primitive planning value.")
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WritePropertyName(value);
    }

    private sealed class PlanningBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Number => reader.GetDouble() != 0,
                JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
                _ => throw new JsonException("Expected a boolean planning value.")
            };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }
}

internal sealed record PlanTaskListPayload(
    string SessionId,
    string? PlanVersion,
    DateTime? PlanGeneratedAtUtc,
    Collection<CodexTask> Tasks,
    string? SnapshotJson);

internal sealed record PlanPatchResolutionPayload(
    [property: JsonPropertyName("result")] PlanPatchResult Result,
    List<string> CleanupWarnings,
    string? SnapshotJson,
    Dictionary<string, string> Metadata);

internal sealed record ExecuteCodeTaskPayload(bool Success, string Message, string TaskId);

internal sealed record RunTestsPayload(
    bool Success,
    int PassedCount,
    int FailedCount,
    Collection<string> FailureDetails,
    string Summary);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PlanArtifact))]
[JsonSerializable(typeof(List<CodexTask>))]
[JsonSerializable(typeof(IReadOnlyList<CodexTask>))]
[JsonSerializable(typeof(PlanDiffResult))]
[JsonSerializable(typeof(List<TaskChecklistItem>))]
[JsonSerializable(typeof(PlanTaskListPayload))]
[JsonSerializable(typeof(PlanPatchResolutionPayload))]
[JsonSerializable(typeof(ExecuteCodeTaskPayload))]
[JsonSerializable(typeof(RunTestsPayload))]
internal sealed partial class PlanToolJsonContext : JsonSerializerContext;
