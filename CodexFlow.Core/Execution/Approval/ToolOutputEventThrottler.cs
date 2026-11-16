// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text;
using CodexFlow.Core.Gateway;

namespace CodexFlow.Core.Execution.Approval;

/// <summary>
/// Coalesces and strictly rate-limits streaming <c>tool_output</c> SSE events on the server side using
/// a serialized publish gate and monotonic minimum interval so bursty stdout/stderr can neither reorder
/// chunks nor flood subscriber bounded channels (<c>DropOldest</c>).
/// </summary>
public sealed class ToolOutputEventThrottler : IAsyncDisposable
{
    private readonly ICodexGateway? _gateway;
    private readonly string _sessionId;
    private readonly string _toolName;
    private readonly string _taskId;
    private readonly TimeSpan _minFlushInterval;
    private readonly int _maxEventsPerExecution;
    private readonly int _maxChunkChars;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _publishSerializer = new(1, 1);
    private readonly StringBuilder _stdoutBuffer = new();
    private readonly StringBuilder _stderrBuffer = new();
    private long _lastFlushStamp;
    private int _emittedEvents;
    private int _suppressedLines;
    private bool _capReachedNotified;

    public ToolOutputEventThrottler(
        ICodexGateway? gateway,
        string sessionId,
        string toolName,
        string taskId,
        TimeSpan? minFlushInterval = null,
        int maxEventsPerExecution = 100,
        int maxChunkChars = 4096)
    {
        _gateway = gateway;
        _sessionId = sessionId;
        _toolName = toolName;
        _taskId = taskId;
        _minFlushInterval = minFlushInterval ?? TimeSpan.FromMilliseconds(200);
        _maxEventsPerExecution = Math.Max(1, maxEventsPerExecution);
        _maxChunkChars = Math.Max(256, maxChunkChars);
    }

    public int EmittedEventCount
    {
        get { lock (_gate) { return _emittedEvents; } }
    }

    public void Append(string stream, string line)
    {
        if (_gateway is null || string.IsNullOrEmpty(_sessionId) || line is null)
        {
            return;
        }

        var shouldTriggerPump = false;
        lock (_gate)
        {
            if (_emittedEvents >= _maxEventsPerExecution)
            {
                _suppressedLines++;
                return;
            }

            var target = string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase)
                ? _stderrBuffer
                : _stdoutBuffer;

            // Strictly bound single-line and total buffer capacity so an oversized single line cannot blow memory
            var maxBufferChars = _maxChunkChars * 2;
            var boundedLine = line.Length > _maxChunkChars
                ? string.Concat(line.AsSpan(0, _maxChunkChars), "...[line truncated]")
                : line;

            var needed = (target.Length > 0 ? 1 : 0) + boundedLine.Length;
            if (target.Length + needed <= maxBufferChars)
            {
                if (target.Length > 0)
                {
                    target.Append('\n');
                }

                target.Append(boundedLine);
            }
            else if (target.Length == 0)
            {
                target.Append(boundedLine.AsSpan(0, Math.Min(boundedLine.Length, maxBufferChars)));
            }
            else
            {
                _suppressedLines++;
            }

            var nowStamp = Stopwatch.GetTimestamp();
            if (_lastFlushStamp == 0 || Stopwatch.GetElapsedTime(_lastFlushStamp, nowStamp) >= _minFlushInterval)
            {
                shouldTriggerPump = true;
            }
        }

        if (shouldTriggerPump)
        {
            _ = TryPumpSerializedAsync(forceDrainAll: false, CancellationToken.None);
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        if (_gateway is null || string.IsNullOrEmpty(_sessionId))
        {
            return;
        }

        await TryPumpSerializedAsync(forceDrainAll: true, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _publishSerializer.Dispose();
    }

    private async Task TryPumpSerializedAsync(bool forceDrainAll, CancellationToken ct)
    {
        if (!forceDrainAll)
        {
            if (!await _publishSerializer.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            await _publishSerializer.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            while (true)
            {
                object? batchPayload = null;
                object? capPayload = null;
                lock (_gate)
                {
                    var nowStamp = Stopwatch.GetTimestamp();
                    var intervalSatisfied = forceDrainAll ||
                                            _lastFlushStamp == 0 ||
                                            Stopwatch.GetElapsedTime(_lastFlushStamp, nowStamp) >= _minFlushInterval;

                    if (intervalSatisfied &&
                        _emittedEvents < _maxEventsPerExecution &&
                        (_stdoutBuffer.Length > 0 || _stderrBuffer.Length > 0))
                    {
                        batchPayload = DrainBatchLocked(nowStamp);
                    }
                    else if (forceDrainAll && (_stdoutBuffer.Length > 0 || _stderrBuffer.Length > 0))
                    {
                        _suppressedLines += CountRemainingLines(_stdoutBuffer) + CountRemainingLines(_stderrBuffer);
                        _stdoutBuffer.Clear();
                        _stderrBuffer.Clear();
                    }

                    if (forceDrainAll && batchPayload is null && _suppressedLines > 0 && !_capReachedNotified)
                    {
                        _capReachedNotified = true;
                        capPayload = new
                        {
                            tool = _toolName,
                            commandTaskId = _taskId,
                            throttled = true,
                            suppressedLines = _suppressedLines
                        };
                    }
                }

                if (batchPayload is not null)
                {
                    await PublishSafeAsync(batchPayload, ct).ConfigureAwait(false);
                    if (!forceDrainAll)
                    {
                        break;
                    }

                    continue;
                }

                if (capPayload is not null)
                {
                    await PublishSafeAsync(capPayload, ct).ConfigureAwait(false);
                }

                break;
            }
        }
        finally
        {
            _publishSerializer.Release();
        }
    }

    private object DrainBatchLocked(long nowStamp)
    {
        var outChunk = TakeBounded(_stdoutBuffer, _maxChunkChars);
        var errChunk = TakeBounded(_stderrBuffer, _maxChunkChars);
        _lastFlushStamp = nowStamp;
        _emittedEvents++;
        return new
        {
            tool = _toolName,
            commandTaskId = _taskId,
            stdout = outChunk,
            stderr = errChunk,
            throttled = false
        };
    }

    private static int CountRemainingLines(StringBuilder sb)
    {
        if (sb.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static string TakeBounded(StringBuilder sb, int maxChars)
    {
        if (sb.Length == 0)
        {
            return string.Empty;
        }

        if (sb.Length <= maxChars)
        {
            var all = sb.ToString();
            sb.Clear();
            return all;
        }

        var head = sb.ToString(0, maxChars);
        sb.Remove(0, maxChars);
        return head;
    }

    private async Task PublishSafeAsync(object payload, CancellationToken ct)
    {
        try
        {
            await _gateway!.PublishEventAsync(
                _sessionId,
                new GatewaySseEvent { Type = GatewaySseEventType.ToolOutput, Payload = payload },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Output streaming is best-effort telemetry; never fail tool execution.
        }
    }
}
