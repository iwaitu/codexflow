// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// A forge worker is expected to change files. A weaker model kept reading (38-60 calls) and never wrote. The runtime counts the calls since the
/// last real write (decided by the call, not by what the model says) and, for a forge worker only, tells it to write once it has read for
/// <see cref="NudgeAfterCalls"/> calls, and requires the write tool once it has read for <see cref="ForceWriteAfterCalls"/>.
/// </summary>
internal static class ForgeProgressGuard
{
    public const int NudgeAfterCalls = 10;
    public const int ForceWriteAfterCalls = 16;

    /// <summary>A forced call whose arguments the model has not prepared fails the same way each round, so forcing stops after this many rounds.</summary>
    public const int MaxForcedRounds = 2;

    // The tool the worker's prompt names for edits first; apply_patch needs a ready patch and is the last resort.
    private static readonly string[] WriteToolPreference = ["hs_write", "ivilson_write", "write_file"];

    public static string? PickWriteTool(IEnumerable<string> toolNames)
    {
        var writers = toolNames.Where(static name => ToolClassification.IsWriteTool(name)).ToList();
        foreach (var preferred in WriteToolPreference)
        {
            var hit = writers.FirstOrDefault(name => name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit;
            }
        }

        return writers.FirstOrDefault();
    }

    public static QueryRuntimeDynamicContextResult? Build(QueryLoopEntryPoint entryPoint, int callsSinceWrite, string? writeToolName, int forcedSoFar = 0)
    {
        if (entryPoint != QueryLoopEntryPoint.ForgeWorker || callsSinceWrite < NudgeAfterCalls)
        {
            return null;
        }

        var tool = string.IsNullOrWhiteSpace(writeToolName) ? "写文件的工具" : $"`{writeToolName}`";
        var force = callsSinceWrite >= ForceWriteAfterCalls && !string.IsNullOrWhiteSpace(writeToolName) && forcedSoFar < MaxForcedRounds;
        var text = force
            ? $"[SYSTEM] 你已连续 {callsSinceWrite} 次只读，一直没有修改任何文件。这一轮必须调用 {tool} 写入任务要求的修改；" +
              "若确实不需要修改，也在这次写入之外如实说明原因。"
            : $"[SYSTEM] 你已连续 {callsSinceWrite} 次只读，还没有修改任何文件。信息已经足够时，下一步请用 {tool} 写入任务要求的修改，" +
              "不要继续探索；写入之后再还原、构建、测试。";
        return new QueryRuntimeDynamicContextResult
        {
            Messages = [new ChatMessage(ChatRole.User, text)],
            RequiredToolName = force ? writeToolName : null
        };
    }
}
