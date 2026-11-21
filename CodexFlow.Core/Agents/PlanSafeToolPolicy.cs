// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Explicit positive allow-list policy enforced whenever <c>PlanModeActive</c> is enabled.
/// Replaces permissive category-based filtering so worker dispatch, workflow scripts,
/// shell execution, worktree switching, and plan-approval tools can NEVER be invoked under Plan Mode.
/// </summary>
public static class PlanSafeToolPolicy
{
    private static readonly HashSet<string> ExplicitlyDeniedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_shell", "exec_cmd", "run_command", "exec_code", "run_tests", "skill",
        "write_file", "edit_file", "delete_file", "create_directory", "apply_patch",
        "ivilson_smart_patch", "hs_write", "notebook_edit", "zip_directory", "git_clone",
        "openspec_revert_changes", "openspec_create_checkpoint", "save_project_summary",
        "execute_code_task", "retry_failed_task", "start_next_task",
        "spawn_worker", "continue_worker", "stop_worker", "cleanup_worker_worktree",
        "enter_worktree", "exit_worktree", "workflow",
        "task_create", "task_update", "task_stop",
        "cron_create", "cron_delete", "cron_list", "monitor", "remote_trigger", "push_notification",
        "approve_plan", "reject_plan", "project_plan_to_tasks",
        "download_artifact", "report_task_status",
        "browser_act", "browser_evaluate", "browser_request_takeover",
        "computer_act", "computer_request_takeover"
    };

    private static readonly HashSet<string> CommonAllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ivilson_read",
        "hs_read",
        "ivilson_ls",
        "list_workspace",
        "glob",
        "search_in_files",
        "search_file_index",
        "analyze_code",
        "lsp_get_diagnostics",
        "lsp_document_symbols",
        "lsp_workspace_symbols",
        "lsp_go_to_definition",
        "lsp_find_references",
        "web_search",
        "fetch_webpage",
        "browser_navigate",
        "browser_snapshot",
        "browser_tabs",
        "computer_screen_info",
        "computer_screenshot",
        "ask_user_question",
        "tool_search",
        "user_learn_preference",
        "list_mcp_resources",
        "read_mcp_resource",
        "list_authorized_directory",
        "enter_plan_mode",
        "exit_plan_mode",
        "synthetic_output"
    };

    private static readonly HashSet<string> DevelopmentExtraAllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze_project",
        "create_session_plan",
        "generate_dev_plan",
        "write_plan_file",
        "read_plan_file",
        "request_plan_approval",
        "plan_diff",
        "plan_patch_resolve",
        "task_list",
        "task_get",
        "list_workers",
        "worker_output",
        "task_output"
    };

    public static bool IsToolNameAllowed(string? toolName, SessionWorkMode workMode)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var trimmed = toolName.Trim();
        if (ExplicitlyDeniedTools.Contains(trimmed))
        {
            return false;
        }

        var capability = ToolCapabilityCatalog.Resolve(trimmed);
        if (capability is ToolCapability.WorkspaceWrite or ToolCapability.ExecuteUntrustedCode or ToolCapability.BrowserInteract or ToolCapability.ComputerInteract)
        {
            return false;
        }

        if (CommonAllowedTools.Contains(trimmed))
        {
            return true;
        }

        return workMode == SessionWorkMode.Development && DevelopmentExtraAllowedTools.Contains(trimmed);
    }

    public static bool IsAllowed(ICodexTool tool, SessionWorkMode workMode)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (ExplicitlyDeniedTools.Contains(tool.Name))
        {
            return false;
        }

        if (tool.Capability is ToolCapability.WorkspaceWrite or ToolCapability.ExecuteUntrustedCode or ToolCapability.BrowserInteract or ToolCapability.ComputerInteract)
        {
            return false;
        }

        var catalogCap = ToolCapabilityCatalog.Resolve(tool.Name);
        if (catalogCap is ToolCapability.WorkspaceWrite or ToolCapability.ExecuteUntrustedCode or ToolCapability.BrowserInteract or ToolCapability.ComputerInteract)
        {
            return false;
        }

        if (tool.Metadata.IsDestructive)
        {
            return false;
        }

        if (CommonAllowedTools.Contains(tool.Name))
        {
            return true;
        }

        if (workMode == SessionWorkMode.Development && DevelopmentExtraAllowedTools.Contains(tool.Name))
        {
            return true;
        }

        // Allow synthetic/custom pure read-only test tools registered as Read/Analysis with IsReadOnly=true.
        return tool.Category is ToolCategory.Read or ToolCategory.Analysis && tool.Metadata.IsReadOnly;
    }
}
