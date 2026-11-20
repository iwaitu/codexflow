// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Browser.Cdp;

/// <summary>
/// Pure .NET 10 WebSocket Chrome DevTools Protocol (CDP) client with:
/// - Direct loopback dial (Proxy = null);
/// - Fragmented large message reassembly with hard 32 MB cap and fail-closed socket abort;
/// - Per-command default timeout (30s) so runaway page JS can never hang sessions indefinitely;
/// - Flat-mode CDP sessionId multiplexing and structured event dispatch.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private const int MaxIncomingMessageBytes = 32 * 1024 * 1024; // 32 MB safety cap
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ClientWebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly Task _receiveLoop;
    private int _nextId;
    private int _disposed;
    private int _disconnectNotified;
    private volatile bool _faulted;

    public event Action<CdpEventMessage>? EventReceived;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _disposed == 0 && !_faulted && _socket.State == WebSocketState.Open;

    private CdpConnection(ClientWebSocket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public static async Task<CdpConnection> ConnectAsync(Uri wsEndpoint, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wsEndpoint);
        ArgumentNullException.ThrowIfNull(logger);

        var socket = new ClientWebSocket();
        socket.Options.Proxy = null; // Never route loopback CDP through system HTTP proxies.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(wsEndpoint, ct).ConfigureAwait(false);
            return new CdpConnection(socket, logger);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> SendAsync(
        string method,
        JsonElement? parameters = null,
        string? sessionId = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (_faulted || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("CDP WebSocket connection is closed or faulted.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new CdpCommandRequest
        {
            Id = id,
            Method = method,
            Params = parameters,
            SessionId = sessionId
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(request, CdpJsonContext.Default.CdpCommandRequest);

        using var cmdTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cmdTimeoutCts.CancelAfter(timeout ?? DefaultCommandTimeout);
        var effectiveCt = cmdTimeoutCts.Token;

        try
        {
            await _sendLock.WaitAsync(effectiveCt).ConfigureAwait(false);
            try
            {
                if (_faulted || _socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("CDP WebSocket connection is closed.");
                }

                await _socket.SendAsync(payloadBytes, WebSocketMessageType.Text, endOfMessage: true, effectiveCt).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var reg = effectiveCt.Register(static state =>
            {
                var (pending, cmdId, callerCt, m) = ((ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>, int, CancellationToken, string))state!;
                if (pending.TryRemove(cmdId, out var waiter))
                {
                    if (callerCt.IsCancellationRequested)
                    {
                        waiter.TrySetCanceled(callerCt);
                    }
                    else
                    {
                        waiter.TrySetException(new TimeoutException($"CDP command '{m}' timed out."));
                    }
                }
            }, (_pending, id, ct, method));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public Task<JsonElement> SendJsonAsync(
        string method,
        string? jsonParams = null,
        string? sessionId = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        JsonElement? element = null;
        if (!string.IsNullOrWhiteSpace(jsonParams))
        {
            using var doc = JsonDocument.Parse(jsonParams);
            element = doc.RootElement.Clone();
        }

        return SendAsync(method, element, sessionId, ct, timeout);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _faulted = true;
                        var closeEx = new IOException("CDP WebSocket closed by browser.");
                        FailAllPending(closeEx);
                        NotifyDisconnected(closeEx);
                        return;
                    }

                    if (messageBuffer.Length + result.Count > MaxIncomingMessageBytes)
                    {
                        _faulted = true;
                        _logger.LogWarning("CDP incoming frame exceeded {MaxBytes} bytes; aborting socket.", MaxIncomingMessageBytes);
                        try { _socket.Abort(); } catch (Exception) { }
                        var sizeEx = new InvalidDataException("CDP message exceeded maximum allowed size.");
                        FailAllPending(sizeEx);
                        NotifyDisconnected(sizeEx);
                        return;
                    }

                    if (result.Count > 0)
                    {
                        messageBuffer.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || messageBuffer.Length == 0)
                {
                    continue;
                }

                DispatchRawMessage(messageBuffer.GetBuffer().AsSpan(0, (int)messageBuffer.Length));
            }
        }
        catch (OperationCanceledException)
        {
            _faulted = true;
            var cancelEx = new OperationCanceledException("CDP connection shut down.");
            FailAllPending(cancelEx);
            NotifyDisconnected(cancelEx);
        }
        catch (Exception ex)
        {
            _faulted = true;
            _logger.LogDebug(ex, "CDP receive loop terminated unexpectedly.");
            try { _socket.Abort(); } catch (Exception) { }
            FailAllPending(ex);
            NotifyDisconnected(ex);
        }
        finally
        {
            NotifyDisconnected(null);
        }
    }

    private void DispatchRawMessage(ReadOnlySpan<byte> utf8Bytes)
    {
        CdpIncomingMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize(utf8Bytes, CdpJsonContext.Default.CdpIncomingMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse incoming CDP message ({Length} bytes).", utf8Bytes.Length);
            return;
        }

        if (msg is null)
        {
            return;
        }

        if (msg.Id is { } responseId)
        {
            if (_pending.TryRemove(responseId, out var waiter))
            {
                if (msg.Error is not null)
                {
                    var dataText = msg.Error.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d
                        ? d.ToString()
                        : null;
                    var errText = string.IsNullOrWhiteSpace(dataText)
                        ? $"CDP error {msg.Error.Code}: {msg.Error.Message}"
                        : $"CDP error {msg.Error.Code}: {msg.Error.Message} ({dataText})";
                    waiter.TrySetException(new InvalidOperationException(errText));
                }
                else
                {
                    var cloned = msg.Result?.Clone() ?? default;
                    waiter.TrySetResult(cloned);
                }
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(msg.Method))
        {
            var evt = new CdpEventMessage(msg.Method, msg.SessionId, msg.Params?.Clone() ?? default);
            var handlers = EventReceived;
            if (handlers is not null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handlers(evt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Unhandled exception in CDP event handler for {Method}", evt.Method);
                    }
                });
            }
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var waiter))
            {
                waiter.TrySetException(ex);
            }
        }
    }

    private void NotifyDisconnected(Exception? ex)
    {
        if (Interlocked.Exchange(ref _disconnectNotified, 1) != 0)
        {
            return;
        }

        var handler = Disconnected;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler(ex);
                }
                catch (Exception callbackEx)
                {
                    _logger.LogDebug(callbackEx, "Unhandled exception in CDP Disconnected callback.");
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _faulted = true;
        try { await _receiveCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Ignore close errors.
        }

        try { await _receiveLoop.ConfigureAwait(false); } catch (Exception) { }
        FailAllPending(new ObjectDisposedException(nameof(CdpConnection)));
        NotifyDisconnected(null);
        _socket.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
    }
}
