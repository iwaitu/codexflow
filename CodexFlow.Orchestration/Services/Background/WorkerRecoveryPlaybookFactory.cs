// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

internal static class WorkerRecoveryPlaybookFactory
{
    public static JObject Create(
        string recoveryReason,
        string workerType,
        IReadOnlyCollection<string>? runtimeFlags = null,
        IReadOnlyDictionary<string, object?>? serviceDiagnostics = null)
    {
        var plan = BuildDefinition(recoveryReason, workerType);

        var playbook = new JObject
        {
            ["title"] = plan.Title,
            ["guidance"] = plan.Guidance,
            ["nextAction"] = plan.NextAction,
            ["suggestedPrompt"] = plan.SuggestedPrompt
        };

        if (plan.Steps.Count > 0)
        {
            playbook["steps"] = JArray.FromObject(plan.Steps);
        }

        if (plan.Checks.Count > 0)
        {
            playbook["checks"] = JArray.FromObject(plan.Checks);
        }

        if (runtimeFlags is { Count: > 0 })
        {
            playbook["runtimeFlags"] = JArray.FromObject(runtimeFlags);
        }

        if (serviceDiagnostics is { Count: > 0 })
        {
            var suspectKeys = serviceDiagnostics
                .Where(static kvp => kvp.Value is string text && text.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
                .Select(static kvp => kvp.Key)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (suspectKeys.Length > 0)
            {
                playbook["diagnosticFocus"] = JArray.FromObject(suspectKeys);
            }
        }

        return playbook;
    }

    public static string? BuildGuidanceSummary(JObject? playbook)
    {
        var guidance = playbook?["guidance"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            return guidance.Trim();
        }

        var nextAction = playbook?["nextAction"]?.Value<string>();
        return string.IsNullOrWhiteSpace(nextAction) ? null : nextAction.Trim();
    }

    private static WorkerRecoveryPlaybookDefinition BuildDefinition(string recoveryReason, string workerType)
    {
        var workerLabel = string.IsNullOrWhiteSpace(workerType) ? "worker" : workerType.Trim().ToLowerInvariant();

        return recoveryReason.Trim().ToLowerInvariant() switch
        {
            "transport_failure" => new WorkerRecoveryPlaybookDefinition(
                Title: "恢复模型与网络链路后再续跑",
                Guidance: $"优先确认模型服务和网络链路恢复正常，再用 continue_worker 让 {workerLabel} 从最小探测开始续跑。",
                NextAction: "先做最小连通性探测，再继续主任务。",
                SuggestedPrompt: "先确认模型服务与网络已恢复，再从上次中断点继续；第一轮只做最小探测并报告 transport diagnostics，确认稳定后再恢复主任务。",
                Steps:
                [
                    "检查 provider、代理、DNS 与认证状态是否恢复正常。",
                    "继续该 worker 时，先执行最小读操作或轻量探测，不要直接扩大任务范围。",
                    "若再次出现 transport failure，保留 diagnostics 并停止继续扩写结果。"
                ],
                Checks:
                [
                    "确认最新 diagnostics 中不再出现连接、超时或认证异常。",
                    "确认本轮第一步是最小探测，而不是直接重放整段复杂任务。"
                ]),
            "stall_detected" => new WorkerRecoveryPlaybookDefinition(
                Title: "缩小范围并打破重复工具调用",
                Guidance: $"继续 {workerLabel} 前先明确本轮唯一目标，避免再次重复相同工具调用。",
                NextAction: "缩小任务范围，要求先产出新的观察，再决定是否继续用工具。",
                SuggestedPrompt: "请不要重复上轮相同工具调用。先说明你将如何改变探测策略，只执行一个新的最小步骤，并给出新的观察结果后再决定后续动作。",
                Steps:
                [
                    "把任务压缩成一个新的最小目标，而不是重放整段计划。",
                    "先要求模型解释新的策略变化，再允许一次新的工具调用。",
                    "若仍重复同一工具调用，应直接返回原因和阻塞点。"
                ],
                Checks:
                [
                    "确认下一轮 prompt 明确禁止重复上一轮同样的工具组合。",
                    "确认本轮结果包含新的观察，而不是重复摘要。"
                ]),
            "empty_response" => new WorkerRecoveryPlaybookDefinition(
                Title: "要求先返回最小有效文本再继续",
                Guidance: $"继续 {workerLabel} 时先要求返回最小有效文本或单个明确动作；如果必要修改已经完成，就直接用一句非空总结收尾，避免再次空响应。",
                NextAction: "先拿到一个最小有效回应，再恢复正常工具循环；若任务已闭环则立即收尾。",
                SuggestedPrompt: "请先返回一个最小有效回应，说明你接下来要做的单一步骤；如果必要修改已经完成，请直接用一句非空总结收尾；如果需要工具，只调用一个最相关工具，不要返回空响应。",
                Steps:
                [
                    "第一轮只要求最小文本回应或单一步骤。",
                    "避免同时请求多项复杂动作，降低再次空响应概率。",
                    "如果工作区已经有足够改动，优先补上最后一个阻塞点或直接输出收尾结论。",
                    "若模型再次无输出，应直接保留失败原因并停止重试。"
                ],
                Checks:
                [
                    "确认续跑 prompt 明确禁止空响应。",
                    "确认第一轮目标是最小有效输出。",
                    "确认任务闭环后不会继续多轮空转。"
                ]),
            "malformed_protocol" => new WorkerRecoveryPlaybookDefinition(
                Title: "收紧协议格式并最小化单轮动作",
                Guidance: $"继续 {workerLabel} 时先收紧协议要求，确保本轮只产出一个严格合法的工具调用或最终文本。",
                NextAction: "先要求严格遵守协议，再恢复任务推进。",
                SuggestedPrompt: "请严格遵守当前协议格式。本轮只能二选一：返回一个合法工具调用，或返回最终文本结论；不要混入额外伪标签、半结构化片段或无效工具参数。",
                Steps:
                [
                    "把续跑 prompt 收紧成单轮单动作。",
                    "明确禁止额外 XML/JSON 噪音与无效工具参数。",
                    "若再次破坏协议，应直接输出阻塞原因。"
                ],
                Checks:
                [
                    "确认本轮 prompt 已明确限制为单个合法动作。",
                    "确认协议错误位置已体现在恢复说明里。"
                ]),
            "unexecuted_write_intent" => new WorkerRecoveryPlaybookDefinition(
                Title: "读完快照后强制进入写入",
                Guidance: $"该 {workerLabel} 已读取目标文件但没有产生任何写入工具调用；续跑时必须直接调用写入工具落地修改。",
                NextAction: "基于已有快照执行最小写入，不要再次重复读取相同文件。",
                SuggestedPrompt: "你已经读取到目标文件快照。下一轮必须直接调用 hs_write、ivilson_smart_patch 或 apply_patch 落地当前任务；不要再输出“现在修改/准备落地/开始修复”这类说明，也不要重复读取相同文件。若确实无法修改，请明确给出阻塞原因。",
                Steps:
                [
                    "复用上轮读取到的 snapshotId、fileFingerprint、文件路径和锚点。",
                    "第一动作必须是写入工具调用；只在快照过期或 Hashline mismatch 时重新读取。",
                    "写入后再做最小验证或给出简短完成摘要。"
                ],
                Checks:
                [
                    "确认续跑 prompt 明确禁止重复读取相同文件。",
                    "确认首轮动作是 hs_write、ivilson_smart_patch 或 apply_patch。",
                    "确认 worker 结果中至少包含一个 changedFiles 条目。"
                ]),
            "lease_expired" => new WorkerRecoveryPlaybookDefinition(
                Title: "从最后稳定检查点恢复 worker",
                Guidance: $"该 {workerLabel} 因 lease 过期被回收，恢复时应从最后稳定检查点继续，并先确认没有并发 worker 仍在处理同一任务。",
                NextAction: "基于已有结果和检查点续跑，不要假设上次运行仍持有执行上下文。",
                SuggestedPrompt: "请基于上次稳定结果继续，不要假设之前的执行上下文仍然存在。先总结你已知的稳定状态与未完成项，再执行下一步最小动作。",
                Steps:
                [
                    "先总结已有稳定结果、未完成项与可能丢失的临时状态。",
                    "确认没有其他 worker 正在并发处理同一任务或同一 worktree。",
                    "从最小下一步恢复，不要直接重复长流程。"
                ],
                Checks:
                [
                    "确认 worktree / claim 已不再被其他运行中的 worker 占用。",
                    "确认续跑 prompt 明确要求从稳定检查点恢复。"
                ]),
            "host_shutdown" => new WorkerRecoveryPlaybookDefinition(
                Title: "在宿主重启后做恢复性续跑",
                Guidance: $"宿主中断了 {workerLabel}，恢复时先重建必要上下文，再继续关键路径。",
                NextAction: "先确认上下文是否完整，再恢复主任务。",
                SuggestedPrompt: "宿主在上次运行中断了执行。请先总结当前仍可信的状态和缺失的上下文，再从最关键的下一步继续。",
                Steps:
                [
                    "重建会话、上下文和关键输入的最小可信视图。",
                    "先做一轮状态确认，再恢复关键任务。",
                    "若发现上下文缺失，先报告缺口再继续。"
                ],
                Checks:
                [
                    "确认续跑前有一段状态重建说明。",
                    "确认缺失上下文会被显式报告。"
                ]),
            _ => new WorkerRecoveryPlaybookDefinition(
                Title: "按恢复语义继续 worker",
                Guidance: $"该 {workerLabel} 已进入 recovery-needed 状态，建议使用 continue_worker 以最小下一步重新建立稳定执行路径。",
                NextAction: "缩小范围，先恢复稳定执行，再扩大任务范围。",
                SuggestedPrompt: "请先总结当前稳定状态和剩余风险，再从最小下一步继续，不要直接重复整段历史任务。",
                Steps:
                [
                    "先总结稳定状态、阻塞点和未完成项。",
                    "先执行一个最小下一步，再决定是否扩大范围。"
                ],
                Checks:
                [
                    "确认本轮先恢复稳定状态，而不是直接全量重跑。"
                ])
        };
    }

    private sealed record WorkerRecoveryPlaybookDefinition(
        string Title,
        string Guidance,
        string NextAction,
        string SuggestedPrompt,
        IReadOnlyList<string> Steps,
        IReadOnlyList<string> Checks);
}
