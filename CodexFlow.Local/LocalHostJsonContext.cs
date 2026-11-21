// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Local;

internal sealed record LocalHostReadyInfo(int Port, int Pid, string InstanceId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocalHostReadyInfo))]
[JsonSerializable(typeof(LocalInstanceDiscoveryInfo))]
internal sealed partial class LocalHostJsonContext : JsonSerializerContext
{
    internal static LocalHostJsonContext Indented { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
}
