// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Gateway;

/// <summary>
/// Configures Gateway query runtime loop limits.
/// </summary>
public sealed class GatewayRuntimeOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Runtime:Gateway";

    /// <summary>
    /// Maximum number of Gateway rounds that may expose tools before the reserved summary round.
    /// </summary>
    public int MaxToolRounds { get; set; } = 30;

    /// <summary>
    /// Enables VLLM client skill catalog injection for Gateway runtime requests.
    /// </summary>
    public bool EnableSkills { get; set; } = true;

    /// <summary>
    /// Optional skills directory. When omitted, Gateway resolves the bundled or source skills directory.
    /// </summary>
    public string? SkillDirectoryPath { get; set; }
}
