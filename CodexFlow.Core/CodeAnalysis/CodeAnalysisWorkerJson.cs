// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.CodeAnalysis;

internal sealed record CodeAnalysisRequest(int Version, long Id, string Code, string ReferenceDirectory);

internal sealed record CodeAnalysisResponse(int Version, long Id, List<CodeDiagnostic>? Diagnostics, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeAnalysisRequest))]
[JsonSerializable(typeof(CodeAnalysisResponse))]
internal sealed partial class CodeAnalysisWorkerJson : JsonSerializerContext
{
    public const int ProtocolVersion = 1;
}
