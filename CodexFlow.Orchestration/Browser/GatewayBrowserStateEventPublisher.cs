// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Gateway;

namespace CodexFlow.Orchestration.Browser;

public sealed class GatewayBrowserStateEventPublisher(ICodexGateway gateway) : IBrowserStateEventPublisher
{
    public async ValueTask PublishStateUpdatedAsync(string sessionId, BrowserSessionStateDto state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(state);

        await gateway.PublishEventAsync(
            sessionId,
            new GatewaySseEvent
            {
                Type = GatewaySseEventType.BrowserStateUpdated,
                Payload = state
            },
            ct).ConfigureAwait(false);
    }
}
