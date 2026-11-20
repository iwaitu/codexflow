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
    // run_shell: 10m approval wait + up to 10m script run + 5m cleanup/staging margin.
    private static readonly TimeSpan DailyShell = TimeSpan.FromMinutes(25);
    // run_business_skill: multi-step API pagination + optional interactive write approval wait.
    private static readonly TimeSpan BusinessSkillRun = TimeSpan.FromMinutes(15);
    // browser_request_takeover: max 15m human takeover wait + post-takeover snapshot/screenshot.
    private static readonly TimeSpan BrowserTakeover = TimeSpan.FromMinutes(16);
    // browser_act / navigate / tabs: 5m control-plane approval wait + browser settlement/staging margin.
    private static readonly TimeSpan BrowserInteractive = TimeSpan.FromMinutes(8);

    public static TimeSpan For(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return EngineDefault;
        if (PlanningToolNames.IsPlanCreationTool(toolName)) return Planning;
        return toolName.ToLowerInvariant() switch
        {
            "run_shell" => DailyShell,
            "run_business_skill" => BusinessSkillRun,
            "browser_request_takeover" => BrowserTakeover,
            "browser_act" or "browser_navigate" or "browser_tabs" or "browser_evaluate" => BrowserInteractive,
            "run_tests" or "exec_cmd" or "run_command" => BuildOrTest,
            _ => EngineDefault
        };
    }
}
