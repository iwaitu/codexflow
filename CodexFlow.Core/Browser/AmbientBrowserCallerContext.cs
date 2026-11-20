// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Browser;

/// <summary>
/// Per-turn host-bound ambient context for browser tools.
/// Tools MUST resolve SessionId, WorkMode, PlanModeActive, and WorkspaceRoot exclusively from here
/// and NEVER from model-supplied arguments.
/// </summary>
public static class AmbientBrowserCallerContext
{
    private sealed class TurnSlot(BrowserTrustedCallerContext caller)
    {
        public BrowserTrustedCallerContext Caller { get; } = caller;
        private int _screenshotsInjected;

        public bool TryConsumeScreenshotBudget(int maxPerTurn)
        {
            var limit = Math.Clamp(maxPerTurn, 1, 12);
            var next = Interlocked.Increment(ref _screenshotsInjected);
            if (next > limit)
            {
                Interlocked.Decrement(ref _screenshotsInjected);
                return false;
            }

            return true;
        }
    }

    private static readonly AsyncLocal<TurnSlot?> Slot = new();

    public static BrowserTrustedCallerContext? Current => Slot.Value?.Caller;

    public static bool TryConsumeScreenshotBudget(int maxPerTurn = 4)
        => Slot.Value is { } slot && slot.TryConsumeScreenshotBudget(maxPerTurn);

    public static IDisposable BeginScope(BrowserTrustedCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var previous = Slot.Value;
        Slot.Value = new TurnSlot(caller);
        return new ScopeGuard(previous);
    }

    private sealed class ScopeGuard(TurnSlot? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Slot.Value = previous;
            }
        }
    }
}
