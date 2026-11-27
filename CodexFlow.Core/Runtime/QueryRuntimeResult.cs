// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: Query Runtime 终止结果 — 返回给入口层
/// </summary>
public sealed record QueryRuntimeResult
{
    /// <summary>终止原因</summary>
    public required QueryTerminationReason TerminationReason { get; init; }

    /// <summary>执行轮次总数</summary>
    public required int TotalRounds { get; init; }

    /// <summary>工具调用总数</summary>
    public required int TotalToolCalls { get; init; }

    /// <summary>写操作工具调用总数（Orchestrator 用它判断是否真的落盘修改）</summary>
    public int WriteToolCalls { get; init; }

    /// <summary>无工具调用轮次数</summary>
    public required int ZeroToolCallRounds { get; init; }

    /// <summary>空响应计数</summary>
    public required int EmptyResponseCount { get; init; }

    /// <summary>恢复尝试计数</summary>
    public required int RecoveryCount { get; init; }

    /// <summary>异常协议计数</summary>
    public required int MalformedProtocolCount { get; init; }

    /// <summary>最终 assistant 文本</summary>
    public required string FinalText { get; init; }

    /// <summary>最终思维链内容</summary>
    public string? FinalThinking { get; init; }

    /// <summary>累计 prompt tokens</summary>
    public int? TotalPromptTokens { get; init; }

    /// <summary>累计 completion tokens</summary>
    public int? TotalCompletionTokens { get; init; }

    /// <summary>本回合最后一次模型请求的 prompt tokens（反映单次上下文窗口实际输入水位）</summary>
    public int? LastStepPromptTokens { get; init; }

    /// <summary>本回合最后一次模型请求的 completion tokens</summary>
    public int? LastStepCompletionTokens { get; init; }

    /// <summary>
    /// 累计 prompt tokens 中由 provider 提示缓存命中的部分（是 <see cref="TotalPromptTokens"/> 的子集，不是额外的量）。
    /// provider 没有上报缓存命中时为 null。
    /// </summary>
    public int? TotalCachedPromptTokens { get; init; }

    /// <summary>总耗时毫秒</summary>
    public required long TotalDurationMs { get; init; }

    /// <summary>状态标志位</summary>
    public RuntimeState Flags { get; init; } = RuntimeState.None;

    /// <summary>终止详情码（用于细化 termination reason）</summary>
    public string? TerminalDetailCode { get; init; }

    /// <summary>终止的人类可读原因（例如触发 fail-closed 的停止钩子原因）；仅用于诊断，不参与分支判断。</summary>
    public string? TerminalDetailMessage { get; init; }

    /// <summary>最后一次函数调用（用于 audit）</summary>
    public string? LastFunctionCall { get; init; }

    /// <summary>最终消息列表（用于 persistence）</summary>
    public IReadOnlyList<ChatMessage>? FinalMessages { get; init; }

    /// <summary>最后一次模型请求前生成的 prompt/context 组装快照。</summary>
    public PromptAssemblySnapshot? LastPromptAssemblySnapshot { get; init; }

    /// <summary>可持久化的 runtime 工作现场，用于 compact / resume / debug。</summary>
    public QueryRuntimeCheckpoint? RuntimeCheckpoint { get; init; }

    /// <summary>worker 通过 report_task_status 工具对自己任务状态的结构化陈述；未调用则为 null。</summary>
    public WorkerStatusReport? WorkerReport { get; init; }

    /// <summary>
    /// The worker reported "completed" with nothing unfinished and a build or test it ran exited cleanly, with no verification left failing
    /// (see <see cref="VerifiedNoChangeCompletion"/>). The host uses it to accept a forge task that changed no file.
    /// </summary>
    public bool VerifiedWithoutChange { get; init; }

    /// <summary>Terminal verification receipts for the current attempt. They do not attest to final candidate content.</summary>
    public IReadOnlyList<RuntimeVerificationReceipt> VerificationReceipts { get; init; } = [];

    /// <summary>Query ID（用于 telemetry 关联）</summary>
    public Guid QueryId { get; init; } = Guid.NewGuid();
}
