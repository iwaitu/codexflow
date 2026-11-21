// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Orchestration.Hosting;

internal static class BooleanConfiguration
{
    public static bool Read(IConfiguration? configuration, string key, bool defaultValue, bool nullable = true)
    {
        var value = configuration?.GetSection(key).Value;
        if (value is null || nullable && value.Length == 0)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Failed to convert configuration value at '{key}' to type 'System.Boolean'.");
    }
}
