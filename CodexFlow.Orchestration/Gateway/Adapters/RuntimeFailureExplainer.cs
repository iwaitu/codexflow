// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.AI;
using CodexFlow.Core.Telemetry;

namespace CodexFlow.Gateway.Adapters;

/// <summary>
/// Turns how a run ended into what the user needs to hear: what happened, and what to do about it. A terminal code and the provider's own
/// JSON are for whoever diagnoses the failure; they stay at the end of the message, they are not the message. "Please retry" is only said
/// when retrying can help: a prompt that passed the model's window will pass it again.
/// </summary>
public static class RuntimeFailureExplainer
{
    private const int MaxDetailChars = 200;

    private static readonly Regex Unauthorized = new(@"\b(401|403)\b|api[ _-]?key|unauthori[sz]ed|forbidden",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RateLimited = new(@"\b429\b|rate limit|too many requests",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TimedOut = new(@"timed out|timeout|超时",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Unreachable = new(@"refused|no such host|name or service not known|could not resolve|network is unreachable|connection (reset|closed|could not)|no connection could be made|unable to connect",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The message shown to the user for a run that ended in failure.</summary>
    public static string Explain(QueryTerminationReason reason, string? detailCode, string? detailMessage)
    {
        var cause = detailMessage?.Trim() ?? string.Empty;
        var overflow = PromptBudget.IsContextOverflow(new InvalidOperationException(cause), out var window);

        string what;
        if (overflow || reason == QueryTerminationReason.ContextHardLimit)
        {
            what = $"这一轮累积的内容超过了模型的上下文窗口{(window is { } w ? $"（{w} tokens）" : string.Empty)}，旧的工具结果压缩之后仍然放不下。" +
                   "这不是网络或密钥问题，直接重试大概率还会失败。建议：把请求拆小、缩小要读的文件或目录范围，或者新开一个会话再试。";
        }
        else if (Unauthorized.IsMatch(cause))
        {
            what = "模型接口拒绝了这次请求（鉴权失败）。请检查模型密钥（VllmAgent:ApiKey）是否正确、是否已过期。";
        }
        else if (RateLimited.IsMatch(cause))
        {
            what = "模型接口限流了（429）。稍等一会儿再重试。";
        }
        else if (TimedOut.IsMatch(cause))
        {
            what = "等待模型回复超时了。模型服务可能过载，或者网络不稳；稍后重试，持续出现就检查模型服务。";
        }
        else if (Unreachable.IsMatch(cause))
        {
            what = "连不上模型接口。请检查网络或 VPN 是否在线、模型地址（VllmAgent:ApiUrl）是否正确。";
        }
        else if (reason == QueryTerminationReason.MaxRoundsReached ||
                 string.Equals(detailCode, "continuation_budget_exhausted", StringComparison.OrdinalIgnoreCase))
        {
            what = "这一轮用完了允许的步数，还没有得出结论。建议把请求拆成更小的几步，或者告诉它具体从哪里接着做。";
        }
        else if (reason == QueryTerminationReason.StallDetected)
        {
            what = "连续几轮没有进展（在重复同样的动作），已经停止。换个说法说明你想要的结果，或者把任务拆小。";
        }
        else
        {
            what = "本轮执行失败。请重试；若持续复现，请检查 Gateway runtime 与 transport 日志。";
        }

        var detail = new List<string>();
        if (!string.IsNullOrWhiteSpace(detailCode)) detail.Add(detailCode!);
        if (cause.Length > 0) detail.Add(cause.Length > MaxDetailChars ? cause[..MaxDetailChars] + "…" : cause);
        return detail.Count == 0 ? what : $"{what}（技术详情：{string.Join("；", detail)}）";
    }
}
