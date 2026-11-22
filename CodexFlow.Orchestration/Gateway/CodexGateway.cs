// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodexFlow.Core.Gateway;
using CodexFlow.Logging;

namespace CodexFlow.Gateway;

/// <summary>
/// Singleton gateway that manages per-session message channels and SSE subscriptions.
/// </summary>
public sealed class CodexGateway : ICodexGateway, IAsyncDisposable
{
    private static readonly TimeSpan DefaultSseHeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, SessionChannel> _channels = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CodexGateway> _logger;
    private readonly TimeSpan _sseHeartbeatInterval;

    // 空闲 Session 自动清理定时器
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes the gateway.
    /// </summary>
    /// <param name="serviceProvider">Service provider used to create per-session processing scopes.</param>
    /// <param name="loggerFactory">Logger factory used when constructing session channels.</param>
    /// <param name="logger">Logger used for gateway lifecycle diagnostics.</param>
    public CodexGateway(
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        ILogger<CodexGateway> logger)
        : this(serviceProvider, loggerFactory, logger, DefaultSseHeartbeatInterval)
    {
    }

    /// <summary>
    /// Initializes the gateway with a custom SSE heartbeat interval.
    /// </summary>
    /// <param name="serviceProvider">Service provider used to create per-session processing scopes.</param>
    /// <param name="loggerFactory">Logger factory used when constructing session channels.</param>
    /// <param name="logger">Logger used for gateway lifecycle diagnostics.</param>
    /// <param name="sseHeartbeatInterval">Interval used for SSE keep-alive heartbeats.</param>
    public CodexGateway(
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        ILogger<CodexGateway> logger,
        TimeSpan sseHeartbeatInterval)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _sseHeartbeatInterval = sseHeartbeatInterval <= TimeSpan.Zero
            ? DefaultSseHeartbeatInterval
            : sseHeartbeatInterval;

        // 每 5 分钟清理空闲 Session
        _cleanupTimer = new Timer(_ => _ = CleanupIdleSessionsAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Enqueues a gateway message into the session channel.
    /// </summary>
    /// <param name="message">Gateway message to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message identifier assigned to the queued message.</returns>
    public async ValueTask<string> EnqueueAsync(GatewayMessage message, CancellationToken ct = default)
    {
        var channel = GetOrCreateChannel(message.SessionId);
        await channel.EnqueueAsync(message, ct);

        _logger.LogInformation(
            "[Gateway] 消息入队: Session={SessionId}, Source={Source}, MessageId={MessageId}",
            message.SessionId, message.Source, message.MessageId);

        return message.MessageId;
    }

    /// <summary>
    /// Subscribes a client to the session's SSE stream, replaying buffered events after the supplied sequence number.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="userId">Current user identifier for audit logging.</param>
    /// <param name="lastEventId">Last SSE sequence seen by the client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of gateway SSE events.</returns>
    public async IAsyncEnumerable<GatewaySseEvent> SubscribeAsync(
        string sessionId,
        string userId,
        long lastEventId = 0,
        string? channelEpoch = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = GetOrCreateChannel(sessionId);
        var subscriber = channel.Subscribe(lastEventId, channelEpoch);

        _logger.LogInformation(
            "[Gateway] SSE 订阅: Session={SessionId}, User={UserId}, LastEventId={LastEventId}, 当前订阅者数={Count}",
            sessionId, userId, lastEventId, channel.SubscriberCount);
        FullTextAuditLogger.Log(
            _logger,
            "gateway_subscribe_started",
            sessionId,
            0,
            0,
            FullTextAuditLogger.Fields(
                ("userId", userId),
                ("lastEventId", lastEventId),
                ("subscriberCount", channel.SubscriberCount)));

        try
        {
            var approvalBroker = _serviceProvider.GetService<CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker>();
            var browserApprovalCoordinator = _serviceProvider.GetService<CodexFlow.Core.Browser.IBrowserApprovalCoordinator>();
            var computerApprovalCoordinator = _serviceProvider.GetService<CodexFlow.Core.ComputerUse.IComputerApprovalCoordinator>();
            int GetTotalPendingApprovals() =>
                (approvalBroker?.GetPendingApprovalCount(sessionId) ?? 0) +
                (browserApprovalCoordinator?.GetPendingApprovalsForSession(sessionId).Count ?? 0) +
                (computerApprovalCoordinator?.GetPendingApprovalsForSession(sessionId).Count ?? 0);

            // 发送初始连接确认事件
            yield return new GatewaySseEvent
            {
                Seq = 0,
                Type = GatewaySseEventType.Heartbeat,
                Payload = new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["status"] = "connected",
                    ["subscriberCount"] = channel.SubscriberCount,
                    ["epoch"] = channel.Epoch,
                    ["pendingApprovals"] = GetTotalPendingApprovals()
                }
            };

            Task<GatewaySseEvent>? subscriberReadTask = null;
            while (!ct.IsCancellationRequested)
            {
                subscriberReadTask ??= subscriber.ReadAsync(ct).AsTask();
                var heartbeatTickTask = Task.Delay(_sseHeartbeatInterval, ct);
                var completed = await Task.WhenAny(subscriberReadTask, heartbeatTickTask);

                if (completed == subscriberReadTask)
                {
                    yield return await subscriberReadTask;
                    subscriberReadTask = null;
                    continue;
                }

                yield return new GatewaySseEvent
                {
                    Seq = 0,
                    Type = GatewaySseEventType.Heartbeat,
                    Payload = new Dictionary<string, object?>
                    {
                        ["sessionId"] = sessionId,
                        ["status"] = "alive",
                        ["subscriberCount"] = channel.SubscriberCount,
                        ["intervalSeconds"] = (int)_sseHeartbeatInterval.TotalSeconds,
                        ["pendingApprovals"] = GetTotalPendingApprovals()
                    }
                };
            }
        }
        finally
        {
            channel.Unsubscribe(subscriber);
            _logger.LogInformation("[Gateway] SSE 断开: Session={SessionId}, User={UserId}", sessionId, userId);
            FullTextAuditLogger.Log(
                _logger,
                "gateway_subscribe_ended",
                sessionId,
                0,
                0,
                FullTextAuditLogger.Fields(
                    ("userId", userId),
                    ("lastEventId", lastEventId),
                    ("subscriberCount", channel.SubscriberCount)));
        }
    }

