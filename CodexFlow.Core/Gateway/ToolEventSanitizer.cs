// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Gateway;

/// <summary>
/// Sanitizes and bounds tool call arguments and tool result snippets before emitting them over SSE or snapshots.
/// </summary>
public static partial class ToolEventSanitizer
{
    public const int DefaultMaxStringChars = 2048;
    public const int DefaultMaxTotalJsonChars = 16 * 1024;
    public const string RedactedValue = "[REDACTED]";

    private static readonly string[] SensitiveKeyTokens =
    [
        "token",
        "secret",
        "password",
        "passwd",
        "apikey",
        "api_key",
        "authorization",
        "credential",
        "privatekey",
        "private_key",
        "cookie",
        "bearer"
    ];

    public static (IReadOnlyDictionary<string, object?>? Arguments, bool Truncated) SanitizeArguments(
        IReadOnlyDictionary<string, object?>? arguments,
        int maxStringChars = DefaultMaxStringChars,
        int maxTotalJsonChars = DefaultMaxTotalJsonChars)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return (null, false);
        }

        var truncated = false;
        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            if (IsSensitiveKey(key))
            {
                sanitized[key] = RedactedValue;
                continue;
            }

            sanitized[key] = SanitizeValue(value, maxStringChars, depth: 0, ref truncated);
        }

        var serialized = JsonSerializer.Serialize(sanitized);
        if (serialized.Length > maxTotalJsonChars)
        {
            truncated = true;
            var compact = new Dictionary<string, object?>(StringComparer.Ordinal);
            var budget = Math.Max(256, maxTotalJsonChars - 128);
            var used = 2;
            foreach (var (key, value) in sanitized)
            {
                var entryJson = JsonSerializer.Serialize(value);
                var entryCost = key.Length + entryJson.Length + 6;
                if (used + entryCost <= budget)
                {
                    compact[key] = value;
                    used += entryCost;
                }
                else
                {
                    compact[key] = "[TRUNCATED]";
                    break;
                }
            }

            return (compact, true);
        }

        return (sanitized, truncated);
    }

    public static (string Text, bool Truncated) TruncateText(string? text, int maxChars = DefaultMaxStringChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, false);
        }

        var redacted = RedactInlineSecrets(text);
        if (redacted.Length <= maxChars)
        {
            return (redacted, false);
        }

        return (string.Concat(redacted.AsSpan(0, maxChars), "...[truncated]"), true);
    }

    private static object? SanitizeValue(object? value, int maxStringChars, int depth, ref bool truncated)
    {
        if (value is null)
        {
            return null;
        }

        if (depth >= 5)
        {
            truncated = true;
            return "[TRUNCATED_DEPTH]";
        }

        switch (value)
        {
            case string s:
            {
                var (clean, didTruncate) = TruncateText(s, maxStringChars);
                if (didTruncate)
                {
                    truncated = true;
                }

                return clean;
            }
            case JsonElement el:
                return SanitizeJsonElement(el, maxStringChars, depth, ref truncated);
            case IReadOnlyDictionary<string, object?> dict:
            {
                var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in dict)
                {
                    nested[k] = IsSensitiveKey(k)
                        ? RedactedValue
                        : SanitizeValue(v, maxStringChars, depth + 1, ref truncated);
                }

                return nested;
            }
            case IDictionary genericDict:
            {
                var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in genericDict)
                {
                    var key = entry.Key?.ToString() ?? string.Empty;
                    nested[key] = IsSensitiveKey(key)
                        ? RedactedValue
                        : SanitizeValue(entry.Value, maxStringChars, depth + 1, ref truncated);
                }

                return nested;
            }
            case IEnumerable list when value is not string:
            {
                var items = new List<object?>();
                var count = 0;
                foreach (var item in list)
                {
                    if (count >= 50)
                    {
                        truncated = true;
                        items.Add("...[truncated]");
                        break;
                    }

                    items.Add(SanitizeValue(item, maxStringChars, depth + 1, ref truncated));
                    count++;
                }

                return items;
            }
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return value;
            default:
            {
                var (clean, didTruncate) = TruncateText(value.ToString(), maxStringChars);
                if (didTruncate)
                {
                    truncated = true;
                }

                return clean;
            }
        }
    }

    private static object? SanitizeJsonElement(JsonElement el, int maxStringChars, int depth, ref bool truncated)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return el.TryGetInt64(out var l) ? l : el.GetDouble();
            case JsonValueKind.String:
            {
                var (clean, didTruncate) = TruncateText(el.GetString(), maxStringChars);
                if (didTruncate)
                {
                    truncated = true;
                }

                return clean;
            }
            case JsonValueKind.Array:
            {
                var items = new List<object?>();
                var count = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (count >= 50)
                    {
                        truncated = true;
                        items.Add("...[truncated]");
                        break;
                    }

                    items.Add(SanitizeJsonElement(item, maxStringChars, depth + 1, ref truncated));
                    count++;
                }

                return items;
            }
            case JsonValueKind.Object:
            {
                var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in el.EnumerateObject())
                {
                    nested[prop.Name] = IsSensitiveKey(prop.Name)
                        ? RedactedValue
                        : SanitizeJsonElement(prop.Value, maxStringChars, depth + 1, ref truncated);
                }

                return nested;
            }
            default:
                return el.ToString();
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var token in SensitiveKeyTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string RedactInlineSecrets(string text)
    {
        var redacted = BearerSecretRegex().Replace(text, "$1 [REDACTED]");
        redacted = PrefixedSecretRegex().Replace(redacted, "[REDACTED]");
        redacted = KeyValueSecretRegex().Replace(redacted, "$1[REDACTED]");
        redacted = CliFlagSecretRegex().Replace(redacted, "$1$2[REDACTED]");
        return redacted;
    }

    [GeneratedRegex(@"\b(Bearer|Token)\s+[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(@"\b(?:(?:sk|glpat)[-_][A-Za-z0-9_\-]{12,}|(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{12,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedSecretRegex();

    [GeneratedRegex(@"((?:^|[?&;\s,])(?:api[-_]?key|access[-_]?token|control[-_]?token|refresh[-_]?token|client[-_]?secret|secret|password|passwd|token)=)([^&\s'""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(--?(?:api[-_]?key|access[-_]?token|control[-_]?token|secret|password|passwd|token))(\s+)([^\s'""]+|'[^']*'|\""[^\""]*\"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliFlagSecretRegex();
}
