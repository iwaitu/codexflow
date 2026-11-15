// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Services;

/// <summary>
/// Publishes planner summary updates to all registered Codex event sinks.
/// </summary>
public sealed class CodexEventSinkPlannerSummaryPublisher(
    IEnumerable<ICodexEventSink> sinks,
    ILogger<CodexEventSinkPlannerSummaryPublisher> logger) : IPlannerSummaryPublisher
{
    /// <summary>
    /// Publishes a planner summary update for the specified session.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="update">Planner summary update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishAsync(CodexSession session, PlannerSummaryUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(session.Id))
        {
            logger.LogDebug("Skip planner summary publish because session id is empty. Message={Message}", update.Message);
            return;
        }

        var evt = new CodexEvent
        {
            SessionId = session.Id,
            Type = CodexEventType.PlanningSummary,
            Message = update.Message,
            Payload = update,
            Timestamp = update.TimestampUtc
        };

        foreach (var sink in sinks)
        {
            try
            {
                await sink.PublishAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Planner summary sink publish failed. Sink={SinkType} SessionId={SessionId} Kind={Kind}",
                    sink.GetType().Name,
                    session.Id,
                    update.Kind);
            }
        }
    }
}
