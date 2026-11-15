// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace CodexFlow.Logging;

internal static class FullTextAuditLogger
{
    private const string RedactedValue = "[REDACTED]";

    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.Ordinal)
    {
        "accesstoken",
        "apikey",
        "authorization",
        "bearer",
        "clientsecret",
        "idtoken",
        "password",
        "refreshtoken",
        "secret",
        "token"
    };

    private static readonly Regex SensitiveQueryParameterRegex = new(
        @"((?:[?&]|^)(?:access_token|refresh_token|id_token|client_secret|api_key|apikey|password|secret|token)=)([^&#\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"\bBearer\s+[A-Za-z0-9\-._~+/]+=*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Log(
        ILogger logger,
        string eventType,
        string sessionId,
        int stage,
        int round,
        object payload)
    {
        try
        {
            var record = new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["eventType"] = eventType,
                ["sessionId"] = sessionId,
                ["stage"] = stage,
                ["round"] = round,
                ["payload"] = payload == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(payload, JsonSerializer.CreateDefault())
            };

            RedactToken(record);
            logger.LogInformation("FULLTEXT|{AuditJson}", record.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to write fulltext audit log. eventType={EventType}, sessionId={SessionId}, round={Round}",
                eventType, sessionId, round);
        }
    }

    private static void RedactToken(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsSensitivePropertyName(property.Name))
                    {
                        property.Value = RedactedValue;
                        continue;
                    }

                    RedactToken(property.Value);
                }

                break;

            case JArray array:
                foreach (var child in array)
                {
                    RedactToken(child);
                }

                break;

            case JValue value when value.Type == JTokenType.String:
                var current = value.Value<string>();
                var sanitized = SanitizeString(current);
                if (!string.Equals(current, sanitized, StringComparison.Ordinal))
                {
                    value.Value = sanitized;
                }

                break;
        }
    }

    private static bool IsSensitivePropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = NormalizeKey(propertyName);
        return SensitivePropertyNames.Contains(normalized);
    }

    private static string SanitizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var sanitized = SensitiveQueryParameterRegex.Replace(
            value,
            static match => match.Groups[1].Value + RedactedValue);

        sanitized = BearerTokenRegex.Replace(sanitized, $"Bearer {RedactedValue}");
        return sanitized;
    }

    private static string NormalizeKey(string key)
        => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
