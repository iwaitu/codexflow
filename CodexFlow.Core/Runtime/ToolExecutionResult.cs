// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Runtime;

/// <summary>What one tool call really returned; the evidence processor folds these into the turn's evidence.</summary>
/// <remarks>
/// To supply observation evidence in <c>Metadata</c>, use a <see cref="Newtonsoft.Json.Linq.JObject"/>
/// or an object-valued <see cref="System.Text.Json.JsonElement"/> whose backing document is still available.
/// Callers can provide JSON from their own explicit DTO serializer or clone the element before disposing its document.
/// </remarks>
public sealed record ToolExecutionResult(
    string ToolName,
    string CallId,
    string Result,
    bool Success,
    int? ResultLength = null,
    Exception? Exception = null,
    string? Summary = null,
    bool IsOutputTruncated = false,
    object? Metadata = null,
    string? SystemHint = null,
    ToolSystemHint? SystemHintDetail = null,
    bool DryRun = false);
