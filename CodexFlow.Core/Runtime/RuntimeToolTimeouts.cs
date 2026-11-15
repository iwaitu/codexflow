// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Constants;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Per-tool execution deadline handed to the query runtime. The engine's own default is one minute for every tool, which is right for reads and
/// searches but cuts planning (draft round + structured projection round) and real builds/test runs short.
/// </summary>
internal static class RuntimeToolTimeouts
{
    private static readonly TimeSpan EngineDefault = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Planning = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BuildOrTest = TimeSpan.FromMinutes(10);

    public static TimeSpan For(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return EngineDefault;
        if (PlanningToolNames.IsPlanCreationTool(toolName)) return Planning;
        return toolName.ToLowerInvariant() is "run_tests" or "exec_cmd" or "run_command" ? BuildOrTest : EngineDefault;
    }
}
