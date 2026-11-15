// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading;

namespace CodexFlow.AI;

internal sealed record VllmToolWireOverride(
    string? RequiredToolName,
    IReadOnlyList<string> ExpectedToolNames,
    string? ExpectedToolChoice,
    string? PromptSnapshotQueryId = null,
    string? PromptSnapshotSessionId = null,
    int? PromptSnapshotRound = null);

internal sealed class VllmToolWireOverrideScope : IDisposable
{
    private static readonly AsyncLocal<VllmToolWireOverride?> CurrentOverride = new();
    private readonly VllmToolWireOverride? _previous;

    private VllmToolWireOverrideScope(VllmToolWireOverride? current)
    {
        _previous = CurrentOverride.Value;
        CurrentOverride.Value = current;
    }

    public static VllmToolWireOverride? Current => CurrentOverride.Value;

    public static IDisposable Push(VllmToolWireOverride? current)
        => new VllmToolWireOverrideScope(current);

    public void Dispose()
        => CurrentOverride.Value = _previous;
}
