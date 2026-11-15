// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 重试已失败的后台作业（fire-and-forget）。
/// 调用后立即返回确认，不阻塞等待任务执行完成。
/// 后台调度器异步接管作业并通过 SignalR 通知推送进展。
/// </summary>
public sealed class RetryTaskTool(
    Func<string, Task<bool>> retryJobFunc,
    ILogger<RetryTaskTool> logger) : ICodexTool
{
    public string Name => "retry_failed_task";

    /// <summary>
    /// The retry that carries the failure to the worker, takes guidance, and refuses with a reason. When it is not set the tool behaves as it
    /// did: the job is put back in the queue and nothing more is known.
    /// </summary>
    public Func<string, string?, Task<RetryJobResult>>? DetailedRetry { get; init; }

    public string Description =>
        "重试一个已失败（Failed / FailedRecoveryNeeded）的后台作业：同一个作业、同样的任务指令和写入范围重新排队。" +
        "上次失败的原因会写进作业的提示词，告诉 worker 不要原样重复。" +
        "工具调用后立即返回，不等待任务执行完成——完成后通过通知推送结果。" +
        "参数：job_id（作业 ID，必填）、guidance（可选：这次要 worker 换的做法，不要写“再试一次”）。" +
        "重试改变不了的失败会被拒绝并说明原因（例如宿主判定的 offline_restore_unavailable、同一个原因连续两次、已重试 3 次）；" +
        "这时不要再调用本工具，需要换指令就用 continue_worker，需要改范围或版本就告诉用户。" +
        "Few-shot: retry_failed_task({\"job_id\":\"01KMB9G3TAY4YH6G9THYJG5Q9Z\",\"guidance\":\"先用 dotnet list package --include-transitive 确认依赖从哪个项目进来，再改对应的 csproj。\"})。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(
        Dictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var jobId = arguments.GetValueOrDefault("job_id")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。请提供要重试的作业 ID。");
        }

        logger.LogInformation("retry_failed_task: 请求重试 Job {JobId}", jobId);

        var guidance = arguments.GetValueOrDefault("guidance")?.ToString();
        RetryJobResult detailed;
        try
        {
            detailed = DetailedRetry != null
                ? await DetailedRetry(jobId, guidance).ConfigureAwait(false)
                : new RetryJobResult(await retryJobFunc(jobId).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "retry_failed_task: 重试 Job {JobId} 时发生异常", jobId);
            return CodexToolResult.Error($"重试作业时发生异常：{ex.Message}");
        }

        if (!detailed.Success && detailed.ReasonCode != null)
        {
            logger.LogWarning("retry_failed_task: Job {JobId} 的重试被拒绝：{ReasonCode}", jobId, detailed.ReasonCode);
            return CodexToolResult.Error(
                $"作业 {jobId} 没有重试（{detailed.ReasonCode}）：{detailed.Reason} " +
                "不要再次调用 retry_failed_task。需要换一种指令就用 continue_worker（写明这次要换的做法）；需要改写入范围、版本或计划，就把原因告诉用户。");
        }

        if (!detailed.Success)
        {
            logger.LogWarning("retry_failed_task: Job {JobId} 重试失败（作业不存在或当前状态不允许重试）", jobId);
            return CodexToolResult.Error(
                $"作业 {jobId} 无法重试：作业不存在，或当前状态不是 Failed / FailedRecoveryNeeded / Cancelled。");
        }

        logger.LogInformation("retry_failed_task: Job {JobId} 已成功重新排入队列", jobId);

        var told = string.IsNullOrWhiteSpace(detailed.FailureSummary)
            ? string.Empty
            : $"已告诉 worker 上次失败的原因：{detailed.FailureSummary}。";
        return CodexToolResult.Succeeded(
            $"✅ 作业 {jobId} 已重新排入调度队列。{told}系统将在后台继续执行，完成后通过通知推送结果。请通知用户等待即可，不要再次调用 start_next_task 或 retry_failed_task。");
    }
}
