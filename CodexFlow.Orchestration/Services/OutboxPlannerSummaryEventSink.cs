// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace CodexFlow.Services;

/// <summary>
/// Persists planning summary events into the outbox so downstream projectors can fan them out.
/// </summary>
public sealed class OutboxPlannerSummaryEventSink(
    IServiceScopeFactory scopeFactory,
    IOutboxSignal outboxSignal,
    ILogger<OutboxPlannerSummaryEventSink> logger) : ICodexEventSink
{
    /// <summary>
    /// Writes a planning summary event into the outbox and pulses the outbox signal.
    /// </summary>
    /// <param name="e">Codex event to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishAsync(CodexEvent e, CancellationToken ct = default)
    {
        if (e.Type != CodexEventType.PlanningSummary || string.IsNullOrWhiteSpace(e.SessionId))
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CodexFlow.Orchestration.Jobs.IJobDbContext>();
            var payloadJson = JsonConvert.SerializeObject(new
            {
                message = e.Message,
                payload = e.Payload,
                timestamp = e.Timestamp
            });

            await dbContext.OutboxEvents.AddAsync(new OutboxEvent
            {
                EventType = "PlanningSummary",
                SessionId = e.SessionId,
                TaskId = e.TaskId,
                Payload = payloadJson,
                CorrelationId = e.SessionId
            }, ct).ConfigureAwait(false);

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            outboxSignal.Pulse();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[OutboxPlannerSummaryEventSink] 写入 planning summary outbox 失败: Session={SessionId}", e.SessionId);
        }
    }
}
