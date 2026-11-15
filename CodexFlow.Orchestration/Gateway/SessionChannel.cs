// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading.Channels;
using CodexFlow.Core.Gateway;
using CodexFlow.Logging;

namespace CodexFlow.Gateway;

/// <summary>
/// 单个 Session 的消息通道 — 入队、顺序处理、广播 SSE 事件给所有订阅者
/// </summary>
public sealed class SessionChannel : IAsyncDisposable
{
    private const string UserChatBusyReason = "user_chat_already_in_progress";
    private const string UserChatBusyMessage = "当前主会话仍在处理中，请等待上一条消息完成后再发送。";
    private readonly Channel<GatewayMessage> _inbox;
    private readonly List<SseSubscriber> _subscribers = new();
    private readonly Lock _subscriberLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionChannel> _logger;
    private Task? _processorTask;

    // 环形事件缓冲区：用于新订阅者断线重连时回放错过的事件
    private readonly Queue<GatewaySseEvent> _eventBuffer = new();
    private readonly Lock _bufferLock = new();
    private const int MaxBufferedEvents = 4096;
    // Highest Seq the ring buffer has dropped. Guarded by _bufferLock.
    private long _evictedThroughSeq;

    private long _eventSeq;
    private int _pendingMessageCount;
    private int _pendingUserChatCount;

    /// <summary>Session identifier owned by this channel.</summary>
    public string SessionId { get; }
    /// <summary>
    /// Random id of this channel instance. It changes whenever the channel is recreated (gateway restart, idle cleanup), which is the
    /// only way a client can tell that the sequence numbers it remembers belong to a different stream.
    /// </summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("N");
    /// <summary>UTC timestamp when the channel was created.</summary>
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the last enqueue, publish, or processing activity.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    /// <summary>Whether the background processing loop is still running.</summary>
    public bool IsRunning => _processorTask is { IsCompleted: false };
    /// <summary>Whether the channel currently has queued or in-flight messages.</summary>
    public bool HasPendingMessages => Volatile.Read(ref _pendingMessageCount) > 0;

    /// <summary>
    /// Initializes a session channel.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="serviceProvider">Service provider used to resolve scoped processors.</param>
    /// <param name="logger">Logger used for per-session diagnostics.</param>
    public SessionChannel(string sessionId, IServiceProvider serviceProvider, ILogger<SessionChannel> logger)
    {
        SessionId = sessionId;
        _serviceProvider = serviceProvider;
        _logger = logger;

        // 有界通道，防止内存爆炸；BoundedChannelFullMode.Wait 提供背压
        _inbox = Channel.CreateBounded<GatewayMessage>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>启动消息处理循环</summary>
    public void Start()
    {
        if (_processorTask != null) return;
        _processorTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
    }

    /// <summary>入队消息</summary>
    public async ValueTask EnqueueAsync(GatewayMessage message, CancellationToken ct)
    {
        var reservedExclusiveUserChatSlot = TryReserveExclusiveUserChatSlot(message);
        Interlocked.Increment(ref _pendingMessageCount);
        LastActivityUtc = DateTime.UtcNow;
        try
        {
            await _inbox.Writer.WriteAsync(message, ct);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingMessageCount);
            if (reservedExclusiveUserChatSlot)
            {
                ReleaseExclusiveUserChatSlot(message);
            }

            throw;
        }
    }

    /// <summary>从 Gateway 外部直接广播 SSE 事件，不进入消息处理队列。</summary>
    public async ValueTask PublishExternalEventAsync(GatewaySseEvent sseEvent, CancellationToken ct)
    {
        LastActivityUtc = DateTime.UtcNow;
        var sequencedEvent = sseEvent with { Seq = Interlocked.Increment(ref _eventSeq) };
        await BroadcastAsync(sequencedEvent);
    }

