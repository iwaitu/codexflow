// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Governance;
using CodexFlow.Core.Services;

namespace CodexFlow.AI;

/// <summary>
/// Ensures every IChatClient request carries configured sampling params.
/// </summary>
[ApprovedNonStreamingLlm(
    "Facade wrapper that centralizes configured sampling for approved non-streaming chat calls.",
    Ticket = "non-streaming-llm-migration",
    ReviewBy = "2026-06-30",
    Scope = ApprovedNonStreamingLlmScope.Facade)]
public sealed class ConfiguredSamplingChatClient : IChatClient
{
    internal const string PromptSnapshotQueryIdProperty = "__codexflow_prompt_snapshot_query_id";
    internal const string PromptSnapshotSessionIdProperty = "__codexflow_prompt_snapshot_session_id";
    internal const string PromptSnapshotRoundProperty = "__codexflow_prompt_snapshot_round";

    private readonly IChatClient _inner;
    private readonly float? _temperature;
    private readonly float? _topP;
    private readonly ILogger<ConfiguredSamplingChatClient> _logger;
    // True once the model has refused sampling parameters (or configuration says to omit them): temperature/top_p are then never sent.
    private volatile bool _omitSampling;
    // The model's window in tokens: configured, or learned from the provider's refusal. Null until known, and then only refusals are handled.
    private int? _contextWindow;

    /// <summary>
    /// Initializes a chat client wrapper that applies configured sampling parameters to every request.
    /// </summary>
    /// <param name="inner">Inner chat client to delegate calls to.</param>
    /// <param name="temperature">Optional temperature override.</param>
    /// <param name="topP">Optional top-p override.</param>
    /// <param name="logger">Logger used to record synchronous and streaming chat audit data.</param>
    /// <param name="omitSampling">
    /// True never sends temperature/top_p (models that deprecate them, e.g. Claude Sonnet 5.x). Null/false sends them until the model refuses
    /// them once: the request is then retried without and every later request omits them.
    /// </param>
    public ConfiguredSamplingChatClient(
        IChatClient inner,
        float? temperature,
        float? topP,
        ILogger<ConfiguredSamplingChatClient> logger,
        bool? omitSampling = null,
        int? contextWindowTokens = null)
    {
        _inner = inner;
        _contextWindow = contextWindowTokens is > 0 ? contextWindowTokens : null;
        _temperature = temperature;
        _topP = topP;
        _logger = logger;
        _omitSampling = omitSampling == true;
    }

    /// <summary>
    /// Recognises "this model no longer accepts temperature / top_p" and remembers it. A value out of range ("temperature must be between...")
    /// is a bug in the caller, not a deprecated parameter, and is not matched.
    /// </summary>
    private bool LearnSamplingRejected(Exception ex)
    {
        if (_omitSampling) return false;
        var text = ex.Message;
        var mentionsSampling = text.Contains("temperature", StringComparison.OrdinalIgnoreCase) || text.Contains("top_p", StringComparison.OrdinalIgnoreCase);
        var saysUnsupported = text.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
        if (!mentionsSampling || !saysUnsupported) return false;
        _omitSampling = true;
        _logger.LogWarning("The model refused sampling parameters; temperature/top_p are no longer sent. Reason: {Reason}", text.Length > 240 ? text[..240] : text);
        return true;
    }

    /// <summary>
    /// Disposes the inner chat client.
    /// </summary>
    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// Resolves a service from the wrapper itself or delegates the lookup to the inner chat client.
    /// </summary>
    /// <typeparam name="TService">Requested service contract.</typeparam>
    /// <param name="serviceKey">Optional keyed-service identifier passed through to the inner client.</param>
    /// <returns>The requested service instance when available; otherwise <see langword="null"/>.</returns>
    public TService? GetService<TService>(object? serviceKey = null) where TService : class
    {
        if (this is TService self)
        {
            return self;
        }

        return _inner.GetService<TService>(serviceKey);
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return ((IChatClient)_inner).GetService(serviceType, serviceKey);
    }
    /// <summary>
    /// Asynchronously generates a chat response based on the provided sequence of messages and optional configuration
    /// settings.
    /// </summary>
    /// <remarks>Use this method to obtain a model-generated reply in the context of a multi-turn
    /// conversation. Supplying appropriate options allows customization of the response generation process.</remarks>
    /// <param name="messages">An enumerable collection of chat messages that define the conversational context for generating the response.
    /// Cannot be null.</param>
    /// <param name="options">Optional settings that influence response generation, such as sampling parameters or maximum token count. If
    /// null, default options are applied.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a ChatResponse with the generated
    /// response.</returns>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        var effectiveOptions = ApplySampling(options);
        materializedMessages = FitToWindow(materializedMessages, effectiveOptions);
        using var wireOverrideScope = VllmToolWireOverrideScope.Push(ResolveWireOverride(effectiveOptions));

