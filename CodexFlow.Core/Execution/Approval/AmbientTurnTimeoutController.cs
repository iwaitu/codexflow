// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace CodexFlow.Core.Execution.Approval;

/// <summary>
/// Ambient per-turn timeout pause controller. While a tool call is suspended waiting for interactive
/// user approval, active turn/attempt timers pause their countdown using monotonic <see cref="Stopwatch"/>
/// timestamps and resume with their remaining budget once the user resolves the approval.
/// </summary>
public sealed class AmbientTurnTimeoutController
{
    private static readonly AsyncLocal<AmbientTurnTimeoutController?> CurrentSlot = new();

    private readonly object _gate = new();
    private readonly List<PausableDeadline> _deadlines = [];
    private int _pauseDepth;

    public static AmbientTurnTimeoutController? Current => CurrentSlot.Value;

    public static IDisposable BeginScope(out AmbientTurnTimeoutController controller)
    {
        var previous = CurrentSlot.Value;
        controller = new AmbientTurnTimeoutController();
        CurrentSlot.Value = controller;
        return new ScopeGuard(previous);
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pauseDepth > 0;
            }
        }
    }

    public IDisposable PauseTimers()
    {
        lock (_gate)
        {
            _pauseDepth++;
            if (_pauseDepth == 1)
            {
                foreach (var d in _deadlines)
                {
                    d.Pause();
                }
            }
        }

        return new ResumeGuard(this);
    }

    public PausableDeadline CreateDeadline(TimeSpan timeout, CancellationToken parentToken)
    {
        var deadline = new PausableDeadline(this, timeout, parentToken);
        lock (_gate)
        {
            _deadlines.Add(deadline);
            if (_pauseDepth > 0)
            {
                deadline.Pause();
            }
        }

        return deadline;
    }

    internal void Unregister(PausableDeadline deadline)
    {
        lock (_gate)
        {
            _deadlines.Remove(deadline);
        }
    }

    private void ResumeOnce()
    {
        lock (_gate)
        {
            if (_pauseDepth <= 0)
            {
                return;
            }

            _pauseDepth--;
            if (_pauseDepth == 0)
            {
                foreach (var d in _deadlines)
                {
                    d.Resume();
                }
            }
        }
    }

    private sealed class ScopeGuard(AmbientTurnTimeoutController? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentSlot.Value = previous;
            }
        }
    }

    private sealed class ResumeGuard(AmbientTurnTimeoutController owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ResumeOnce();
            }
        }
    }
}

public sealed class PausableDeadline : IDisposable
{
    private readonly AmbientTurnTimeoutController _owner;
    private readonly CancellationTokenSource _cts;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private TimeSpan _remaining;
    private long? _runningSinceStamp;
    private bool _paused;
    private bool _timedOut;
    private bool _disposed;

    internal PausableDeadline(AmbientTurnTimeoutController owner, TimeSpan timeout, CancellationToken parentToken)
    {
        _owner = owner;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _remaining = timeout;
        _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (timeout > TimeSpan.Zero)
        {
            _runningSinceStamp = Stopwatch.GetTimestamp();
            _timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    public CancellationToken Token => _cts.Token;

    public bool IsTimedOut
    {
        get
        {
            lock (_gate)
            {
                return _timedOut;
            }
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (_disposed || _paused || _timedOut || _remaining <= TimeSpan.Zero)
            {
                return;
            }

            _paused = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_runningSinceStamp is { } stamp)
            {
                var elapsed = Stopwatch.GetElapsedTime(stamp);
                _remaining = elapsed >= _remaining ? TimeSpan.FromMilliseconds(1) : _remaining - elapsed;
                _runningSinceStamp = null;
            }
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_disposed || !_paused || _timedOut || _remaining <= TimeSpan.Zero)
            {
                return;
            }

            _paused = false;
            _runningSinceStamp = Stopwatch.GetTimestamp();
            _timer.Change(_remaining, Timeout.InfiniteTimeSpan);
        }
    }

        private void OnTimerTick(object? state)
        {
            bool shouldCancel = false;
            lock (_gate)
            {
                if (_disposed || _paused || _timedOut)
                {
                    return;
                }

                // Re-verify monotonic remaining budget in case a queued timer callback raced with a quick Pause/Resume cycle
                if (_runningSinceStamp is { } stamp)
                {
                    var elapsed = Stopwatch.GetElapsedTime(stamp);
                    if (elapsed + TimeSpan.FromMilliseconds(2) < _remaining)
                    {
                        var remainingLeft = _remaining - elapsed;
                        _remaining = remainingLeft;
                        _runningSinceStamp = Stopwatch.GetTimestamp();
                        _timer.Change(remainingLeft, Timeout.InfiniteTimeSpan);
                        return;
                    }
                }

                _timedOut = true;
                _remaining = TimeSpan.Zero;
                shouldCancel = true;
            }

        if (shouldCancel)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _owner.Unregister(this);
        _timer.Dispose();
        _cts.Dispose();
    }
}
