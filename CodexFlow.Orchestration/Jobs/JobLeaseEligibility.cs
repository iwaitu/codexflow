// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json;

namespace CodexFlow.Orchestration.Jobs;

/// <summary>Interprets job eligibility consistently for managed and SQLite stores.</summary>
internal static class JobLeaseEligibility
{
    internal static bool IsRunnableAt(DateTime createdAtUtc, string? payload, DateTime now)
        => createdAtUtc <= now && GetEarliestRunAt(createdAtUtc, payload) <= now;

    internal static DateTime GetEarliestRunAt(DateTime createdAtUtc, string? payload)
    {
        var retry = GetRetryNotBeforeUtc(payload);
        return retry is { } value && value.UtcDateTime > createdAtUtc ? value.UtcDateTime : createdAtUtc;
    }

    private static DateTimeOffset? GetRetryNotBeforeUtc(string? payload)
    {
        const string propertyName = "retryNotBeforeUtc";
        if (string.IsNullOrWhiteSpace(payload) || !payload.Contains("\"retryNotBeforeUtc\"", StringComparison.Ordinal))
        {
            return null;
        }

        // The existing HTTP entry point accepts Newtonsoft's permissive JSON syntax.
        // Use its streaming reader to preserve that behavior without building a JSON object graph.
        try
        {
            using var body = new StringReader(payload);
            using var reader = new JsonTextReader(body);
            while (reader.Read() && reader.TokenType == JsonToken.Comment) { }
            if (reader.TokenType != JsonToken.StartObject)
            {
                return null;
            }

            string? date = null;
            var readDate = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject && reader.Depth == 0)
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.Comment)
                        {
                            return null;
                        }
                    }

                    return DateTimeOffset.TryParse(date, out var parsed) ? parsed.ToUniversalTime() : null;
                }

                if (reader.Depth != 1 || reader.TokenType == JsonToken.Comment)
                {
                    continue;
                }

                if (reader.TokenType == JsonToken.PropertyName)
                {
                    readDate = string.Equals((string?)reader.Value, propertyName, StringComparison.Ordinal);
                }
                else if (readDate)
                {
                    date = reader.Value?.ToString();
                    readDate = false;
                }
            }
        }
        catch (JsonException)
        {
            // Missing and unreadable retry dates both leave the job eligible in the legacy store.
        }

        return null;
    }
}
