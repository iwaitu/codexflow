// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Infrastructure.Persistence;

/// <summary>
/// <see cref="IMemoryService"/> with long-term memory switched off, for hosts without MongoDB: nothing is
/// remembered, nothing is recalled, and summaries are not produced by a model.
/// </summary>
public sealed class NullMemoryService : IMemoryService
{
    public Task LearnFactAsync(string sessionId, string key, string value, string category = "general", string? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<MemoryEntry>> RecallFactsAsync(string sessionId, string query = "", CancellationToken ct = default)
        => Task.FromResult(new List<MemoryEntry>());

    public Task ForgetFactAsync(string sessionId, string key, CancellationToken ct = default) => Task.CompletedTask;

    public Task LearnUserFactAsync(string userId, string key, string value, string category = "preference", string? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<MemoryEntry>> RecallUserFactsAsync(string userId, string query = "", CancellationToken ct = default)
        => Task.FromResult(new List<MemoryEntry>());

    public Task ForgetUserFactAsync(string userId, string key, CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendTurnAsync(string sessionId, ChatTurn turn, CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<ChatTurn>> GetRecentTurnsAsync(string sessionId, int count = 10, CancellationToken ct = default)
        => Task.FromResult(new List<ChatTurn>());

    public Task<string> SummarizeHistoryAsync(string sessionId, string existingSummary, IReadOnlyList<ChatTurn> newTurns, CancellationToken ct = default)
        => Task.FromResult(existingSummary);

    public Task<string> SummarizeExecutionAsync(string sessionId, IReadOnlyList<string> completedTasks, string? verificationSummary = null, CancellationToken ct = default)
        => Task.FromResult(string.Join("; ", completedTasks));

    public Task<IReadOnlyList<(string Key, string Value, string Category)>> ExtractStructuredFactsAsync(string executionSummary, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string Key, string Value, string Category)>>(Array.Empty<(string, string, string)>());

    public Task LogEventAsync(string sessionId, string eventType, string data, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearSessionMemoryAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
}
