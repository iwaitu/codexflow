// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Core.Agents;

namespace CodexFlow.Core.Models;

/// <summary>Creates JSON metadata for a tool result without runtime reflection.</summary>
public static class ToolResultMetadata
{
    private static readonly JsonElement DisposedDocument = Omitted("metadata_document_disposed");
    private static readonly JsonElement SerializationFailed = Omitted("serialization_failed");

    /// <summary>Snapshots dynamic metadata already expressed as JSON.</summary>
    public static JsonElement FromJson(JsonNode? value) => From(value, SourceGeneratedToolJsonContext.Default.JsonNode);

    /// <summary>Converts typed metadata using the caller's explicit JSON contract.</summary>
    public static JsonElement From<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            return JsonSerializer.SerializeToElement(value, typeInfo);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or ArgumentException or InvalidOperationException)
        {
            return SerializationFailed;
        }
    }

    internal static JsonNode? ToNode<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonNode.Parse(From(value, typeInfo).GetRawText());

    internal static JsonArray StringArray(IEnumerable<string> values)
        => new(values.Select(static value => (JsonNode?)value).ToArray());

    internal static JsonElement? Own(JsonElement? value)
    {
        try
        {
            return value is { } element && element.ValueKind != JsonValueKind.Undefined ? element.Clone() : null;
        }
        catch (ObjectDisposedException)
        {
            return DisposedDocument;
        }
    }

    private static JsonElement Omitted(string reason) => JsonSerializer.SerializeToElement(
        new JsonObject { ["omitted"] = true, ["reason"] = reason },
        SourceGeneratedToolJsonContext.Default.JsonObject);
}