    /// <summary>
    /// 添加一个 SSE 订阅者（对应一个 codex/sse HTTP 连接）。
    /// 若提供 lastEventId，会自动回放缓冲区中所有 Seq > lastEventId 的历史事件，
    /// 解决断线期间事件丢失的问题。
    /// </summary>
    public SseSubscriber Subscribe(long lastEventId = 0, string? clientEpoch = null)
    {
        // A client that remembers a position from a different channel instance, or one this channel has never reached, is following
        // a stream that no longer exists: its Last-Event-ID must not filter this channel's events. The epoch catches every restart;
        // the "ahead of us" test only covers clients that send no epoch (and cannot see a restart whose sequence already caught up).
        var sequenceReset = lastEventId > 0 &&
            ((clientEpoch is not null && !string.Equals(clientEpoch, Epoch, StringComparison.Ordinal)) ||
             lastEventId > Interlocked.Read(ref _eventSeq));
        var effectiveLastEventId = sequenceReset ? 0 : lastEventId;
        var subscriber = new SseSubscriber(effectiveLastEventId);

        // One critical section for gap check, registration and replay snapshot. Broadcast enqueues under
        // _bufferLock, so every event is either in the snapshot or published after the subscriber is
        // registered (SseSubscriber de-duplicates the overlap by Seq). Lock order is always
        // _bufferLock -> _subscriberLock; nothing takes them in the opposite order.
        GatewaySseEvent[] missed;
        lock (_bufferLock)
        {
            // A client that has never seen an event (lastEventId 0) has nothing to lose: it gets the
            // whole buffer, as before. Only a client that proved it was mid-stream can have a gap.
            if (sequenceReset)
            {
                var oldestBuffered = _eventBuffer.Count > 0 ? _eventBuffer.Min(e => e.Seq) : Interlocked.Read(ref _eventSeq) + 1;
                subscriber.NoteSequenceReset(lastEventId, oldestBuffered, Epoch);
                _logger.LogWarning(
                    "[Gateway] Session {SessionId} sequence reset: client LastEventId={LastEventId} belongs to another channel instance",
                    SessionId, lastEventId);
            }
            else if (lastEventId > 0 && _evictedThroughSeq > lastEventId)
            {
                var oldestAvailable = _eventBuffer.Count > 0 ? _eventBuffer.Min(e => e.Seq) : _evictedThroughSeq + 1;
                subscriber.NoteReplayGap(
                    lastEventId + 1, _evictedThroughSeq, oldestAvailable, GatewayReplayGapReason.BufferEvicted);
                _logger.LogWarning(
                    "[Gateway] Session {SessionId} replay gap: client LastEventId={LastEventId}, buffer evicted through {EvictedThrough}",
                    SessionId, lastEventId, _evictedThroughSeq);
            }

            lock (_subscriberLock)
            {
                _subscribers.Add(subscriber);
            }

            // 回放缓冲区中错过的事件（Seq > lastEventId）
            missed = _eventBuffer.Where(e => e.Seq > effectiveLastEventId).ToArray();
        }

        foreach (var evt in missed)
        {
            subscriber.TryWriteReplay(evt);
        }

        return subscriber;
    }

