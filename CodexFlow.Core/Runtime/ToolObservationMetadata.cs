// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Runtime;

internal readonly struct ToolObservationMetadata(bool isProvided, JObject? value)
{
    public bool IsProvided => isProvided;
    public bool IsReadable => value is not null;

    public static ToolObservationMetadata From(object? metadata)
        => metadata switch
        {
            null => default,
            JObject json => new(true, json),
            JsonElement element => new(true, ReadObject(element)),
            _ => new(true, null)
        };

    public string? GetString(params string[] keys) => ReadString(keys, skipBlank: false);

    public string? GetNonBlankString(params string[] keys) => ReadString(keys, skipBlank: true);

    private string? ReadString(string[] keys, bool skipBlank)
    {
        foreach (var key in keys)
        {
            if (GetToken(key) is JValue { Type: JTokenType.String, Value: string text } &&
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
            if (GetToken(key) is not { } token)
            {
                continue;
            }

            if (token is JValue { Type: JTokenType.String, Value: string text } &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (token.Type == JTokenType.Integer)
            {
                try
                {
                    return (int)token;
                }
                catch (OverflowException)
                {
                    // An alias may still provide an in-range value.
                }
            }
        }

        return null;
    }

    public bool HasField(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (GetToken(key) is not null)
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
            if (GetToken(key) is { Type: not JTokenType.Null })
            {
                return true;
            }
        }

        return false;
    }

    private JToken? GetToken(string key) => value?.GetValue(key, StringComparison.OrdinalIgnoreCase);

    private static JObject? ReadObject(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            using var input = new StringReader(element.GetRawText());
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = null };
            return JObject.Load(reader);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or JsonReaderException)
        {
            return null;
        }
    }
}
