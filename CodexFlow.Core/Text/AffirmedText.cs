// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Core.Text;

/// <summary>
/// "Does this text really say X?" for decisions that would otherwise fire on a mere mention: a needle counts only when the sentence around it does
/// not deny it. Two readings of denial exist because two kinds of text are read:
/// <list type="bullet">
/// <item><see cref="Style.Report"/>: what a worker or model says happened ("没有 NU1605", "no package downgrade", "build failed 0 times").</item>
/// <item><see cref="Style.Task"/>: what a task is allowed or asked to do ("不修改测试文件", "无需构建", "构建和测试除外").</item>
/// </list>
/// Pure and conservative: an unparseable case counts as affirmed (the stricter reading), never as denied.
/// </summary>
internal static class AffirmedText
{
    internal enum Style { Report, Task }

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex ReportBefore = new(
        @"(没有|没|无|不存在|未出现|未发现|未再|不再|零|\bno\b|\bnot\b|\bwithout\b|\bnever\b|\bzero\b|\b0\b)[^\n\r。.;；]{0,10}$", Options, Timeout);

    private static readonly Regex ReportAfter = new(
        @"^[^\n\r。.;；]{0,6}(不存在|未出现|不再|已消除|消失|没有出现|did not appear|does not appear|absent|\b0\s+(times|errors?)|0\s*(次|个错误))", Options, Timeout);

    // "不" negates unless it is part of a word that does not ("不仅要修复还要测试" asks for the test; 不但/不过/不同/不管/不如/不错...).
    private static readonly Regex TaskBefore = new(
        @"(不(?!仅|但|过|同|管|如|错|久|论|是|少|够|妨|然)|无需|无须|不再|没有|禁止|避免|\bno need (to|for)\b|\bdo not\b|\bdon'?t\b|\bwithout\b|\bexcept\b|\bnot\b)[^\n\r。.;；]{0,12}$",
        Options, Timeout);

    private static readonly Regex TaskAfter = new(
        @"^[^\n\r。.;；]{0,6}(除外|不需要|无需|不涉及|不在范围|out of scope|excluded)", Options, Timeout);

    public static bool ContainsAffirmed(string text, Style style, params string[] needles)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var before = style == Style.Report ? ReportBefore : TaskBefore;
        var after = style == Style.Report ? ReportAfter : TaskAfter;
        foreach (var needle in needles)
        {
            var from = 0;
            while (from < text.Length)
            {
                var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                from = at + needle.Length;
                try
                {
                    if (!before.IsMatch(text[Math.Max(0, at - 16)..at]) && !after.IsMatch(text[from..Math.Min(text.Length, from + 24)]))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