    /// <summary>移除订阅者（连接断开时调用）</summary>
    public void Unsubscribe(SseSubscriber subscriber)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(subscriber);
        }
        subscriber.Complete();
    }

    /// <summary>Current number of active SSE subscribers.</summary>
    public int SubscriberCount
    {
        get { lock (_subscriberLock) { return _subscribers.Count; } }
    }

    /// <summary>核心处理循环 — 逐条从 inbox 取消息，调用 Processor，广播 SSE 事件</summary>
    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Gateway] Session {SessionId} 处理循环启动", SessionId);

        try
        {
            await foreach (var message in _inbox.Reader.ReadAllAsync(ct))
            {
                LastActivityUtc = DateTime.UtcNow;
                FullTextAuditLogger.Log(
                    _logger,
                    "gateway_message_dequeued",
                    SessionId,
                    0,
                    0,
                    new
                    {
                        messageId = message.MessageId,
                        source = message.Source.ToString(),
                        conversationId = message.ConversationId,
                        taskId = message.TaskId,
                        subscriberCount = SubscriberCount
                    });

                // The exclusive user-chat slot is given back BEFORE the done / error event is broadcast: a client sends its next message the
                // moment it sees "done", and that message must be accepted. Released after the broadcast (and the audit line that follows
                // it), it was refused with user_chat_already_in_progress 3 ms after the previous stream closed. The next message waits in the
                // inbox until this iteration ends, so the order of events does not change. The finally block releases it too, once only.
                var slotReleased = false;
                void ReleaseSlotOnce()
                {
                    if (slotReleased) return;
                    slotReleased = true;
                    ReleaseExclusiveUserChatSlot(message);
                }

                try
                {
                    // 每条消息使用独立的 DI scope（确保 DbContext 等 scoped 服务隔离）
                    await using var scope = _serviceProvider.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IGatewayMessageProcessor>();

                    await processor.ProcessAsync(message, async sseEvent =>
                    {
                        // 分配全局递增序号
                        sseEvent = sseEvent with { Seq = Interlocked.Increment(ref _eventSeq) };

                        // The processor emits its own "done" when the model's answer is finished and goes on persisting the turn. A client
                        // takes the first done for the end of the turn, so the slot goes back no later than that event, whoever emits it.
                        if (sseEvent.Type == GatewaySseEventType.Done)
                        {
                            ReleaseSlotOnce();
                        }

                        // 广播给所有订阅者
                        await BroadcastAsync(sseEvent);
                    }, ct);

                    // 消息处理完毕后发送 done 事件
                    ReleaseSlotOnce();
                    var doneEvent = new GatewaySseEvent
                    {
                        Seq = Interlocked.Increment(ref _eventSeq),
                        Type = GatewaySseEventType.Done,
                        Payload = new { messageId = message.MessageId, source = message.Source.ToString() }
                    };
                    await BroadcastAsync(doneEvent);
                    FullTextAuditLogger.Log(
                        _logger,
                        "gateway_message_completed",
                        SessionId,
                        0,
                        0,
                        new
                        {
                            messageId = message.MessageId,
                            source = message.Source.ToString(),
                            doneEventSeq = doneEvent.Seq
                        });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Gateway] Session {SessionId} 处理消息 {MessageId} 失败",
                        SessionId, message.MessageId);
                    FullTextAuditLogger.Log(
                        _logger,
                        "gateway_message_failed",
                        SessionId,
                        0,
                        0,
                        new
                        {
                            messageId = message.MessageId,
                            source = message.Source.ToString(),
                            error = ex.Message
                        });

                    // 向订阅者推送错误事件
                    ReleaseSlotOnce();
                    var errorEvent = new GatewaySseEvent
                    {
                        Seq = Interlocked.Increment(ref _eventSeq),
                        Type = GatewaySseEventType.Error,
                        Payload = new { messageId = message.MessageId, error = ex.Message }
                    };
                    await BroadcastAsync(errorEvent);
                }
                finally
                {
                    ReleaseSlotOnce();
                    Interlocked.Decrement(ref _pendingMessageCount);
                    LastActivityUtc = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gateway] Session {SessionId} 处理循环异常退出", SessionId);
        }

        _logger.LogInformation("[Gateway] Session {SessionId} 处理循环已停止", SessionId);
    }

    private bool TryReserveExclusiveUserChatSlot(GatewayMessage message)
    {
        if (message.Source != GatewayMessageSource.UserChat)
        {
            return false;
        }

        if (Interlocked.Increment(ref _pendingUserChatCount) == 1)
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingUserChatCount);
        throw new GatewayMessageRejectedException(UserChatBusyReason, UserChatBusyMessage);
    }

    private void ReleaseExclusiveUserChatSlot(GatewayMessage message)
    {
        if (message.Source != GatewayMessageSource.UserChat)
        {
            return;
        }

        Interlocked.Decrement(ref _pendingUserChatCount);
    }

    /// <summary>广播 SSE 事件给所有活跃订阅者，并存入环形缓冲区以支持断线重连回放</summary>
    private async ValueTask BroadcastAsync(GatewaySseEvent sseEvent)
    {
        // 先存入缓冲区，确保后续订阅者可回放（包括当前无订阅者的情况）
        lock (_bufferLock)
        {
            _eventBuffer.Enqueue(sseEvent);
            if (_eventBuffer.Count > MaxBufferedEvents)
                _evictedThroughSeq = Math.Max(_evictedThroughSeq, _eventBuffer.Dequeue().Seq);
        }

        SseSubscriber[] snapshot;
        lock (_subscriberLock)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            try
            {
                await subscriber.WriteAsync(sseEvent);
            }
            catch (ChannelClosedException)
            {
                // 订阅者已断开，清理
                Unsubscribe(subscriber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Gateway] Session {SessionId} 向订阅者推送失败", SessionId);
                Unsubscribe(subscriber);
            }
        }
    }

    /// <summary>
    /// Disposes the channel, stops processing, and completes all subscribers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _inbox.Writer.TryComplete();

        if (_processorTask != null)
        {
            await Task.WhenAny(_processorTask, Task.Delay(5000));
        }

        // 关闭所有订阅者
        lock (_subscriberLock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Complete();
            _subscribers.Clear();
        }

        _cts.Dispose();
    }
}

/// <summary>
/// 单个 SSE 订阅者 — 对应一个 codex/sse HTTP 长连接
/// </summary>
public sealed class SseSubscriber
{
    private const int MaxPendingEvents = 4096;
    private readonly Channel<GatewaySseEvent> _channel;
    /// <summary>Last SSE sequence acknowledged by the subscriber when it subscribed.</summary>
    public long LastEventId { get; }

    // 已写入的最大 Seq，用于防止回放与正常广播之间的重复推送
    private long _lastWrittenSeq;

