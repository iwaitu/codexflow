// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Models;

public static class CodexPlanSummaryFormatter
{
    public static string BuildExecutionSummary(IEnumerable<CodexTask>? plan)
    {
        var taskList = (plan ?? Enumerable.Empty<CodexTask>()).Where(t => t != null).ToList();
        var total = taskList.Count;
        var succeeded = taskList.Count(t => t.Status is CodexTaskStatus.Success or CodexTaskStatus.CompletedWithWarnings);
        var skipped = taskList.Count(t => t.Status == CodexTaskStatus.Skipped);
        var failed = taskList.Count(t => t.Status == CodexTaskStatus.Failed);
        return $"任务执行汇总：成功 {succeeded}，跳过 {skipped}，失败 {failed}，总计 {total}。";
    }
}
