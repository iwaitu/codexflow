// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Agents;

internal static class RuntimeToolArgumentJson
{
    internal static string? TrySerialize(object? arguments)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var writer = new JsonTextWriter(output);
        try
        {
            WriteValue(writer, arguments, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return output.ToString();
        }
        catch (JsonSerializationException)
        {
            return null;
        }
    }

    internal static JToken SerializeForDiagnostics(object? arguments)
        => TrySerialize(arguments) is { } json
            ? new JRaw(json)
            : new JValue("(Unsupported tool argument value)");

    internal static Dictionary<string, object?> ReadObject(JObject obj)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var property in obj.Properties())
        {
            arguments[property.Name] = property.Value is JValue scalar ? scalar.Value : property.Value;
        }

        return arguments;
    }

    private static void WriteValue(JsonTextWriter writer, object? value, HashSet<object> ancestors)
    {
        switch (value)
        {
            case JToken token:
                token.WriteTo(writer);
                break;
            case JsonElement element:
                // Preserve Json.NET's legacy diagnostic representation before tool argument normalization.
                new JObject { [nameof(JsonElement.ValueKind)] = (int)element.ValueKind }.WriteTo(writer);
                break;
            case JsonDocument document:
                new JObject
                {
                    [nameof(JsonDocument.RootElement)] = new JObject { [nameof(JsonElement.ValueKind)] = (int)document.RootElement.ValueKind }
                }.WriteTo(writer);
                break;
            case null or string or char or bool or sbyte or byte or short or ushort or int or uint or long or ulong or
                 float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Uri or byte[]:
                writer.WriteValue(value);
                break;
            case BigInteger bignum:
                writer.WriteRawValue(bignum.ToString(CultureInfo.InvariantCulture));
                break;
            case Enum enumeration:
                writer.WriteValue(Convert.ChangeType(enumeration, Enum.GetUnderlyingType(enumeration.GetType()), CultureInfo.InvariantCulture));
                break;
            case IEnumerable<KeyValuePair<string, object?>> properties:
                EnterContainer(ancestors, properties);
                writer.WriteStartObject();
                foreach (var (name, item) in properties)
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, item, ancestors);
                }

                writer.WriteEndObject();
                ancestors.Remove(properties);
                break;
            case IDictionary dictionary:
                EnterContainer(ancestors, dictionary);
                writer.WriteStartObject();
                foreach (DictionaryEntry property in dictionary)
                {
                    writer.WritePropertyName(Convert.ToString(property.Key, CultureInfo.InvariantCulture)!);
                    WriteValue(writer, property.Value, ancestors);
                }

                writer.WriteEndObject();
                ancestors.Remove(dictionary);
                break;
            case IEnumerable items:
                EnterContainer(ancestors, items);
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(writer, item, ancestors);
                }

                writer.WriteEndArray();
                ancestors.Remove(items);
                break;
            default:
                throw new JsonSerializationException("Unsupported tool argument value.");
        }
    }

    private static void EnterContainer(HashSet<object> ancestors, object container)
    {
        if (!ancestors.Add(container))
        {
            throw new JsonSerializationException("Tool arguments contain a recursive value.");
        }
    }
}
