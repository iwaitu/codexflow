// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Models;

/// <summary>
/// Configures runtime limits for context compression read paths.
/// Bound from the "ContextCompression" section in appsettings.json.
/// </summary>
public sealed class ContextCompressionOptions
{
    /// <summary>Configuration section name used for binding context compression options.</summary>
    public const string SectionName = "ContextCompression";

    /// <summary>Max chars of HistorySummary injected into Gateway prompts.</summary>
    public int GatewaySummaryMaxChars { get; set; } = 2000;

    /// <summary>Defensive hard cap on the message rows loaded for a gateway prompt.</summary>
    public int PromptHistoryHardLimit { get; set; } = 40;
}
