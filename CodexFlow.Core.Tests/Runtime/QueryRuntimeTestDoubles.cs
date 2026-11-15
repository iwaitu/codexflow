// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using System.Reflection;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Tests.Runtime;

internal sealed class ScriptedLlmExecutor(
    params Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] steps) : ILLMExecutor
{
    private readonly Queue<Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _steps = new(steps);

    public List<LLMExecutionRequest> Requests { get; } = [];

    public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Layer A QueryRuntime tests use StreamAsync only.");

    public IAsyncEnumerable<ChatResponseUpdate> StreamAsync(LLMExecutionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("No scripted LLM step remains.");
        }

        return _steps.Dequeue()(request, ct);
    }

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> TextStep(string text)
        => (_, _) => StreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]));

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> FunctionCallStep(
        string callId,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
        => (_, _) => StreamUpdates(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase))]));

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> MultiStep(
        params ChatResponseUpdate[] updates)
        => (_, _) => StreamUpdates(updates);

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> ReasoningStep(
        params (bool Thinking, string Text)[] updates)
        => (_, _) => StreamUpdates(updates.Select(update => CreateReasoningUpdate(update.Thinking, update.Text)).ToArray());

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> ThrowingStep(Exception ex)
        => (_, _) => StreamThrowing(ex);

    public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> InspectingStep(
        Action<LLMExecutionRequest> inspect,
        params ChatResponseUpdate[] updates)
        => (request, _) =>
        {
            inspect(request);
            return StreamUpdates(updates);
        };

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdates(
        params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return update;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamThrowing(
        Exception ex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
#pragma warning disable CS0162
        if (false)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, []);
        }
#pragma warning restore CS0162

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw ex;
    }

    private static ChatResponseUpdate CreateReasoningUpdate(bool thinking, string text)
    {
        var reasoningType = AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetType("Microsoft.Extensions.AI.ReasoningChatResponseUpdate", throwOnError: false))
            .FirstOrDefault(type => type != null)
            ?? Type.GetType("Microsoft.Extensions.AI.ReasoningChatResponseUpdate, Microsoft.Extensions.AI.VllmChatClient", throwOnError: false);

        if (reasoningType == null)
        {
            throw new InvalidOperationException("ReasoningChatResponseUpdate type was not found in loaded assemblies.");
        }

        var parameterlessCtor = reasoningType.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor != null)
        {
            var update = (ChatResponseUpdate)parameterlessCtor.Invoke(null);
            reasoningType.GetProperty("Thinking")?.SetValue(update, thinking);
            reasoningType.GetProperty("Reasoning")?.SetValue(update, thinking ? text : string.Empty);
            reasoningType.GetProperty("Answer")?.SetValue(update, thinking ? string.Empty : text);
            reasoningType.GetProperty("Anwser")?.SetValue(update, thinking ? string.Empty : text);

            var textProperty = typeof(ChatResponseUpdate).GetProperty(nameof(ChatResponseUpdate.Text));
            if (textProperty?.CanWrite == true)
            {
                textProperty.SetValue(update, text);
            }

            var contentsProperty = typeof(ChatResponseUpdate).GetProperty(nameof(ChatResponseUpdate.Contents));
            if (contentsProperty?.CanWrite == true)
            {
                contentsProperty.SetValue(update, new AIContent[] { new TextContent(text) });
            }

            return update;
        }

        var constructors = reasoningType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(ctor => ctor.GetParameters().Length);

        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            var arguments = new object?[parameters.Length];
            var supported = true;
            var assignedText = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;

                if (parameterType == typeof(ChatRole))
                {
                    arguments[i] = ChatRole.Assistant;
                }
                else if (parameterType == typeof(bool))
                {
                    arguments[i] = thinking;
                }
                else if (parameterType == typeof(string))
                {
                    arguments[i] = assignedText ? string.Empty : text;
                    assignedText = true;
                }
                else if (typeof(IEnumerable<AIContent>).IsAssignableFrom(parameterType) || parameterType == typeof(IReadOnlyList<AIContent>) || parameterType == typeof(IList<AIContent>))
                {
                    arguments[i] = new AIContent[] { new TextContent(text) };
                }
                else if (parameterType == typeof(AIContent[]))
                {
                    arguments[i] = new AIContent[] { new TextContent(text) };
                }
                else if (parameterType == typeof(ChatResponseUpdate))
                {
                    arguments[i] = new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
                }
                else if (parameterType.IsValueType)
                {
                    arguments[i] = Activator.CreateInstance(parameterType);
                }
                else
                {
                    arguments[i] = null;
                }
            }

            if (!supported)
            {
                continue;
            }

            try
            {
                return (ChatResponseUpdate)constructor.Invoke(arguments);
            }
            catch
            {
                // Try next constructor.
            }
        }

        throw new InvalidOperationException("Unable to construct ReasoningChatResponseUpdate via reflection.");
    }
}

internal sealed class CapturingRuntimeEventSink : IQueryRuntimeEventSink
{
    private readonly List<QueryRuntimeEvent> _events = [];

    public IReadOnlyList<QueryRuntimeEvent> Events => _events;

    public bool IsEnabled(QueryRuntimeEventType eventType) => true;

    public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        _events.Add(runtimeEvent);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingContextWindowManager : IContextWindowManager
{
    public List<QueryRuntimeRequest> StartedTurns { get; } = [];
    public List<(QueryRuntimeRequest Request, QueryRuntimeResult Result)> Completions { get; } = [];

    public Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        StartedTurns.Add(request);
        return Task.CompletedTask;
    }

    public Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        Completions.Add((request, result));
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingContextWindowManager(Exception exception) : IContextWindowManager
{
    public int StartCallCount { get; private set; }
    public int CompletionCallCount { get; private set; }

    public Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        StartCallCount++;
        throw exception;
    }

    public Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        CompletionCallCount++;
        throw exception;
    }
}

internal sealed class CompletionThrowingContextWindowManager(Exception exception) : IContextWindowManager
{
    public int StartCallCount { get; private set; }
    public int CompletionCallCount { get; private set; }

    public Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        StartCallCount++;
        return Task.CompletedTask;
    }

    public Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        CompletionCallCount++;
        throw exception;
    }
}

internal sealed class DelegateInterventionHook(
    Func<string, IDictionary<string, object?>, object?, CancellationToken, ValueTask<QueryRuntimeIntervention>>? onToolCallRequested = null,
    Func<string, string, bool, object?, CancellationToken, ValueTask<QueryRuntimeIntervention>>? onToolExecutionCompleted = null)
    : IQueryRuntimeInterventionHook
{
    public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default)
        => onToolCallRequested?.Invoke(toolName, arguments, session, ct)
           ?? ValueTask.FromResult(QueryRuntimeIntervention.None);

    public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default)
        => onToolExecutionCompleted?.Invoke(toolName, result, success, session, ct)
           ?? ValueTask.FromResult(QueryRuntimeIntervention.None);
}
