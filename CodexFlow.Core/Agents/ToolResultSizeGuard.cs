// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Bounds what one tool result may add to a run's history. The model is billed for, and limited by, everything a result carries: its
/// output, the display copy of it, and the metadata object that often holds the same data again. Without a bound a handful of large
/// results pass the model's window, and the run ends with a provider error instead of an answer.
/// </summary>
internal static class ToolResultSizeGuard
{
    /// <summary>The most a serialized tool-result envelope may be, in characters as written (a CJK character counts six when escaped).</summary>
    internal const int MaxEnvelopeChars = 32_000;

    /// <summary>Metadata larger than this is replaced by a note; it is for programs, the model reads the output.</summary>
    internal const int MaxMetadataChars = 8_000;

    private const int MinOutputChars = 2_000;

    /// <summary>Returns the envelope itself when it fits, otherwise the same object with its largest parts reduced.</summary>
    internal static JsonNode Apply(JsonNode envelope)
    {
        if (envelope is not JsonObject node || Size(node) <= MaxEnvelopeChars)
        {
            return envelope;
        }

        // 1. The display copy of an output that is already there says nothing new.
        if (Name(node, "display") is { } displayName && Name(node, "output") is { } outputName &&
            node[displayName] is JsonValue display && node[outputName] is JsonValue output &&
            display.TryGetValue<string>(out var displayText) && output.TryGetValue<string>(out var outputText) &&
            string.Equals(displayText?.Trim(), outputText?.Trim(), StringComparison.Ordinal))
        {
            node[displayName] = null;
        }

        // 2. Metadata too large to be useful to the model.
        if (Name(node, "metadata") is { } metadataName && node[metadataName] is { } metadata)
        {
            var size = metadata.ToJsonString().Length;
            if (size > MaxMetadataChars)
            {
                node[metadataName] = new JsonObject { ["omitted"] = true, ["originalChars"] = size };
            }
        }

        if (Size(node) <= MaxEnvelopeChars)
        {
            return node;
        }

        // 3. What is left over is the text: cut the output (and a display that differs from it) to what the envelope can still carry.
        foreach (var key in new[] { "display", "output" })
        {
            if (Name(node, key) is not { } name || node[name] is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null)
            {
                continue;
            }

            var overflow = Size(node) - MaxEnvelopeChars;
            if (overflow <= 0)
            {
                break;
            }

            var cost = Escaped(text);
            var budget = Math.Max(MinOutputChars, cost - overflow - MarkerReserve);
            node[name] = Cut(text, budget);
        }

        // "isOutputTruncated" in the adapter envelope, "truncated" in the gateway's.
        if ((Name(node, "isOutputTruncated") ?? Name(node, "truncated")) is { } flag)
        {
            node[flag] = true;
        }

        return node;
    }

    /// <summary>The same bound for the gateway's dictionary envelope: the text fields are replaced in place when they had to be cut.</summary>
    internal static Dictionary<string, object?> Apply(Dictionary<string, object?> payload)
    {
        // The gateway's wrapped payload contains only text, flags and nulls.
        var node = new JsonObject();
        foreach (var (key, value) in payload)
        {
            node[key] = value switch
            {
                null => null,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                _ => throw new InvalidOperationException("Unexpected value in the gateway tool-result payload.")
            };
        }

        if (Size(node) <= MaxEnvelopeChars)
        {
            return payload;
        }

        var bounded = (JsonObject)Apply(node);
        foreach (var key in payload.Keys.ToArray())
        {
            if (bounded.TryGetPropertyValue(key, out var gone) && gone is null && payload[key] is string)
            {
                payload[key] = null;   // a display copy that said nothing new
            }
            else if (bounded.TryGetPropertyValue(key, out var value) && value is JsonValue json && json.TryGetValue<string>(out var text) &&
                payload[key] is string or null && !string.Equals(payload[key] as string, text, StringComparison.Ordinal))
            {
                payload[key] = text;
            }
            else if (bounded.TryGetPropertyValue(key, out var flag) && flag is JsonValue boolValue && boolValue.TryGetValue<bool>(out var truncated) &&
                     payload[key] is bool)
            {
                payload[key] = truncated;
            }
        }

        return payload;
    }

    private const int MarkerReserve = 160;

    // JsonObject property lookup is case-sensitive; the envelope is camelCase but a caller may have changed that.
    private static string? Name(JsonObject node, string camel) =>
        node.Select(static p => p.Key).FirstOrDefault(key => string.Equals(key, camel, StringComparison.OrdinalIgnoreCase));

    private static int Size(JsonNode node) => node.ToJsonString().Length;

    private static int Escaped(string text) => JsonValue.Create(text)!.ToJsonString().Length;

    /// <summary>
    /// Keeps the start and the end of <paramref name="text"/> so that its escaped length is about <paramref name="budget"/>, and says how
    /// much was left out. Start and end both matter: a command's verdict is at the end, a file's declarations at the start.
    /// </summary>
    private static string Cut(string text, int budget)
    {
        // Largest number of characters whose escaped form fits: escaping makes the cost per character uneven (CJK, quotes, newlines).
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var head = (int)(mid * 0.7);
            if (Escaped(text[..head] + text[^(mid - head)..]) <= budget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (low >= text.Length)
        {
            return text;
        }

        var headLength = (int)(low * 0.7);
        var tailLength = low - headLength;
        var omitted = text.Length - headLength - tailLength;
        return text[..headLength] +
               $"\n[… {omitted} characters omitted from the middle of this result; ask for a narrower range or a more specific query …]\n" +
               text[^tailLength..];
    }
}
