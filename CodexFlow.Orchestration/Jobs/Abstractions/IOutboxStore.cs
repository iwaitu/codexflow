// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

public interface IOutboxStore
{
    Task AppendAsync(OutboxEvent entry, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(DateTime cutoffUtc, int limit, CancellationToken ct);
    Task RecordProjectionAsync(IReadOnlyList<OutboxProjectionResult> results, CancellationToken ct);
    Task AdvanceLastEventSeqAsync(string jobId, long sequence, DateTime now, CancellationToken ct);
    Task<OutboxJobSnapshot?> FindJobAsync(string jobId, CancellationToken ct);
}

public sealed record OutboxProjectionResult(string EventId, DateTime? ProcessedAtUtc, string? Error);

public sealed record OutboxJobSnapshot(
    string JobId, string? SessionId, string? TaskId, string JobType, string? RunnerId, int Attempt,
    BackgroundJobStatus Status, string PayloadJson, string? ResultPayloadJson, string? ShortSummary,
    string? WaitingReason, string? ErrorMessage, string? LatestCheckpointStep);