    /// <summary>
    /// Initializes an SSE subscriber with the last event identifier already seen by the client.
    /// </summary>
    /// <param name="lastEventId">Last event sequence seen by the client before subscription.</param>
    public SseSubscriber(long lastEventId = 0)
    {
        LastEventId = lastEventId;
        _lastWrittenSeq = lastEventId;
        // 有界缓冲，防止慢客户端拖垮整个通道。被挤掉的事件通过 itemDropped 记录，随后以 replay_gap 标记告知读取方。
        _channel = Channel.CreateBounded<GatewaySseEvent>(new BoundedChannelOptions(MaxPendingEvents)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false  // 回放线程 + 广播线程均可写
        }, OnItemDropped);
    }

    // Gap markers live outside the bounded channel: a marker queued behind a full replay would itself be
    // the oldest item and be dropped first.
    private readonly object _gapLock = new();
    private GatewaySseEvent? _initialGap;
    private long _overflowFromSeq;
    private long _overflowThroughSeq;
    // Only touched by the single reader.
    private GatewaySseEvent? _held;

    /// <summary>Records, before the first read, that events the client wanted are unavailable.</summary>
    internal void NoteReplayGap(long fromSeq, long throughSeq, long oldestAvailableSeq, string reason)
    {
        lock (_gapLock)
        {
            _initialGap = CreateGapMarker(fromSeq, throughSeq, oldestAvailableSeq, reason);
        }
    }

    /// <summary>Records, before the first read, that the client's position belongs to a different channel instance.</summary>
    internal void NoteSequenceReset(long previousLastEventId, long oldestAvailableSeq, string epoch)
    {
        lock (_gapLock)
        {
            _initialGap = new GatewaySseEvent
            {
                Seq = 0,
                Type = GatewaySseEventType.ReplayGap,
                Payload = new { reason = GatewayReplayGapReason.SequenceReset, epoch, previousLastEventId, oldestAvailableSeq }
            };
        }
    }

    private void OnItemDropped(GatewaySseEvent dropped)
    {
        if (dropped.Seq <= 0) return;
        lock (_gapLock)
        {
            if (_overflowFromSeq == 0 || dropped.Seq < _overflowFromSeq) _overflowFromSeq = dropped.Seq;
            if (dropped.Seq > _overflowThroughSeq) _overflowThroughSeq = dropped.Seq;
        }
    }

    private static GatewaySseEvent CreateGapMarker(long fromSeq, long throughSeq, long oldestAvailableSeq, string reason) => new()
    {
        Seq = 0,
        Type = GatewaySseEventType.ReplayGap,
        Payload = new { fromSeq, toSeq = throughSeq, oldestAvailableSeq, reason }
    };

    /// <summary>
    /// Takes the pending marker, if any. With a <paramref name="beforeSeq"/> below <see cref="long.MaxValue"/>,
    /// an overflow marker is only taken when everything it covers is older than that event; otherwise it
    /// stays queued for after it.
    /// </summary>
    private bool TryTakeMarker(long beforeSeq, out GatewaySseEvent marker)
    {
        lock (_gapLock)
        {
            if (_initialGap is not null)
            {
                marker = _initialGap;
                _initialGap = null;
                return true;
            }

            if (_overflowThroughSeq > 0 && (beforeSeq == long.MaxValue || _overflowThroughSeq < beforeSeq))
            {
                marker = CreateGapMarker(
                    _overflowFromSeq, _overflowThroughSeq, _overflowThroughSeq + 1, GatewayReplayGapReason.SubscriberOverflow);
                _overflowFromSeq = 0;
                _overflowThroughSeq = 0;
                return true;
            }
        }

        marker = null!;
        return false;
    }

    /// <summary>正常广播路径写入（异步，有背压）</summary>
    public ValueTask WriteAsync(GatewaySseEvent evt)
    {
        // 原子 CAS：只有当 evt.Seq > _lastWrittenSeq 时才写入，防止重复
        long current;
        do
        {
            current = Interlocked.Read(ref _lastWrittenSeq);
            if (evt.Seq <= current) return ValueTask.CompletedTask;
        } while (Interlocked.CompareExchange(ref _lastWrittenSeq, evt.Seq, current) != current);

        return _channel.Writer.WriteAsync(evt);
    }

    /// <summary>回放路径写入（同步 TryWrite，不阻塞调用方）</summary>
    public void TryWriteReplay(GatewaySseEvent evt)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _lastWrittenSeq);
            if (evt.Seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastWrittenSeq, evt.Seq, current) != current);

        _channel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Reads all future events for the subscriber as an async stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of gateway SSE events.</returns>
    public async IAsyncEnumerable<GatewaySseEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            GatewaySseEvent next;
            try
            {
                next = await ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                yield break;
            }

            yield return next;
        }
    }

    /// <summary>
    /// Reads the next event for the subscriber.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next gateway SSE event.</returns>
    public async ValueTask<GatewaySseEvent> ReadAsync(CancellationToken ct)
    {
        if (_held is { } held)
        {
            _held = null;
            return held;
        }

        if (TryTakeMarker(long.MaxValue, out var marker)) return marker;

        var next = await _channel.Reader.ReadAsync(ct);

        // Events dropped between the check above and this read are older than `next`: report them first.
        if (TryTakeMarker(next.Seq, out var late))
        {
            _held = next;
            return late;
        }

        return next;
    }

    /// <summary>
    /// Completes the subscriber channel.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
