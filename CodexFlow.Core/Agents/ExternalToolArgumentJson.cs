// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CodexFlow.Core.Agents;

internal static class ExternalToolArgumentJson
{
    private static readonly ExternalToolArgumentJsonContext Web = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static bool TryWriteWeb(object value, [NotNullWhen(true)] out string? json)
    {
        var type = GetTypeInfo(Web, value);
        if (type is not null)
        {
            try
            {
                json = JsonSerializer.Serialize(value, type);
                return true;
            }
            catch (NotSupportedException)
            {
                // A registered container may embed an extension's unregistered CLR type.
            }
        }

        json = null;
        return false;
    }

    public static bool TryWriteElement(object? value, out JsonElement json)
    {
        var type = GetTypeInfo(ExternalToolArgumentJsonContext.Default, value);
        if (type is not null)
        {
            try
            {
                json = JsonSerializer.SerializeToElement(value, type);
                return true;
            }
            catch (NotSupportedException)
            {
                // Delegate unregistered values to the caller's existing CLR contract.
            }
        }

        json = default;
        return false;
    }

    private static JsonTypeInfo? GetTypeInfo(ExternalToolArgumentJsonContext context, object? value)
        => value is JsonNode ? context.JsonNode : context.GetTypeInfo(value?.GetType() ?? typeof(object));
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(Int128))]
[JsonSerializable(typeof(UInt128))]
[JsonSerializable(typeof(Half))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Uri))]
[JsonSerializable(typeof(Version))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonValue))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement[]))]
internal sealed partial class ExternalToolArgumentJsonContext : JsonSerializerContext;