    /// <summary>
    /// Publishes an externally produced SSE event into the target session channel.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="sseEvent">Event payload to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask PublishEventAsync(string sessionId, GatewaySseEvent sseEvent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var channel = GetOrCreateChannel(sessionId);
        await channel.PublishExternalEventAsync(sseEvent, ct);
    }

    /// <summary>
    /// Determines whether the session currently has queued or in-flight work.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns><see langword="true"/> when the session channel is active; otherwise <see langword="false"/>.</returns>
    public bool IsSessionActive(string sessionId)
        => _channels.TryGetValue(sessionId, out var ch) && ch.HasPendingMessages;

    /// <summary>
    /// Determines whether the session currently has active SSE subscribers.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns><see langword="true"/> when the session has one or more subscribers; otherwise <see langword="false"/>.</returns>
    public bool HasActiveSubscribers(string sessionId)
        => _channels.TryGetValue(sessionId, out var ch) && ch.SubscriberCount > 0;

    /// <summary>
    /// Closes a session channel and disposes its resources.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    public async ValueTask CloseSessionAsync(string sessionId)
    {
        _serviceProvider.GetService<CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker>()
            ?.ClearSessionAuthorizations(sessionId);

        if (_serviceProvider.GetService<CodexFlow.Core.Browser.IBrowserSessionManager>() is { } browserManager)
        {
            try
            {
                await browserManager.CloseSessionAsync(sessionId, "session_closed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Gateway] 关闭 Session {SessionId} 关联浏览器上下文失败，继续释放通道。", sessionId);
            }
        }

        if (_serviceProvider.GetService<CodexFlow.Core.ComputerUse.IComputerSessionManager>() is { } computerManager)
        {
            try
            {
                await computerManager.CloseSessionAsync(sessionId, "session_closed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Gateway] 关闭 Session {SessionId} 关联桌面 Computer Use 会话失败，继续释放通道。", sessionId);
            }
        }

        if (_channels.TryRemove(sessionId, out var channel))
        {
            await channel.DisposeAsync();
            _logger.LogInformation("[Gateway] Session {SessionId} 已关闭", sessionId);
        }
    }

    public Task<bool> CancelTurnAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ct.ThrowIfCancellationRequested();

        if (!_channels.TryGetValue(sessionId, out var channel))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(channel.CancelActiveTurn());
    }

    public Task<int> CancelActiveRemoteTurnsAsync(
        long? minAccountRevocationSeq,
        long? minDeviceRevocationSeq,
        IReadOnlyList<Guid>? revokedPairingIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cancelledCount = 0;

        foreach (var channel in _channels.Values)
        {
            if (channel.CancelActiveTurnIfRevoked(stamp =>
                    (minAccountRevocationSeq.HasValue && stamp.AccountRevocationSeq < minAccountRevocationSeq.Value) ||
                    (minDeviceRevocationSeq.HasValue && stamp.DeviceRevocationSeq < minDeviceRevocationSeq.Value) ||
                    (revokedPairingIds is not null && revokedPairingIds.Contains(stamp.PairingId))))
            {
                cancelledCount++;
            }
        }

        return Task.FromResult(cancelledCount);
    }

    public GatewayChannelSnapshotState? GetChannelSnapshotState(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _channels.TryGetValue(sessionId, out var channel)
            ? channel.GetSnapshotState()
            : null;

    private SessionChannel GetOrCreateChannel(string sessionId)
    {
        return _channels.GetOrAdd(sessionId, sid =>
        {
            var channelLogger = _loggerFactory.CreateLogger<SessionChannel>();
            var channel = new SessionChannel(sid, _serviceProvider, channelLogger);
            channel.Start();
            _logger.LogInformation("[Gateway] 创建 Session 通道: {SessionId}", sid);
            FullTextAuditLogger.Log(
                _logger,
                "gateway_channel_created",
                sid,
                0,
                0,
                FullTextAuditLogger.Fields());
            return channel;
        });
    }

    private async Task CleanupIdleSessionsAsync()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        var idleSessions = _channels
            .Where(kv => kv.Value.LastActivityUtc < cutoff && kv.Value.SubscriberCount == 0)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var sessionId in idleSessions)
        {
            await CloseSessionAsync(sessionId);
            _logger.LogInformation("[Gateway] 清理空闲 Session: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Disposes the gateway and all active session channels.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cleanupTimer.Dispose();

        foreach (var kv in _channels)
        {
            await kv.Value.DisposeAsync();
        }
        _channels.Clear();
    }
}
