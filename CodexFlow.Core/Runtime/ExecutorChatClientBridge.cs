// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
        var request = new LLMExecutionRequest(
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray(),
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
        var request = new LLMExecutionRequest(
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray(),
            options,
            _scenario,
            _session,
            _caller);
        await foreach (var update in _executor.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
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
