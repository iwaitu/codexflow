// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Runtime;

internal readonly struct ToolObservationMetadata
{
    private readonly object? _value;

    private ToolObservationMetadata(object? value)
    {
        IsProvided = true;
        _value = value;
    }

    public bool IsProvided { get; }
    public bool IsReadable => _value is not null;

    public static ToolObservationMetadata From(object? metadata)
        => metadata switch
        {
            null => default,
            JObject json => new(json),
            JsonElement element => new(CloneObject(element)),
            _ => new(null)
        };

    public string? GetString(params string[] keys) => ReadString(keys, skipBlank: false);

    public string? GetNonBlankString(params string[] keys) => ReadString(keys, skipBlank: true);

    private string? ReadString(string[] keys, bool skipBlank)
    {
        foreach (var key in keys)
        {
            if (GetField(key).Text is { } text &&
                (!skipBlank || !string.IsNullOrWhiteSpace(text)))
            {
                return text;
            }
        }

        return null;
    }

    public int? GetInt(params string[] keys)
    {
        foreach (var key in keys)
        {
            var field = GetField(key);
            if (field.Integer is { } integer)
            {
                return integer;
            }

            if (field.Text is { } text &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public bool HasField(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (GetField(key).IsPresent)
            {
                return true;
            }
        }

        return false;
    }

    public bool HasNonNullValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (GetField(key).HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private Field GetField(string key) => _value switch
    {
        JsonElement element => ReadField(FindProperty(element, key)),
        JObject obj => ReadLegacyField(obj.GetValue(key, StringComparison.OrdinalIgnoreCase)),
        _ => default
    };

    private static JsonElement? CloneObject(JsonElement element)
    {
        try
        {
            return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static JsonElement FindProperty(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var value))
        {
            return value;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                // Use the last value for the first spelling, matching the legacy DOM.
                return element.GetProperty(property.Name);
            }
        }

        return default;
    }

    private static Field ReadField(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined => default,
        JsonValueKind.Null => new(true, false),
        JsonValueKind.String => new(true, true, Text: element.GetString()),
        JsonValueKind.Number when element.TryGetInt32(out var number) => new(true, true, Integer: number),
        _ => new(true, true)
    };

    private static Field ReadLegacyField(JToken? token)
    {
        switch (token)
        {
            case null:
                return default;
            case { Type: JTokenType.Null }:
                return new(true, false);
            case JValue { Type: JTokenType.String, Value: string text }:
                return new(true, true, Text: text);
            case { Type: JTokenType.Integer }:
                try
                {
                    return new(true, true, Integer: (int)token);
                }
                catch (OverflowException)
                {
                    // An alias may still provide an in-range value.
                    return new(true, true);
                }
            default:
                return new(true, true);
        }
    }

    private readonly record struct Field(bool IsPresent, bool HasValue, string? Text = null, int? Integer = null);
}
