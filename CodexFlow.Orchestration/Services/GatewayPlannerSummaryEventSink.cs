// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;

namespace CodexFlow.Services;

/// <summary>
/// Publishes planning summary events directly to the gateway SSE channel.
/// </summary>
public sealed class GatewayPlannerSummaryEventSink(
    ICodexGateway gateway,
    ILogger<GatewayPlannerSummaryEventSink> logger) : ICodexEventSink
{
    /// <summary>
    /// Publishes a planning summary event to gateway subscribers when the event carries a valid session identifier.
    /// </summary>
    /// <param name="e">Codex event to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishAsync(CodexEvent e, CancellationToken ct = default)
    {
        if (e.Type != CodexEventType.PlanningSummary || string.IsNullOrWhiteSpace(e.SessionId))
        {
            return;
        }

        try
        {
            await gateway.PublishEventAsync(
                    e.SessionId,
                    new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.PlanningSummary,
                        Payload = new
                        {
                            type = GatewaySseEventType.PlanningSummary,
                            message = e.Message,
                            payload = e.Payload,
                            timestamp = e.Timestamp
                        }
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GatewayPlannerSummaryEventSink] 推送 planning summary 失败: Session={SessionId}", e.SessionId);
        }
    }
}
