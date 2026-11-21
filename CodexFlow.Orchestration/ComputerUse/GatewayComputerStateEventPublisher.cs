// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Gateway;

namespace CodexFlow.Orchestration.ComputerUse;

public sealed class GatewayComputerStateEventPublisher(ICodexGateway gateway) : IComputerStateEventPublisher
{
    public async ValueTask PublishStateUpdatedAsync(
        string sessionId,
        ComputerSessionStateDto state,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(state);

        await gateway.PublishEventAsync(
            sessionId,
            new GatewaySseEvent
            {
                Type = GatewaySseEventType.ComputerStateUpdated,
                Payload = state
            },
            ct).ConfigureAwait(false);
    }
}
