// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CodexFlow.AI;

/// <summary>
/// What one model request may weigh. A run adds a tool result to its history every round, and nothing in the run counts what has piled up:
/// without this the first sign of a too-long prompt is the provider's refusal, which ends the run. The shrink is deliberately narrow — old
/// tool results are replaced by a note; the task, the calls and the newest results are never touched.
/// </summary>
public static class PromptBudget
{
    /// <summary>The newest tool results are the ones the model is still working with.</summary>
    private const int KeepNewestResults = 2;

    /// <summary>A result this small is not worth a note.</summary>
    private const int MinResultCharsToStub = 400;

    private static readonly Regex NamedLimit = new(@"maximum context length is (\d+)|context window of (\d+)|(\d+) token limit",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A deliberately pessimistic count: ASCII text costs a token per ~3 characters, and anything else (Chinese above all) more than one
    /// token per character. The 4-characters-per-token rule used for compression undercounts both, and JSON-escaped text most of all.
    /// </summary>
    public static int Estimate(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        double tokens = 0;
        foreach (var message in messages)
        {
            tokens += 4;   // role and framing
            foreach (var content in message.Contents)
            {
                tokens += Cost(ContentText(content));
            }
        }

        foreach (var tool in options?.Tools ?? [])
        {
            tokens += Cost(tool.Name) + Cost(tool.Description);
            if (tool is AIFunctionDeclaration declaration)
            {
                tokens += Cost(declaration.JsonSchema.GetRawText());
            }
        }

        return (int)Math.Ceiling(tokens);
    }

    /// <summary>
    /// Returns a copy of <paramref name="messages"/> whose oldest large tool results are replaced by a note until the estimate fits
    /// <paramref name="targetTokens"/>, or null when nothing could be shrunk. The input list and its messages are never changed: they are
    /// the run's own history.
    /// </summary>
    public static List<ChatMessage>? Compact(IReadOnlyList<ChatMessage> messages, int targetTokens, ChatOptions? options)
    {
        var results = new List<(int Message, int Content, int Chars)>();
        for (var m = 0; m < messages.Count; m++)
        {
            for (var c = 0; c < messages[m].Contents.Count; c++)
            {
                if (messages[m].Contents[c] is FunctionResultContent result)
                {
                    results.Add((m, c, ResultText(result).Length));
                }
            }
        }

        var copy = messages.ToList();
        var changed = false;
        var stubbed = new HashSet<int>();
        // First keep the newest two results, and only when that is not enough the newest one: the result the model is about to react to is
        // never replaced (the cap on a single result keeps it from being the problem on its own).
        foreach (var keep in new[] { KeepNewestResults, 1 })
        {
            for (var i = 0; i < results.Count - keep; i++)
            {
                if (Estimate(copy, options) <= targetTokens)
                {
                    break;
                }

                var (m, c, chars) = results[i];
                if (chars < MinResultCharsToStub || !stubbed.Add(i))
                {
                    continue;
                }

                Stub(copy, m, c, chars);
                changed = true;
            }
        }

        return changed ? copy : null;
    }

    private static void Stub(List<ChatMessage> copy, int m, int c, int chars)
    {
        var original = (FunctionResultContent)copy[m].Contents[c];
        var contents = copy[m].Contents.ToList();
        contents[c] = new FunctionResultContent(original.CallId,
            $"[This earlier tool result ({chars} characters) was removed to keep the conversation within the model's context window. Call the tool again if you still need it.]")
        {
            Exception = original.Exception
        };
        copy[m] = new ChatMessage(copy[m].Role, contents)
        {
            AuthorName = copy[m].AuthorName,
            MessageId = copy[m].MessageId,
            AdditionalProperties = copy[m].AdditionalProperties
        };
    }

    /// <summary>Recognises the provider's "this prompt is longer than the model's window" refusal, and the window it names when it names one.</summary>
    public static bool IsContextOverflow(Exception exception, out int? limit)
    {
        limit = null;
        var text = exception.Message ?? string.Empty;
        var overflow = text.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
                       (text.Contains("context window", StringComparison.OrdinalIgnoreCase) &&
                        (text.Contains("exceed", StringComparison.OrdinalIgnoreCase) || text.Contains("too long", StringComparison.OrdinalIgnoreCase)));
        if (!overflow)
        {
            return false;
        }

        var match = NamedLimit.Match(text);
        if (match.Success)
        {
            var digits = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(static g => g.Success)?.Value;
            if (int.TryParse(digits, out var parsed) && parsed > 0)
            {
                limit = parsed;
            }
        }

        return true;
    }

    private static double Cost(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        foreach (var ch in text)
        {
            if (ch < 128) ascii++;
        }

        // A JSON-escaped character (对) is six ASCII characters that a tokenizer splits into four or five tokens, not two: counted as
        // plain ASCII, nine tool_search results of 42,000 characters were taken for 126,000 tokens and were really about 290,000.
        var escapes = UnicodeEscape.Count(text);
        return (ascii - escapes * 6) / 3.0 + escapes * 4.5 + (text.Length - ascii) * 1.2;
    }

    private static readonly Regex UnicodeEscape = new(@"\\u[0-9A-Fa-f]{4}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static string ContentText(AIContent content) => content switch
    {
        TextContent text => text.Text ?? string.Empty,
        TextReasoningContent reasoning => reasoning.Text ?? string.Empty,
        FunctionCallContent call => call.Name + " " + (call.Arguments is null ? string.Empty : JsonSerializer.Serialize(call.Arguments)),
        FunctionResultContent result => ResultText(result),
        _ => string.Empty
    };

    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        var other => JsonSerializer.Serialize(other)
    };
}
