// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Gateway;

/// <summary>
/// 会话标题占位判断、模型输出清洗与确定性兜底摘要策略。
/// </summary>
internal static partial class ConversationTitlePolicy
{
    public const string DefaultNewConversationTitle = "新会话";
    public const int MaxAutoTitleLength = 18;

    private static readonly HashSet<string> DefaultPlaceholderTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "新会话",
        "新对话",
        "主会话",
        "默认对话",
        "New Conversation",
        "New Chat"
    };

    private static readonly char[] TrimmedPunctuationAndQuotes =
        ['"', '\'', '“', '”', '‘', '’', '`', '。', '！', '？', '!', '?', '.', ',', '，', '、', ':', '：', ';', '；'];

    public static bool IsDefaultPlaceholderTitle(string? title)
        => string.IsNullOrWhiteSpace(title) || DefaultPlaceholderTitles.Contains(title.Trim());

    public static string SanitizeGeneratedTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        // 剥离推理模型可能回传的 <think>...</think> 思考块（含未闭合尾部）
        var cleaned = ThinkBlockRegex().Replace(rawTitle, " ");
        cleaned = cleaned.Replace("\r", "\n", StringComparison.Ordinal);

        // 若模型输出多行，取最后一个非空有效行
        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        cleaned = lines.Length > 0 ? lines[^1] : cleaned;

        cleaned = MarkdownPrefixRegex().Replace(cleaned, string.Empty);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        cleaned = cleaned.Trim(TrimmedPunctuationAndQuotes).Trim();

        if (IsDefaultPlaceholderTitle(cleaned))
        {
            return string.Empty;
        }

        return TruncateTitle(cleaned);
    }

    public static string BuildFallbackTitle(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return string.Empty;
        }

        var lines = userMessage.Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var promptParts = new List<string>();
        string? firstAttachmentName = null;

        foreach (var line in lines)
        {
            var attachmentMatch = AttachmentTagRegex().Match(line);
            if (attachmentMatch.Success)
            {
                firstAttachmentName ??= attachmentMatch.Groups[1].Value.Trim();
                continue;
            }

            if (line.StartsWith("以下附件已", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[附件", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[工作区附件", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            promptParts.Add(line);
        }

        var candidate = promptParts.Count > 0
            ? string.Join(" ", promptParts)
            : firstAttachmentName ?? string.Empty;

        candidate = WhitespaceRegex().Replace(candidate, " ").Trim();
        candidate = candidate.Trim(TrimmedPunctuationAndQuotes).Trim();

        if (IsDefaultPlaceholderTitle(candidate))
        {
            return string.Empty;
        }

        return TruncateTitle(candidate);
    }

    private static string TruncateTitle(string value)
        => value.Length > MaxAutoTitleLength ? value[..MaxAutoTitleLength] + "…" : value;

    [GeneratedRegex(@"<\s*think\s*>[\s\S]*?(?:<\s*/\s*think\s*>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex(@"^(?:[#>*\-•]+|\d+[.)])\s*")]
    private static partial Regex MarkdownPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\[(?:工作区)?附件:\s*([^\]]+)\]")]
    private static partial Regex AttachmentTagRegex();
}
