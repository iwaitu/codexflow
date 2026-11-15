// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CodexFlow.Core.Protocols;

/// <summary>A <see cref="JobFailure"/> as the single string that travels in notification metadata.</summary>
public static class JobFailureWire
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>The failure as JSON, with the kind and the remedy by name.</summary>
    public static string ToJson(JobFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return JsonConvert.SerializeObject(failure, Settings);
    }

    /// <summary>The failure back from JSON; null when the text is missing or not a failure (a notice must still go out without it).</summary>
    public static JobFailure? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<JobFailure>(json, Settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