        try
        {
            ChatResponse response;
            try
            {
                response = await _inner.GetResponseAsync(materializedMessages, effectiveOptions, cancellationToken);
            }
            catch (Exception ex) when (LearnSamplingRejected(ex))
            {
                effectiveOptions = ApplySampling(options);
                response = await _inner.GetResponseAsync(materializedMessages, effectiveOptions, cancellationToken);
            }
            catch (Exception ex) when (IsInvalidThinkingSignature(ex) && HasReasoning(materializedMessages))
            {
                _logger.LogWarning("The model refused the replayed thinking blocks; retrying once without them and with thinking off. Reason: {Reason}", Truncate(ex.Message));
                materializedMessages = WithoutReasoning(materializedMessages);
                DisableThinking(effectiveOptions);
                response = await _inner.GetResponseAsync(materializedMessages, effectiveOptions, cancellationToken);
            }
            catch (Exception ex) when (ShrinkAfterOverflow(ex, materializedMessages, effectiveOptions) is { } shrunk)
            {
                materializedMessages = shrunk;
                response = await _inner.GetResponseAsync(materializedMessages, effectiveOptions, cancellationToken);
            }

            var (reply, thinking) = ChatClientAudit.ExtractResponse(response);
            ChatClientAudit.LogInteraction(_logger, nameof(ConfiguredSamplingChatClient), "sync", materializedMessages, effectiveOptions, reply, thinking);
            return response;
        }
        catch (Exception ex)
        {
            ChatClientAudit.LogInteraction(_logger, nameof(ConfiguredSamplingChatClient), "sync", materializedMessages, effectiveOptions, string.Empty, string.Empty, ex);
            throw;
        }
    }

    /// <summary>
    /// Asynchronously streams chat response updates generated from a sequence of chat messages.
    /// </summary>
    /// <remarks>This method yields response updates as they become available, enabling clients to process
    /// partial results without waiting for the entire response. Monitor the cancellation token to handle cancellation
    /// requests appropriately.</remarks>
    /// <param name="messages">An enumerable collection of chat messages to process and generate responses for.</param>
    /// <param name="options">Optional settings that configure the behavior of the streaming response. If not specified, default options are
    /// used.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the streaming operation.</param>
    /// <returns>An asynchronous stream of chat response updates, allowing real-time processing of responses as they are
    /// produced.</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        var effectiveOptions = ApplySampling(options);
        materializedMessages = FitToWindow(materializedMessages, effectiveOptions);
        using var wireOverrideScope = VllmToolWireOverrideScope.Push(ResolveWireOverride(effectiveOptions));
        var responseText = new System.Text.StringBuilder();
        var thinkingText = new System.Text.StringBuilder();

        var enumerator = _inner
            .GetStreamingResponseAsync(materializedMessages, effectiveOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var yielded = false;
        var retriedWithoutReasoning = false;
        var retriedAfterOverflow = false;
        try
        {
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    ChatClientAudit.LogInteraction(
                        _logger,
                        nameof(ConfiguredSamplingChatClient),
                        "stream",
                        materializedMessages,
                        effectiveOptions,
                        responseText.ToString(),
                        thinkingText.ToString());
                    yield break;
                }

                update = enumerator.Current;
            }
            catch (Exception ex) when (!yielded && LearnSamplingRejected(ex))
            {
                // Refused before any output reached the caller: safe to start the same request again without sampling.
                await enumerator.DisposeAsync();
                effectiveOptions = ApplySampling(options);
                enumerator = _inner
                    .GetStreamingResponseAsync(materializedMessages, effectiveOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                continue;
            }
            catch (Exception ex) when (!yielded && !retriedWithoutReasoning && IsInvalidThinkingSignature(ex) && HasReasoning(materializedMessages))
            {
                // Same safe moment as above (nothing reached the caller yet): drop the replayed thinking blocks and think no more for this request.
                retriedWithoutReasoning = true;
                _logger.LogWarning("The model refused the replayed thinking blocks; retrying once without them and with thinking off. Reason: {Reason}", Truncate(ex.Message));
                await enumerator.DisposeAsync();
                materializedMessages = WithoutReasoning(materializedMessages);
                DisableThinking(effectiveOptions);
                enumerator = _inner
                    .GetStreamingResponseAsync(materializedMessages, effectiveOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                continue;
            }
            catch (Exception ex) when (!yielded && !retriedAfterOverflow && ShrinkAfterOverflow(ex, materializedMessages, effectiveOptions) is { } shrunk)
            {
                // Refused as too long before anything reached the caller: the same request with the old tool results replaced by a note.
                retriedAfterOverflow = true;
                await enumerator.DisposeAsync();
                materializedMessages = shrunk;
                enumerator = _inner
                    .GetStreamingResponseAsync(materializedMessages, effectiveOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                ChatClientAudit.LogInteraction(
                    _logger,
                    nameof(ConfiguredSamplingChatClient),
                    "stream",
                    materializedMessages,
                    effectiveOptions,
                    responseText.ToString(),
                    thinkingText.ToString(),
                    ex);
                throw;
            }

            ChatClientAudit.AppendStreamingUpdate(update, responseText, thinkingText);
            yielded = true;
            yield return update;
        }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// A request that is known not to fit the window is shrunk before it is sent (old tool results become a note). Below 85% of the window
    /// nothing is touched; above it the target is 70%, which leaves the model room to answer.
    /// </summary>
    private List<ChatMessage> FitToWindow(List<ChatMessage> messages, ChatOptions? options)
    {
        if (_contextWindow is not { } window || PromptBudget.Estimate(messages, options) <= (int)(window * 0.85))
        {
            return messages;
        }

        var compacted = PromptBudget.Compact(messages, (int)(window * 0.7), options);
        if (compacted is null)
        {
            return messages;
        }

        _logger.LogWarning("The request would not fit the model's window of {Window} tokens; earlier tool results were replaced by a note.", window);
        return compacted;
    }

    /// <summary>
    /// The provider refused the request as too long: learns the window when it is named, and returns the request shrunk to 60% of it (or of
    /// its own size when no window is known), or null when this is another error or nothing is left to shrink.
    /// </summary>
    private List<ChatMessage>? ShrinkAfterOverflow(Exception ex, List<ChatMessage> messages, ChatOptions? options)
    {
        if (!PromptBudget.IsContextOverflow(ex, out var named))
        {
            return null;
        }

        if (named is { } limit)
        {
            _contextWindow = limit;
        }

        // The provider counted more than we did, so the target comes from the smaller of its window and our own count of this request.
        var estimate = PromptBudget.Estimate(messages, options);
        var target = (int)(Math.Min(_contextWindow ?? estimate, estimate) * 0.6);
        var compacted = PromptBudget.Compact(messages, target, options);
        if (compacted is not null)
        {
            _logger.LogWarning("The provider refused the request as too long; earlier tool results are replaced by a note and the request is sent once more. Reason: {Reason}", Truncate(ex.Message));
        }

        return compacted;
    }

    /// <summary>Anthropic: "Invalid `signature` in `thinking` block. The block is bound to a different conversation".</summary>
    private static bool IsInvalidThinkingSignature(Exception ex)
        => ex.Message.Contains("signature", StringComparison.OrdinalIgnoreCase) &&
           ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase);

    private static bool HasReasoning(IEnumerable<ChatMessage> messages)
        => messages.Any(message => message.Contents.Any(content => content is TextReasoningContent));

    /// <summary>A copy of the history without reasoning blocks; a message that held nothing else is dropped. The caller's list is untouched.</summary>
    private static List<ChatMessage> WithoutReasoning(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var kept = message.Contents.Where(content => content is not TextReasoningContent).ToList();
            if (kept.Count == message.Contents.Count)
            {
                result.Add(message);
            }
            else if (kept.Count > 0)
            {
                result.Add(new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName, AdditionalProperties = message.AdditionalProperties });
            }
        }

        return result;
    }

    private static void DisableThinking(ChatOptions options)
    {
        if (options is VllmChatOptions vllm)
        {
            vllm.ThinkingEnabled = false;
        }
    }

    private static string Truncate(string text) => text.Length > 240 ? text[..240] : text;

    private ChatOptions ApplySampling(ChatOptions? options)
    {
        var effective = options ?? new ChatOptions();

        if (_omitSampling)
        {
            effective.Temperature = null;
            effective.TopP = null;
        }
        else if (_temperature.HasValue)
        {
            effective.Temperature = _temperature.Value;
        }

        if (!_omitSampling && _topP.HasValue)
        {
            effective.TopP = _topP.Value;
        }

        DisableThinkingForRequiredToolChoice(effective);
        return effective;
    }

    private static VllmToolWireOverride? ResolveWireOverride(ChatOptions? options)
    {
        var requiredToolName = ResolveRequiredToolName(options);

        var promptSnapshot = ExtractPromptSnapshotCorrelation(options);
        var optionToolNames = options?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToArray() ?? [];
        var expectedToolNames = string.IsNullOrWhiteSpace(requiredToolName)
            ? optionToolNames
            : optionToolNames.Any(name => string.Equals(name, requiredToolName, StringComparison.OrdinalIgnoreCase))
                ? [requiredToolName]
                : optionToolNames;
        var expectedToolChoice = ResolveToolChoiceDescription(options);

        return string.IsNullOrWhiteSpace(requiredToolName) &&
               expectedToolNames.Length == 0 &&
               string.IsNullOrWhiteSpace(expectedToolChoice) &&
               promptSnapshot == null
            ? null
            : new VllmToolWireOverride(
                string.IsNullOrWhiteSpace(requiredToolName) ? null : requiredToolName,
                expectedToolNames,
                expectedToolChoice,
                promptSnapshot?.QueryId,
                promptSnapshot?.SessionId,
                promptSnapshot?.Round);
    }

    private static void DisableThinkingForRequiredToolChoice(ChatOptions options)
    {
        if (options is VllmChatOptions vllmOptions && vllmOptions.ToolMode is RequiredChatToolMode)
        {
            vllmOptions.ThinkingEnabled = false;
        }
    }

    private static string? ResolveRequiredToolName(ChatOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        var toolMode = options.GetType().GetProperty("ToolMode")?.GetValue(options);
        if (toolMode == null)
        {
            return null;
        }

        foreach (var propertyName in new[] { "RequiredFunctionName", "RequiredToolName", "FunctionName", "ToolName", "Name" })
        {
            var value = toolMode.GetType().GetProperty(propertyName)?.GetValue(toolMode) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static PromptSnapshotCorrelation? ExtractPromptSnapshotCorrelation(ChatOptions? options)
    {
        if (options?.AdditionalProperties == null)
        {
            return null;
        }

        var queryId = ExtractAdditionalPropertyString(options, PromptSnapshotQueryIdProperty);
        var sessionId = ExtractAdditionalPropertyString(options, PromptSnapshotSessionIdProperty);
        var round = ExtractAdditionalPropertyInt(options, PromptSnapshotRoundProperty);
        return string.IsNullOrWhiteSpace(queryId) &&
               string.IsNullOrWhiteSpace(sessionId) &&
               !round.HasValue
            ? null
            : new PromptSnapshotCorrelation(queryId, sessionId, round);
    }

    private static string? ExtractAdditionalPropertyString(ChatOptions options, string key)
    {
        if (options.AdditionalProperties?.TryGetValue(key, out var value) != true)
        {
            return null;
        }

        options.AdditionalProperties.Remove(key);
        return value?.ToString();
    }

    private static int? ExtractAdditionalPropertyInt(ChatOptions options, string key)
    {
        var raw = ExtractAdditionalPropertyString(options, key);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static string? ResolveToolChoiceDescription(ChatOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        var toolMode = options.GetType().GetProperty("ToolMode")?.GetValue(options);
        return toolMode?.ToString();
    }

    private sealed record PromptSnapshotCorrelation(string? QueryId, string? SessionId, int? Round);
}
