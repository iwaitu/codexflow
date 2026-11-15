// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CodexFlow.Infrastructure.Persistence;

/// <summary>
/// <see cref="ISessionStore"/> that keeps each session as JSON in an <see cref="IKeyValueTextStore"/>, for hosts
/// without Redis. A session that cannot be read back is treated as missing, like <see cref="RedisSessionStore"/>.
/// </summary>
public sealed class KeyValueSessionStore : ISessionStore
{
    private const string KeyPrefix = "Codex:Session:";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);

    private readonly IKeyValueTextStore _store;
    private readonly ILogger<KeyValueSessionStore> _logger;

    public KeyValueSessionStore(IKeyValueTextStore store, ILogger<KeyValueSessionStore> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodexSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var payload = await _store.GetAsync(KeyPrefix + sessionId, ct).ConfigureAwait(false);
            return string.IsNullOrEmpty(payload) ? null : JsonConvert.DeserializeObject<CodexSession>(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load session {SessionId}.", sessionId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveSessionAsync(CodexSession session, CancellationToken ct = default)
    {
        try
        {
            await _store.SetAsync(KeyPrefix + session.Id, JsonConvert.SerializeObject(session), SessionTtl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to save session {SessionId}.", session.Id);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _store.DeleteAsync(KeyPrefix + sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delete session {SessionId}.", sessionId);
        }
    }
}
