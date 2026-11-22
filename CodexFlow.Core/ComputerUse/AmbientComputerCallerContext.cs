// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.ComputerUse;

public sealed record ComputerTrustedCallerContext(
    string SessionId,
    SessionWorkMode WorkMode,
    bool PlanModeActive,
    string? WorkspaceRoot = null);

/// <summary>
/// Per-turn host-bound ambient context for Computer Use tools.
/// Tools MUST resolve SessionId, WorkMode, and PlanModeActive exclusively from here
/// and NEVER from model-supplied arguments.
/// </summary>
public static class AmbientComputerCallerContext
{
    private sealed class TurnSlot(ComputerTrustedCallerContext caller)
    {
        public ComputerTrustedCallerContext Caller { get; } = caller;
        private int _screenshotsInjected;
        private ComputerScreenshotData? _attachedScreenshot;

        public void SetAttachedScreenshot(ComputerScreenshotData screenshot)
            => Volatile.Write(ref _attachedScreenshot, screenshot);

        public ComputerScreenshotData? ConsumeAttachedScreenshot()
            => Interlocked.Exchange(ref _attachedScreenshot, null);

        public bool TryConsumeScreenshotBudget(int maxPerTurn)
        {
            var limit = Math.Clamp(maxPerTurn, 1, 30);
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

    public static ComputerTrustedCallerContext? Current => Slot.Value?.Caller;

    public static bool TryConsumeScreenshotBudget(int maxPerTurn = 12)
        => Slot.Value is { } slot && slot.TryConsumeScreenshotBudget(maxPerTurn);

    internal static void SetAttachedScreenshot(ComputerScreenshotData screenshot)
        => Slot.Value?.SetAttachedScreenshot(screenshot);

    internal static ComputerScreenshotData? ConsumeAttachedScreenshot()
        => Slot.Value?.ConsumeAttachedScreenshot();

    public static IDisposable BeginScope(ComputerTrustedCallerContext caller)
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
