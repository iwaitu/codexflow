// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Gateway;

/// <summary>
/// Thrown to terminate a user message that explicitly requested a local repository the shared policy refused.
/// <see cref="SessionChannel"/> turns it into a correlated error event (and no done event), so the client sees a
/// deterministic structured refusal and no model/tool work has happened.
/// </summary>
public sealed class GatewayLocalRepositoryDeniedException : InvalidOperationException
{
    public GatewayLocalRepositoryDeniedException(string message) : base(message)
    {
    }
}
