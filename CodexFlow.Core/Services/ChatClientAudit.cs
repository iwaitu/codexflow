// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

public static class ChatClientAudit
{
    private const int MaxPreviewLength = 600;
    private static readonly ChatClientAuditJsonContext AuditJson = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    });

    public static void AppendStreamingUpdate(
        ChatResponseUpdate update,
        StringBuilder responseText,
        StringBuilder thinkingText)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(thinkingText);

        var isThinking = update is ReasoningChatResponseUpdate ru && ru.Thinking;
        var reasonText = isThinking ? update.Text : null;
        if (!string.IsNullOrEmpty(reasonText))
        {
            thinkingText.Append(reasonText);
        }

        var hasTextFromContents = false;

        foreach (var part in update.Contents ?? Array.Empty<AIContent>())
        {
            if (part is not TextContent tc) continue;
            var text = tc.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text)) continue;

            hasTextFromContents = true;
            if (isThinking) thinkingText.Append(text);
            else responseText.Append(text);
        }

        if (!hasTextFromContents && !string.IsNullOrEmpty(update.Text))
        {
            if (isThinking && string.IsNullOrEmpty(reasonText)) thinkingText.Append(update.Text);
            else responseText.Append(update.Text);
        }
    }

    public static (string Response, string Thinking) ExtractResponse(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var responseText = new StringBuilder();
        var thinkingText = new StringBuilder();

        foreach (var message in response.Messages ?? Array.Empty<ChatMessage>())
        {
            foreach (var part in message.Contents ?? Array.Empty<AIContent>())
            {
                switch (part)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        responseText.Append(tc.Text);
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        thinkingText.Append(reasoning.Text);
                        break;
                }
            }
        }

        if (responseText.Length == 0 && !string.IsNullOrEmpty(response.Text))
        {
            responseText.Append(response.Text);
        }

        return (responseText.ToString(), thinkingText.ToString());
    }

    public static void LogInteraction(
        ILogger logger,
        string source,
        string mode,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string response,
        string thinking,
        Exception? error = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(thinking);

#pragma warning disable CA1031
        try
        {
            var record = new AuditRecord(
                DateTime.UtcNow.ToString("O"),
                error == null ? "ichatclient_interaction" : "ichatclient_interaction_error",
                "__ichatclient__",
                Stage: 0,
                Round: 0,
                new AuditPayload(
                    source,
                    mode,
                    SnapshotMessages(messages),
                    SnapshotOptions(options),
                    Truncate(response),
                    Truncate(thinking),
                    response.Length,
                    thinking.Length,
                    error?.Message));

            logger.LogInformation("FULLTEXT|{AuditJson}", JsonSerializer.Serialize(record, AuditJson.AuditRecord));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write IChatClient audit log. source={Source}, mode={Mode}", source, mode);
        }
#pragma warning restore CA1031
    }

    private static AuditOptions SnapshotOptions(ChatOptions? options) => new(
        options?.Temperature,
        options?.TopP,
        options?.Tools?.Count ?? 0,
        options?.Tools?.Select(static tool => tool.Name).ToArray() ?? [],
        options?.ToolMode?.ToString(),
        (options as VllmChatOptions)?.ThinkingEnabled,
        options?.MaxOutputTokens);

    private static List<AuditMessage> SnapshotMessages(IEnumerable<ChatMessage> messages)
    {
        var snapshot = new List<AuditMessage>();
        var index = 0;
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text) && message.Contents != null)
            {
                text = string.Concat(message.Contents
                    .Select(ExtractAuditableContentText)
                    .Where(t => !string.IsNullOrEmpty(t)));
            }

            snapshot.Add(new AuditMessage(index++, message.Role.ToString(), Truncate(text ?? string.Empty)));
        }

        return snapshot;
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxPreviewLength) return text;
        return text[..MaxPreviewLength] + "...[truncated]";
    }

    private static string ExtractAuditableContentText(AIContent content)
    {
        switch (content)
        {
            case TextContent tc:
                return tc.Text ?? string.Empty;
            case FunctionResultContent frc:
                return ExtractFunctionResultText(frc);
            default:
                return string.Empty;
        }
    }

    private static string ExtractFunctionResultText(FunctionResultContent content)
    {
        var callId = content.CallId ?? string.Empty;
        var result = content.Result;
        var resultText = result switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable enumerable => string.Join(" ", enumerable.Cast<object?>().Select(static item => item?.ToString()).Where(static item => !string.IsNullOrWhiteSpace(item))),
            _ => result.ToString() ?? string.Empty
        };

        return string.IsNullOrWhiteSpace(callId)
            ? resultText
            : $"[tool_result:{callId}] {resultText}";
    }

    internal sealed record AuditRecord(
        string Timestamp,
        string EventType,
        string SessionId,
        int Stage,
        int Round,
        AuditPayload Payload);

    internal sealed record AuditPayload(
        string Source,
        string Mode,
        List<AuditMessage> Request,
        AuditOptions Options,
        string Response,
        string Thinking,
        int ResponseLength,
        int ThinkingLength,
        string? Error);

    internal sealed record AuditOptions(
        float? Temperature,
        float? TopP,
        int ToolCount,
        string[] ToolNames,
        string? ToolMode,
        bool? ThinkingEnabled,
        int? MaxOutputTokens);

    internal sealed record AuditMessage(int Index, string Role, string Text);
}

[JsonSerializable(typeof(ChatClientAudit.AuditRecord))]
internal sealed partial class ChatClientAuditJsonContext : JsonSerializerContext;
