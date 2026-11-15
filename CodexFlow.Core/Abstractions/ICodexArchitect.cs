// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// [Level 8] 独立的架构师代理接口。
/// 负责 Stage 1 (Analysis) 和 Stage 2 (Planning) 的高层认知任务。
/// </summary>
public interface ICodexArchitect
{
    /// <summary>
    /// 分析用户需求并结合项目上下文生成项目摘要/技术方案。
    /// </summary>
    Task<string> AnalyzeAsync(CodexSession session, string userGoal, CancellationToken ct = default);

    /// <summary>
    /// 基于分析结果生成开发计划 (Task Breakdown)。
    /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    Task<List<CodexTask>> PlanAsync(CodexSession session, string goal, CancellationToken ct = default);
#pragma warning restore CA1002
}
