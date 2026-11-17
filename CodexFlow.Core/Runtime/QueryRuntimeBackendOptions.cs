// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Runtime;

public sealed record QueryRuntimeBackendOptions
{
    public const string SectionName = "Runtime:QueryRuntime";

    public string Backend { get; init; } = QreV2Backend;

    public int ModelStreamTimeoutMs { get; init; } = 180_000;

    /// <summary>
    /// When set, every run is recorded under this directory (<c>.qre/v2/private/runs/...</c>) in QRE's private diagnostic mode: full prompts,
    /// the model's output including its reasoning, tool calls and results. <c>qre replay latest --workspace &lt;dir&gt;</c> reads it back.
    /// Off by default: the recording holds everything the model saw and said, so it is for local diagnosis only.
    /// </summary>
    public string? AuditDirectory { get; set; }

    public const string QreV2Backend = "qre-v2";

    public bool IsSupportedBackend =>
        string.Equals(Backend, QreV2Backend, StringComparison.OrdinalIgnoreCase);
}
