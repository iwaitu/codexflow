// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.ComputerUse;

internal struct PhysicalEscDoubleTapTracker
{
    private const int DoubleTapIntervalMs = 450;
    private bool _escDown;
    private long? _lastTapMs;

    public bool Observe(bool pressed, bool injected, long timestampMs)
    {
        if (injected || pressed == _escDown)
        {
            return false;
        }

        _escDown = pressed;
        if (!pressed)
        {
            return false;
        }

        if (_lastTapMs is { } lastTap && timestampMs - lastTap <= DoubleTapIntervalMs)
        {
            _lastTapMs = null;
            return true;
        }

        _lastTapMs = timestampMs;
        return false;
    }
}
