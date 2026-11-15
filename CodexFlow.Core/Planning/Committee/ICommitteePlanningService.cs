// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会规划服务接口。
/// 负责多轮结构化评审流程，产出最终蓝图。
/// </summary>
public interface ICommitteePlanningService
{
    /// <summary>
    /// 执行委员会规划会议。
    /// </summary>
    Task<CommitteePlanningResult> RunCommitteePlanningAsync(
        CommitteePlanningRequest request,
        CancellationToken ct = default);
}
