// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Execution;

/// <summary>
/// Host-owned capability table. Capabilities are keyed by registered tool name and never derived from model input.
/// Anything not listed resolves to <see cref="ToolCapability.Unknown"/> and is denied by the broker.
/// "ReadOnly" means: no workspace write and no code execution (it may still touch session/plan state).
/// </summary>
public static class ToolCapabilityCatalog
{
    private static readonly Dictionary<string, ToolCapability> Table = Build();

    public static IReadOnlyDictionary<string, ToolCapability> Declared => Table;

    public static ToolCapability Resolve(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && Table.TryGetValue(toolName.Trim(), out var c) ? c : ToolCapability.Unknown;

    /// <summary>Capability gate: Unknown is denied; otherwise Allowed (execution tools additionally need a trusted context at run time).</summary>
    public static ExecutionDecision Evaluate(string? toolName, ToolCapability? declaredByTool = null)
    {
        var capability = declaredByTool is { } d && d != ToolCapability.Unknown ? d : Resolve(toolName);
        return capability == ToolCapability.Unknown
            ? ExecutionDecision.Deny(ExecutionReasonCodes.UnknownToolCapability, $"Tool '{toolName}' has no trusted capability declaration; denied by default.")
            : ExecutionDecision.Allow();
    }

    private static Dictionary<string, ToolCapability> Build()
    {
        var t = new Dictionary<string, ToolCapability>(StringComparer.OrdinalIgnoreCase);

        void Add(ToolCapability c, params string[] names)
        {
            foreach (var n in names)
            {
                t[n] = c;
            }
        }

        Add(ToolCapability.ExecuteUntrustedCode,
            "exec_cmd", "run_command", "exec_code", "run_tests", "skill");

        Add(ToolCapability.WorkspaceWrite,
            "write_file", "edit_file", "delete_file", "create_directory", "apply_patch", "ApplyPatchTool",
            "ivilson_smart_patch", "hs_write", "notebook_edit", "write_plan_file", "save_project_summary",
            "zip_directory", "git_clone", "openspec_revert_changes", "openspec_create_checkpoint",
            "cleanup_worker_worktree", "enter_worktree", "exit_worktree", "execute_code_task", "retry_failed_task",
            "spawn_worker", "continue_worker");

        Add(ToolCapability.ReadOnly,
            "ivilson_read", "hs_read", "ivilson_ls", "list_workspace", "glob", "search_in_files", "search_file_index",
            "analyze_project", "analyze_code", "lsp_get_diagnostics", "lsp_document_symbols", "lsp_workspace_symbols",
            "lsp_go_to_definition", "lsp_find_references", "plan_diff", "read_plan_file", "download_artifact",
            "list_workers", "worker_output", "task_output", "task_stop", "stop_worker", "task_create", "task_get",
            "task_list", "task_update", "tool_search", "web_search", "fetch_webpage", "ask_user_question",
            "enter_plan_mode", "exit_plan_mode", "user_learn_preference", "list_mcp_resources", "read_mcp_resource",
            "create_session_plan", "generate_dev_plan", "plan_patch_resolve", "request_plan_approval", "approve_plan",
            "reject_plan", "project_plan_to_tasks", "cron_create", "cron_delete", "cron_list", "monitor",
            "remote_trigger", "push_notification", "workflow", "synthetic_output", "report_task_status");

        return t;
    }
}
