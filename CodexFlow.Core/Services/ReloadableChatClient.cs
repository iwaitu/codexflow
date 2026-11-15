// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace CodexFlow.Core.Services;

/// <summary>
/// An <see cref="IChatClient"/> that follows its configuration: whenever the watched change token fires, a new inner
/// client is built from the current settings and later calls use it. Calls that already started finish on the client
/// they started with. If the new settings cannot build a client, the previous one stays in use.
/// </summary>
public sealed class ReloadableChatClient : IChatClient
{
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(5);

    private readonly Func<IChatClient> _factory;
    private readonly ILogger _logger;
    private readonly IDisposable? _subscription;
    private IChatClient _current;

    /// <param name="factory">Builds a client from the current configuration; called once now and once per change.</param>
    /// <param name="getReloadToken">Returns a fresh change token each time; usually <c>section.GetReloadToken</c>.</param>
    public ReloadableChatClient(Func<IChatClient> factory, Func<IChangeToken> getReloadToken, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(getReloadToken);
        _factory = factory;
        _logger = logger;
        _current = factory();
        _subscription = ChangeToken.OnChange(getReloadToken, Reload);
    }

    /// <summary>How many times the inner client has been replaced by a reload.</summary>
    public int ReloadCount { get; private set; }

    private void Reload()
    {
        IChatClient next;
        try
        {
            next = _factory();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model settings changed but do not build a chat client; keeping the previous client.");
            return;
        }

        var previous = Interlocked.Exchange(ref _current, next);
        ReloadCount++;
        _logger.LogInformation("Model settings changed; the chat client was rebuilt (reload {Count}).", ReloadCount);

        // A running call may still use the previous client, so it is disposed later.
        _ = Task.Delay(RetireDelay).ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Volatile.Read(ref _current).GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Volatile.Read(ref _current).GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => Volatile.Read(ref _current).GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _subscription?.Dispose();
        Volatile.Read(ref _current).Dispose();
    }
}
