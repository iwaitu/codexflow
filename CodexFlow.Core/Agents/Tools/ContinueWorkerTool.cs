// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class ContinueWorkerTool(
    Func<ContinueWorkerRequest, CancellationToken, Task<ContinueWorkerResult>> continueWorkerFunc,
    ILogger<ContinueWorkerTool> logger) : ICodexTool
{
    public string Name => "continue_worker";

    public string Description =>
        "继续一个已有 worker。若 worker 正处于 waiting-user，则恢复原 job；若 worker 已完成或已失败，则基于上次结论创建 follow-up worker。"
        + "对失败的作业，follow-up 的提示词会带上宿主记录的失败原因和更早几次尝试的失败记录；但新作业的写入范围与上次相同，"
        + "所以如果上次失败是因为写入被拒绝或范围不足，换指令也改变不了，请把失败原因告诉用户而不是继续派发。"
        + "参数：job_id(必填), prompt(必填), resume_token(可选)。"
        + "prompt 必须提供新的用户回复、追问焦点或需要继续的具体范围，不要只写“继续/continue”。"
        + "如果要基于上次结论继续推进，请明确这次新增的范围、证据要求或交付物。"
        + "Few-shot: continue_worker({\"job_id\":\"01K...\",\"prompt\":\"继续追踪这个模块里的缓存失效路径；这次只看 MongoFileService.cs 与 FileServiceTests.cs，并返回新的失败证据摘要\"})。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 12_288);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var jobId = arguments.GetValueOrDefault("job_id")?.ToString()
            ?? arguments.GetValueOrDefault("worker_id")?.ToString();
        var prompt = arguments.GetValueOrDefault("prompt")?.ToString();
        var resumeToken = arguments.GetValueOrDefault("resume_token")?.ToString();
        var sessionIdFallback = arguments.GetValueOrDefault("session_id")?.ToString();
        var userIdFallback = arguments.GetValueOrDefault("user_id")?.ToString();
        var workspacePathFallback = arguments.GetValueOrDefault("workspace_path")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CodexToolResult.Error("缺少必填参数 prompt。请给出新的追问或用户回复。");
        }

        var lintWarnings = WorkerDelegationLint.Analyze(
            workerType: null,
            prompt,
            description: null,
            isContinue: true);

        try
        {
            var result = await continueWorkerFunc(
                new ContinueWorkerRequest
                {
                    JobId = jobId,
                    Prompt = prompt,
                    ResumeToken = resumeToken,
                    SessionIdFallback = sessionIdFallback,
                    UserIdFallback = userIdFallback,
                    WorkspacePathFallback = workspacePathFallback
                },
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                return CodexToolResult.Error(result.Message ?? $"worker {jobId} 无法继续。");
            }

            var toolResult = result.Mode switch
            {
                ContinueWorkerMode.Resumed => CodexToolResult.Succeeded(
                    $"✅ worker {result.JobId} 已恢复执行。完成后系统会通过通知回流结果。",
                    summary: $"已恢复 worker {result.JobId}。"),
                _ => CodexToolResult.Succeeded(
                    $"✅ 已基于 worker {result.JobId} 创建 follow-up worker，新作业 ID: {result.NewJobId}。完成后系统会通过通知回流结果。",
                    summary: $"已基于 worker {result.JobId} 创建 follow-up worker。")
            };

            return WorkerDelegationLint.Decorate(toolResult, lintWarnings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "continue_worker 执行失败。jobId={JobId}", jobId);
            return CodexToolResult.Error($"继续 worker 时发生异常：{ex.Message}");
        }
    }
}
