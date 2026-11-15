// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Agents;

public static class CoordinatorToolSurfacePolicy
{
    private static readonly HashSet<string> AllowedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn_worker",
        "continue_worker",
        "cron_create",
        "cron_delete",
        "cron_list",
        "stop_worker",
        "task_stop",
        "list_workers",
        "monitor",
        "push_notification",
        "remote_trigger",
        "cleanup_worker_worktree",
        "worker_output",
        "workflow",
        "task_output",
        "task_create",
        "task_get",
        "task_list",
        "task_update",
        "ask_user_question",
        "enter_plan_mode",
        "enter_worktree",
        "exit_plan_mode",
        "exit_worktree",
        "synthetic_output"
    };

    public static IReadOnlySet<string> AllowedNames => AllowedToolNames;

    public static IReadOnlyList<ICodexTool> Filter(IEnumerable<ICodexTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools
            .Where(tool => AllowedToolNames.Contains(tool.Name))
            .ToArray();
    }
}
