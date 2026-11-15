// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// A model that calls a tool this turn does not expose gets a "not exposed" denial, and a weaker model answers a denial by calling the same
/// tool again. Seen live (local vLLM, two planning turns): <c>ReadSkillFile</c>/<c>ListSkillFiles</c> called 31 times in a row, every call
/// denied, until the turn ran out of steps and the user got no reply at all.
/// The runtime counts the denials at the END of the conversation (decided by the runtime's own error code, never by wording): after
/// <see cref="NudgeAfterDenials"/> it names the tools that do exist, and after <see cref="StopAfterDenials"/> it ends the turn with a reason
/// instead of spending the remaining steps.
/// </summary>
internal static class UnexposedToolCallGuard
{
    /// <summary>The runtime's error code for a call to a tool outside the step's catalog.</summary>
    public const string DeniedCode = "tool_not_exposed_in_context";

    /// <summary>The error code this guard ends the turn with.</summary>
    public const string StopCode = "repeated_unexposed_tool_calls";

    public const int NudgeAfterDenials = 2;
    public const int StopAfterDenials = 6;

    /// <summary>
    /// The tool calls denied as "not exposed" since the model last did anything else: counted back from the end of the conversation and
    /// stopped by a tool result of any other kind or by a user message. Names are returned oldest first.
    /// </summary>
    public static IReadOnlyList<string> TrailingDenials(IReadOnlyList<QreProtocol.RuntimeMessage> messages)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var item in message.Items)
            {
                if (item is QreProtocol.RuntimeToolCallItem call)
                {
                    callNames[call.Call.InvocationId.Value] = call.Call.Name;
                }
            }
        }

        var denied = new List<string>();
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role == QreProtocol.RuntimeMessageRole.User)
            {
                break;
            }

            if (message.Role != QreProtocol.RuntimeMessageRole.Tool)
            {
                continue;
            }

            for (var itemIndex = message.Items.Count - 1; itemIndex >= 0; itemIndex--)
            {
                if (message.Items[itemIndex] is not QreProtocol.RuntimeToolResultItem result)
                {
                    continue;
                }

                if (!string.Equals(result.Result.Error?.Code, DeniedCode, StringComparison.Ordinal))
                {
                    denied.Reverse();
                    return denied;
                }

                denied.Add(callNames.TryGetValue(result.Result.InvocationId.Value, out var name) ? name : "?");
            }
        }

        denied.Reverse();
        return denied;
    }

    public static bool ShouldStop(IReadOnlyList<string> trailingDenials) => trailingDenials.Count >= StopAfterDenials;

    /// <summary>What the turn ends with once the model would not stop calling tools that are not there.</summary>
    public static string StopMessage(IReadOnlyList<string> trailingDenials)
        => $"The model called tools this turn does not expose {trailingDenials.Count} times in a row " +
           $"({string.Join(", ", trailingDenials.Distinct(StringComparer.OrdinalIgnoreCase))}) and ignored the list of tools that are available.";

    /// <summary>The reminder added to the next model request; null while the model has not repeated a denied call.</summary>
    public static ChatMessage? Nudge(IReadOnlyList<string> trailingDenials, IEnumerable<string> exposedToolNames)
    {
        if (trailingDenials.Count < NudgeAfterDenials)
        {
            return null;
        }

        var missing = string.Join("、", trailingDenials.Distinct(StringComparer.OrdinalIgnoreCase).Select(static name => $"`{name}`"));
        var available = string.Join("、", exposedToolNames.Select(static name => $"`{name}`"));
        return new ChatMessage(
            ChatRole.User,
            $"[SYSTEM] 本轮没有 {missing} 这样的工具，已连续 {trailingDenials.Count} 次调用被拒绝，再调用结果也一样，不要再调用。" +
            (available.Length == 0
                ? "本轮没有可用工具，请直接用文字回答。"
                : $"本轮可用的工具只有：{available}。请改用其中的工具完成用户的请求；都不合适时直接用文字回答。"));
    }
}
