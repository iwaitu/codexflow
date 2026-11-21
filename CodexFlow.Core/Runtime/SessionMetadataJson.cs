// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;
using Newtonsoft.Json.Linq;
using JsonNet = Newtonsoft.Json;

namespace CodexFlow.Core.Runtime;

internal static class SessionMetadataJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new ScalarStringConverter(),
            new LegacyBooleanConverter(),
            new NumericEnumConverter<QueryLoopEntryPoint>(),
            new NumericEnumConverter<QueryRuntimeLoopPhase>(),
            new NumericEnumConverter<PromptAssemblyFrameKind>()
        },
        TypeInfoResolver = SessionMetadataJsonContext.Default.WithAddedModifier(static info =>
        {
            // Older checkpoints predate some current CLR required members, including the envelope.
            foreach (var property in info.Properties)
            {
                property.IsRequired = false;
            }
        })
    };

    internal static string WriteCheckpoint(RuntimeCheckpointProjection checkpoint)
    {
        var json = JsonSerializer.Serialize(checkpoint, TypeInfo<RuntimeCheckpointProjection>());
        // Keep the historical compact text representation used by the duplicate-write check.
        return ReadLegacyToken(json).ToString(JsonNet.Formatting.None);
    }

    internal static QueryRuntimeCheckpoint? ReadCheckpoint(string json)
    {
        var token = ReadLegacyToken(json);
        if (token.Type == JTokenType.Undefined || token is JValue { Value: "" })
        {
            return null;
        }

        if (token is JObject checkpoint)
        {
            SetDefault(checkpoint, nameof(QueryRuntimeCheckpoint.CreatedAtUtc), new JValue(DateTimeOffset.UtcNow));
            SetDefault(checkpoint, nameof(QueryRuntimeCheckpoint.RequiredToolContractToolNames), new JArray());
            if (checkpoint.GetValue(nameof(QueryRuntimeCheckpoint.EvidenceLedger), StringComparison.OrdinalIgnoreCase) is JObject ledger)
            {
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.Files));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.ToolResults));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.ChangedFiles));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.DiffChanges), nameof(DiffChangeEvidence.Removed), nameof(DiffChangeEvidence.Added));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.Verifications));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.PendingModifications), nameof(PendingModificationEvidence.CandidateFiles));
                FillEvidenceDefaults(ledger, nameof(QueryEvidenceLedgerSnapshot.Failures));
                SetDefault(ledger, nameof(QueryEvidenceLedgerSnapshot.RepeatedEvidenceKeys), new JArray());
                if (ledger.GetValue(nameof(QueryEvidenceLedgerSnapshot.RepeatedEvidenceKeys), StringComparison.OrdinalIgnoreCase) is not JArray)
                {
                    throw new JsonNet.JsonSerializationException("Expected a checkpoint repeated evidence key array.");
                }
            }

            if (checkpoint.GetValue(nameof(QueryRuntimeCheckpoint.LastPromptAssemblySnapshot), StringComparison.OrdinalIgnoreCase) is JObject prompt)
            {
                SetDefault(prompt, nameof(PromptAssemblySnapshot.DroppedFrames), new JArray());
                SetDefault(prompt, nameof(PromptAssemblySnapshot.BudgetDecisions), new JArray());
                if (prompt.GetValue(nameof(PromptAssemblySnapshot.Frames), StringComparison.OrdinalIgnoreCase) is JArray frames)
                {
                    foreach (var frame in frames.OfType<JObject>())
                    {
                        SetDefault(frame, nameof(PromptAssemblyFrameRecord.Compressible), new JValue(true));
                    }
                }
            }
        }

        return JsonSerializer.Deserialize(token.ToString(JsonNet.Formatting.None), TypeInfo<QueryRuntimeCheckpoint>());
    }

    internal static string WriteExecutionUpdates(IEnumerable<string> updates) => new JArray(updates).ToString(JsonNet.Formatting.None);

    internal static List<string>? ReadExecutionUpdates(string json)
    {
        using var input = new StringReader(json);
        using var reader = new JsonNet.JsonTextReader(input);
        if (!ReadContent(reader))
        {
            return null;
        }

        List<string>? updates = null;
        if (reader.TokenType == JsonNet.JsonToken.StartArray)
        {
            updates = [];
            while (true)
            {
                var item = reader.ReadAsString();
                if (reader.TokenType == JsonNet.JsonToken.EndArray)
                {
                    break;
                }

                if (reader.TokenType is not (JsonNet.JsonToken.String or JsonNet.JsonToken.Null))
                {
                    throw new JsonNet.JsonSerializationException("Invalid execution updates array.");
                }

                updates.Add(item!);
            }
        }
        else if (reader.TokenType is not (JsonNet.JsonToken.Null or JsonNet.JsonToken.Undefined) &&
                 (reader.TokenType != JsonNet.JsonToken.String || reader.Value is not ""))
        {
            throw new JsonNet.JsonSerializationException("Expected an execution updates array.");
        }

        EnsureEnd(reader);
        return updates;
    }

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    internal static SemanticRecallTrace? ReadRecallTrace(string json)
    {
        var info = TypeInfo<SemanticRecallTrace>();
        try
        {
            return JsonSerializer.Deserialize(json, info);
        }
        catch (JsonException)
        {
            // Persisted JSON can also contain legacy Json.NET syntax such as single quotes.
            var token = ReadLegacyToken(json);
            return token.Type == JTokenType.Undefined || token is JValue { Value: "" }
                ? default
                : JsonSerializer.Deserialize(token.ToString(JsonNet.Formatting.None), info);
        }
    }

    private static JToken ReadLegacyToken(string json)
    {
        using var input = new StringReader(json);
        using var reader = new JsonNet.JsonTextReader(input) { DateParseHandling = JsonNet.DateParseHandling.None };
        var token = ReadContent(reader) ? JToken.ReadFrom(reader) : JValue.CreateNull();
        EnsureEnd(reader);
        return token;
    }

    private static void FillEvidenceDefaults(JObject ledger, string name, params string[] nestedCollections)
    {
        SetDefault(ledger, name, new JArray());
        if (ledger.GetValue(name, StringComparison.OrdinalIgnoreCase) is not JArray items)
        {
            throw new JsonNet.JsonSerializationException("Expected a checkpoint evidence array.");
        }

        foreach (var item in items)
        {
            if (item is not JObject evidence)
            {
                throw new JsonNet.JsonSerializationException("Expected a checkpoint evidence object.");
            }

            SetDefault(evidence, nameof(FileEvidence.ObservedAt), new JValue(DateTimeOffset.UtcNow));
            foreach (var collection in nestedCollections)
            {
                SetDefault(evidence, collection, new JArray());
            }
        }
    }

    private static void SetDefault(JObject obj, string name, JToken value)
    {
        if (obj.GetValue(name, StringComparison.OrdinalIgnoreCase) is null)
        {
            obj[name] = value;
        }
    }

    private static bool ReadContent(JsonNet.JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonNet.JsonToken.Comment)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureEnd(JsonNet.JsonReader reader)
    {
        if (ReadContent(reader))
        {
            throw new JsonNet.JsonSerializationException("Additional content after session metadata.");
        }
    }

    private sealed class ScalarStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                _ => throw new JsonException("Expected a scalar string value.")
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    private sealed class NumericEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new JsonException("Invalid session metadata enum value.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private sealed class LegacyBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Number => reader.GetDouble() != 0,
                JsonTokenType.String when bool.TryParse(reader.GetString(), out var parsed) => parsed,
                _ => throw new JsonException("Expected a boolean session metadata value.")
            };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }
}

internal sealed record RuntimeCheckpointProjection(
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    QueryRuntimeLoopPhase CurrentPhase,
    DateTimeOffset CreatedAtUtc,
    string? ActiveTaskSummary,
    string? WorkerType,
    string? WorkerDisplayName,
    string? WorkerIsolationMode,
    string? RequiredToolContractName,
    IReadOnlyList<string> RequiredToolContractToolNames,
    string? RequiredToolNameForNextRound,
    bool RequiredToolContractSatisfied,
    int TotalToolCalls,
    int WriteToolCalls,
    int RecoveryCount,
    string? LastContinueReason,
    CheckpointPromptProjection? LastPromptSnapshot,
    QueryEvidenceLedgerSnapshot EvidenceLedger);

internal sealed record CheckpointPromptProjection(
    int Round,
    IReadOnlyList<string> ToolNames,
    string? ToolChoice,
    string? RequiredToolName,
    IReadOnlyList<string> DroppedFrames,
    IReadOnlyList<string> BudgetDecisions);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RuntimeCheckpointProjection))]
[JsonSerializable(typeof(QueryRuntimeCheckpoint))]
[JsonSerializable(typeof(SemanticRecallTrace))]
internal sealed partial class SessionMetadataJsonContext : JsonSerializerContext;
