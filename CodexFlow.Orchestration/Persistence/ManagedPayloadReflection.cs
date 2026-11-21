// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Orchestration.Persistence;

internal static class ManagedPayloadReflection
{
#if CODEXFLOW_CLOSED_PAYLOADS
    // The AOT and trimmed builds use the closed payload readers and do not reference reflection serializers.
    public static JToken? TryToToken(object value) => null;
    public static string? TryToJson(object value) => null;
    public static object? TryReadProperty(object value, string name) => null;
#else
    private static bool IsAvailable => RuntimeFeature.IsDynamicCodeSupported && JsonSerializer.IsReflectionEnabledByDefault;

    public static JToken? TryToToken(object value) => IsAvailable ? JToken.FromObject(value) : null;
    public static string? TryToJson(object value) => IsAvailable ? JsonSerializer.Serialize(value) : null;
    public static object? TryReadProperty(object value, string name) =>
        IsAvailable ? value.GetType().GetProperty(name)?.GetValue(value) : null;
#endif
}
