// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Adapts <see cref="ILLMExecutor"/> to MEAI's <see cref="IChatClient"/> so that
/// <see cref="CodexFlow.QueryRuntime.Models.MeaiRuntimeModelClient"/> continues to flow
/// through <see cref="ILLMExecutor"/> (memory enrichment) and the shared
/// ReloadableChatClient -> ConfiguredSamplingChatClient -> VllmChatClient pipeline.
/// This bridge does not own the underlying executor or shared client; disposing it is a no-op.
/// </summary>
internal sealed class ExecutorChatClientBridge : IChatClient
{
    private readonly ILLMExecutor _executor;
    private readonly MemoryInjectionScenario _scenario;
    private readonly CodexSession? _session;
    private readonly string _caller;

    public ExecutorChatClientBridge(
        ILLMExecutor executor,
        MemoryInjectionScenario scenario,
        CodexSession? session,
        string caller = nameof(QreV2BackedQueryRuntimeEngine))
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _scenario = scenario;
        _session = session;
        _caller = caller;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var hydrated = HydrateVisualMessages(
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray(),
            AmbientTurnVisualAttachmentContext.Current);
        var request = new LLMExecutionRequest(
            hydrated,
            options,
            _scenario,
            _session,
            _caller);
        return _executor.ExecuteAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var hydrated = HydrateVisualMessages(
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray(),
            AmbientTurnVisualAttachmentContext.Current);
        var request = new LLMExecutionRequest(
            hydrated,
            options,
            _scenario,
            _session,
            _caller);
        await foreach (var update in _executor.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    internal static IReadOnlyList<ChatMessage> HydrateVisualMessages(
        IReadOnlyList<ChatMessage> messages,
        TurnVisualAttachmentStore? store)
    {
        if (messages.Count == 0 || store is null)
        {
            return messages;
        }

        var result = new List<ChatMessage>(messages.Count + 1);
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != ChatRole.User)
            {
                result.Add(msg);
                continue;
            }

            result.Add(HydrateUserMessage(msg, store));
        }

        if (!store.IsMultimodalEnabled)
        {
            return result;
        }

        // Only hydrate images from the latest trailing tool-result round (after the last assistant turn)
        // so provider APIs that forbid images inside role="tool" receive them in a trailing user message
        // without re-sending earlier rounds' images.
        var lastAssistantIndex = -1;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].Role == ChatRole.Assistant)
            {
                lastAssistantIndex = i;
                break;
            }
        }

        var trailingToolVisuals = new List<TurnVisualAttachment>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = lastAssistantIndex + 1; i < result.Count; i++)
        {
            var msg = result[i];
            if (msg.Role != ChatRole.Tool)
            {
                continue;
            }

            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent fnResult)
                {
                    var raw = fnResult.Result?.ToString();
                    foreach (var item in store.ExtractReferencedAttachments(raw))
                    {
                        if (seenIds.Add(item.Id))
                        {
                            trailingToolVisuals.Add(item);
                        }
                    }
                }
            }
        }

        if (trailingToolVisuals.Count > 0)
        {
            var contents = new List<AIContent>(1 + trailingToolVisuals.Count * 2)
            {
                new TextContent("[系统附带文档/PDF页面图像 - 请严格依据每张图标注的页码进行识别]")
            };

            foreach (var item in trailingToolVisuals)
            {
                contents.Add(new TextContent(DescribeVisual(item)));
                contents.Add(new DataContent(item.Bytes, item.MimeType));
            }

            result.Add(new ChatMessage(ChatRole.User, contents));
        }

        return result;
    }

    private static ChatMessage HydrateUserMessage(ChatMessage msg, TurnVisualAttachmentStore store)
    {
        var referenced = new List<TurnVisualAttachment>();
        var newContents = new List<AIContent>(msg.Contents.Count);
        var hasMarkers = false;

        foreach (var content in msg.Contents)
        {
            if (content is TextContent textContent)
            {
                var found = store.ExtractReferencedAttachments(textContent.Text);
                if (found.Count > 0)
                {
                    hasMarkers = true;
                    referenced.AddRange(found);
                    var cleaned = TurnVisualAttachmentStore.StripMarkers(textContent.Text);
                    if (!string.IsNullOrEmpty(cleaned))
                    {
                        newContents.Add(new TextContent(cleaned));
                    }
                    continue;
                }
            }

            newContents.Add(content);
        }

        if (!hasMarkers)
        {
            return msg;
        }

        if (store.IsMultimodalEnabled)
        {
            foreach (var item in referenced)
            {
                newContents.Add(new TextContent(DescribeVisual(item)));
                newContents.Add(new DataContent(item.Bytes, item.MimeType));
            }
        }

        return new ChatMessage(msg.Role, newContents)
        {
            AuthorName = msg.AuthorName,
            MessageId = msg.MessageId,
            AdditionalProperties = msg.AdditionalProperties
        };
    }

    private static string DescribeVisual(TurnVisualAttachment item)
    {
        if (item.PageNumber.HasValue)
        {
            var idx = item.ImageIndexOnPage ?? 1;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[图片信息] 文件: {item.FileName} | 所属页码: 第 {item.PageNumber.Value} 页 | 图片 #{idx}");
        }

        return $"[用户附带图片] 文件: {item.FileName}";
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Intentionally a no-op: the shared IChatClient / ILLMExecutor lifetime is owned by DI.
    }
}
