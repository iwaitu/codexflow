// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Newtonsoft.Json;

namespace CodexFlow.Core.Services;

internal static class ProjectIndexJson
{
    internal static string Serialize(IEnumerable<FileIndexEntry>? entries)
    {
        if (entries is null)
        {
            return "null";
        }

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using var writer = new JsonTextWriter(text);
        writer.WriteStartArray();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                writer.WriteNull();
                continue;
            }

            writer.WriteStartObject();
            writer.WritePropertyName(nameof(FileIndexEntry.Path));
            writer.WriteValue(entry.Path);
            writer.WritePropertyName(nameof(FileIndexEntry.Type));
            writer.WriteValue(entry.Type);
            writer.WritePropertyName(nameof(FileIndexEntry.Size));
            writer.WriteValue(entry.Size);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return text.ToString();
    }

    internal static List<FileIndexEntry>? Deserialize(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text));
        if (!ReadContent(reader))
        {
            return null;
        }

        List<FileIndexEntry>? entries = null;
        if (!IsNullValue(reader))
        {
            if (reader.TokenType != JsonToken.StartArray)
            {
                throw new JsonSerializationException("Expected a project file index array.");
            }

            entries = ReadEntries(reader);
        }

        if (ReadContent(reader))
        {
            throw new JsonSerializationException("Additional content after the project file index.");
        }

        return entries;
    }

    private static List<FileIndexEntry> ReadEntries(JsonReader reader)
    {
        var entries = new List<FileIndexEntry>();
        while (ReadContent(reader))
        {
            if (reader.TokenType == JsonToken.EndArray)
            {
                return entries;
            }

            entries.Add(IsNullValue(reader) ? null! : ReadEntry(reader));
        }

        throw new JsonSerializationException("Unexpected end of the project file index array.");
    }

    private static FileIndexEntry ReadEntry(JsonReader reader)
    {
        if (reader.TokenType != JsonToken.StartObject)
        {
            throw new JsonSerializationException("Expected a project file index entry.");
        }

        // Historical indexes may omit either member despite the current constructor requirements.
        var entry = new FileIndexEntry { Path = null!, Type = null! };
        while (ReadContent(reader))
        {
            if (reader.TokenType == JsonToken.EndObject)
            {
                return entry;
            }

            if (reader.TokenType != JsonToken.PropertyName || reader.Value is not string property)
            {
                throw new JsonSerializationException("Expected a project file index property.");
            }

            switch (property.ToLowerInvariant())
            {
                case "path": entry.Path = reader.ReadAsString()!; break;
                case "type": entry.Type = reader.ReadAsString()!; break;
                case "size": entry.Size = ReadSize(reader); break;
                default: reader.Skip(); break;
            }
        }

        throw new JsonSerializationException("Unexpected end of the project file index entry.");
    }

    private static long ReadSize(JsonReader reader)
    {
        if (!ReadContent(reader) || reader.TokenType is not (JsonToken.Integer or JsonToken.Float or JsonToken.String or JsonToken.Boolean))
        {
            throw new JsonSerializationException("Expected a numeric project file size.");
        }

        try
        {
            return Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            throw new JsonSerializationException("Invalid project file size.", ex);
        }
    }

    private static bool IsNullValue(JsonReader reader)
        => reader.TokenType is JsonToken.Null or JsonToken.Undefined ||
           reader.TokenType == JsonToken.String && reader.Value is "";

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
}
