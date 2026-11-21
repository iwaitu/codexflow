// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;

namespace CodexFlow.Core.Security;

/// <summary>
/// Thread-safe registry of local loopback TCP ports that outbound agents, plugins,
/// and managed browsers must NEVER dial (e.g., CodexFlow host HTTP port, browser CDP ports,
/// internal outbound proxy ports).
/// </summary>
public sealed class ProtectedLocalPortRegistry
{
    private readonly ConcurrentDictionary<int, string> _ports = new();

    public void RegisterPort(int port, string owner = "host")
    {
        if (port is > 0 and <= 65535)
        {
            _ports[port] = string.IsNullOrWhiteSpace(owner) ? "host" : owner.Trim();
        }
    }

    public void UnregisterPort(int port)
    {
        if (port is > 0 and <= 65535)
        {
            _ports.TryRemove(port, out _);
        }
    }

    public bool IsProtected(int port)
        => port is > 0 and <= 65535 && _ports.ContainsKey(port);

    public IReadOnlyCollection<int> GetProtectedPorts()
        => _ports.Keys.ToArray();
}
