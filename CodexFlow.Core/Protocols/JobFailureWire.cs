// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Protocols;

/// <summary>A <see cref="JobFailure"/> as the single string that travels in notification metadata.</summary>
public static class JobFailureWire
{
    /// <summary>The failure as JSON, with the kind and the remedy by name.</summary>
    public static string ToJson(JobFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var root = new JObject
        {
            [nameof(JobFailure.Code)] = failure.Code,
            [nameof(JobFailure.Kind)] = Enum.GetName(failure.Kind) is { } kind ? new JValue(kind) : new JValue((int)failure.Kind),
            [nameof(JobFailure.Remedy)] = Enum.GetName(failure.Remedy) is { } remedy ? new JValue(remedy) : new JValue((int)failure.Remedy),
            [nameof(JobFailure.Message)] = failure.Message,
            [nameof(JobFailure.RefusedPaths)] = failure.RefusedPaths is { } paths ? new JArray(paths) : null,
            [nameof(JobFailure.Evidence)] = failure.Evidence is { } evidence ? new JArray(evidence) : null,
            [nameof(JobFailure.Retryable)] = failure.Retryable
        };

        foreach (var property in root.Properties().Where(static property => property.Value is JValue { Value: null }).ToArray())
        {
            property.Remove();
        }

        return root.ToString(Formatting.None);
    }

    /// <summary>The failure back from JSON; null when the text is missing or not a failure (a notice must still go out without it).</summary>
    public static JobFailure? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            if (!ReadContent(reader) || reader.TokenType != JsonToken.StartObject)
            {
                return null;
            }

            string? code = null;
            JobFailureKind kind = default;
            JobFailureRemedy remedy = default;
            string? message = null;
            IReadOnlyList<string>? refusedPaths = null;
            IReadOnlyList<string>? evidence = null;
            var readingMetadata = true;

            while (ReadContent(reader) && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string)reader.Value!;
                if (readingMetadata)
                {
                    // Preserve ordinary Json.NET metadata handling without resolving CLR types or references.
                    if (name is "$values")
                    {
                        return null;
                    }

                    if (name is "$ref")
                    {
                        if (!ReadContent(reader) || reader.TokenType != JsonToken.Null)
                        {
                            return null;
                        }

                        continue;
                    }

                    if (name is "$type" or "$id")
                    {
                        var metadataValue = reader.ReadAsString();
                        if (name == "$type" && metadataValue is null)
                        {
                            return null;
                        }

                        continue;
                    }

                    readingMetadata = false;
                }

                switch (name.ToUpperInvariant())
                {
                    case "CODE":
                        code = reader.ReadAsString();
                        break;
                    case "KIND":
                        kind = ReadEnum<JobFailureKind>(reader);
                        break;
                    case "REMEDY":
                        remedy = ReadEnum<JobFailureRemedy>(reader);
                        break;
                    case "MESSAGE":
                        message = reader.ReadAsString();
                        break;
                    case "REFUSEDPATHS":
                        refusedPaths = ReadStrings(reader);
                        break;
                    case "EVIDENCE":
                        evidence = ReadStrings(reader);
                        break;
                    case "RETRYABLE":
                        // Validate the historical wire field, but always derive the flag from Remedy.
                        if (reader.ReadAsBoolean() is null)
                        {
                            return null;
                        }

                        break;
                    default:
                        // With an unknown field, a comment before its value was not accepted by the old reader.
                        if (!reader.Read() || reader.TokenType == JsonToken.Comment)
                        {
                            return null;
                        }

                        reader.Skip();
                        break;
                }
            }

            return reader.TokenType == JsonToken.EndObject && !ReadContent(reader)
                ? new JobFailure(code!, kind, remedy, message!, refusedPaths!, evidence!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadContent(JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.Comment)
            {
                return true;
            }
        }

        return false;
    }

    private static TEnum ReadEnum<TEnum>(JsonReader reader) where TEnum : struct, Enum
    {
        if (ReadContent(reader))
        {
            if (reader.TokenType == JsonToken.Integer && reader.Value is long number)
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), number);
            }

            if (reader.TokenType == JsonToken.String && Enum.TryParse<TEnum>((string?)reader.Value, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        throw new JsonSerializationException("Invalid job failure enum value.");
    }

    private static List<string>? ReadStrings(JsonReader reader)
    {
        if (!ReadContent(reader) || reader.TokenType is JsonToken.Null or JsonToken.Undefined)
        {
            return null;
        }

        if (reader.TokenType != JsonToken.StartArray)
        {
            throw new JsonSerializationException("Expected a job failure string array.");
        }

        var values = new List<string>();
        while (true)
        {
            var value = reader.ReadAsString();
            switch (reader.TokenType)
            {
                case JsonToken.EndArray:
                    return values;
                case JsonToken.String:
                case JsonToken.Null:
                    values.Add(value!);
                    break;
                default:
                    throw new JsonSerializationException("Invalid job failure string array.");
            }
        }
    }
}
