// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// The worker's structured statement about its own task. It changes nothing; the runtime records the call's arguments and the host reads them
/// when judging the job, so a task the worker itself reports as blocked is never counted as completed.
/// </summary>
public sealed class ReportTaskStatusTool : ICodexTool
{
    public string Name => WorkerStatusTool.Name;

    public string Description => "结束任务前报告这个任务的真实状态（不修改任何文件）。参数：status(必填，completed=任务要求的修改都已写入项目文件；blocked=还有任务要求的修改没有写入、无法写入，或需要用户先做决定), reason(blocked 时必填，说明卡在哪里), unfinished(可选，字符串数组，列出没做完的修改), unverified(可选，字符串数组，列出没能运行或没能做完的验证)。\n" +
        "判断标准是修改，不是验证：只要还有任务要求的修改没有写入，就必须报告 blocked，不要把部分完成说成完成；已经完成的部分不会因此丢失。\n" +
        "修改都已写入、只是某些验证在你的环境里跑不了或没做完（比如核对基线、某条命令访问不到网络），报告 completed，并把这些验证列进 unverified：验收由宿主独立完成（构建、测试、依赖与密钥扫描），不会因为你没验证而放过问题。\n" +
        "Few-shot:\n" +
        "  report_task_status({\"status\":\"completed\"})\n" +
        "  report_task_status({\"status\":\"completed\",\"unverified\":[\"逐项目核对变更前后的解析版本\"]})\n" +
        "  report_task_status({\"status\":\"blocked\",\"reason\":\"任务引用的漏洞清单不在任务描述或仓库里，只升级了其中 4 个包\",\"unfinished\":[\"Snappier\",\"MessagePack\"]})";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(true, true, false, ToolInterruptBehavior.CancelSafe, 4_096);

    public IReadOnlyList<int> AllowedStages => [3];

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var status = arguments.GetValueOrDefault("status")?.ToString()?.Trim().ToLowerInvariant();
        if (status is not (WorkerStatusTool.Completed or WorkerStatusTool.Blocked))
        {
            return Task.FromResult(CodexToolResult.Error("status 必须是 completed 或 blocked。"));
        }

        var reason = arguments.GetValueOrDefault("reason")?.ToString();
        if (status == WorkerStatusTool.Blocked && string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(CodexToolResult.Error("status=blocked 时必须用 reason 说明卡在哪里。"));
        }

        // Told plainly rather than rejected: a rejection teaches the worker to drop the list, and the list is the honest part of the report.
        // No way out by rewording is offered either: the runtime keeps this report until a write follows it.
        var report = WorkerStatusReport.FromArguments(System.Text.Json.JsonSerializer.SerializeToElement(arguments));
        if (status == WorkerStatusTool.Completed && report is { IsBlocked: true })
        {
            return Task.FromResult(CodexToolResult.Succeeded(
                "已记录：你报告 completed，但 unfinished 里列出了没写入的修改，宿主按任务受阻判定。已经写入的修改会保留。" +
                "只有把这些修改真正写入项目文件之后，重新报告才会改变这个判定；换一种说法重新报告不会。",
                summary: "task status: " + WorkerStatusTool.Blocked));
        }

        return Task.FromResult(CodexToolResult.Succeeded(
            status == WorkerStatusTool.Completed ? "已记录：任务完成。" : "已记录：任务受阻，宿主会据此判定任务未完成。",
            summary: "task status: " + status));
    }
}
