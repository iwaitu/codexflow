// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Browser.Cdp;

internal sealed class BrowserChildFrameRecord
{
    public required string TargetId { get; init; }
    public required string CdpSessionId { get; init; }
    public string Url { get; set; } = string.Empty;
    public int? IsolatedWorldContextId { get; set; }
}

internal sealed class BrowserTabRecord
{
    public required string TargetId { get; init; }
    public required string CdpSessionId { get; init; }
    public string Url { get; set; } = "about:blank";
    public string Title { get; set; } = string.Empty;
    public int? IsolatedWorldContextId { get; set; }
    public Dictionary<string, CdpSnapshotElement> ElementRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BrowserChildFrameRecord> ChildFrames { get; } = [];
    public List<CdpBoundingBox> LastSecretRects { get; } = [];
}

/// <summary>
/// Per-session isolated Chromium BrowserContext state, including mutable WorkMode tracking,
/// session-approved loopback ports, temporary single-use loopback port leases, tab-scoped refs,
/// console/action buffers, and concurrency-safe disposal.
/// </summary>
internal sealed class CdpBrowserSessionState : IAsyncDisposable
{
    private const int MaxActions = 50;
    private const int MaxConsoleEntries = 100;
    private const int MaxNetworkErrors = 50;

    private readonly object _stateLock = new();
    private readonly List<BrowserTabRecord> _tabs = [];
    private readonly Queue<BrowserActionRecordDto> _actions = new();
    private readonly Queue<BrowserConsoleEntryDto> _console = new();
    private readonly Queue<string> _networkErrors = new();
    private readonly HashSet<int> _sessionApprovedLoopbackPorts = [];
    private readonly HashSet<int> _oneTimeLoopbackPorts = [];

    private SessionWorkMode _workMode;
    private long _actionSeq;
    private long _screenshotSeq;
    private BrowserScreenshotData? _latestScreenshot;
    private int _consoleErrorCount;
    private long _lastPublishedTicks;
    private volatile bool _isClosed;
    private int _disposed;
    private bool _takeoverActive;
    private bool _isManualTakeover;
    private string? _takeoverReason;
    private DateTimeOffset? _takeoverStartedAtUtc;
    private TaskCompletionSource<string?>? _takeoverTcs;

    public string SessionId { get; }
    public string BrowserContextId { get; }
    public bool IsRemote { get; }
    public bool Headless { get; }
    public BrowserProfileMode ProfileMode { get; }
    public bool UsesDefaultContext { get; }
    public string? WorkspaceRoot { get; }
    public string StagingDirectory { get; }
    public string GenerationId { get; } = Guid.NewGuid().ToString("N")[..8];
    public BrowserOutboundProxy? OutboundProxy { get; }
    public SemaphoreSlim ExecutionLock { get; } = new(1, 1);
    public string? ActiveTargetId { get; private set; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool IsClosed => _isClosed;

    public bool TakeoverActive
    {
        get
        {
            lock (_stateLock)
            {
                return _takeoverActive;
            }
        }
    }

    public bool IsManualTakeover
    {
        get
        {
            lock (_stateLock)
            {
                return _takeoverActive && _isManualTakeover;
            }
        }
    }

    public DateTimeOffset? TakeoverStartedAtUtc
    {
        get
        {
            lock (_stateLock)
            {
                return _takeoverStartedAtUtc;
            }
        }
    }

    public SessionWorkMode WorkMode
    {
        get
        {
            lock (_stateLock)
            {
                return _workMode;
            }
        }
    }

    public CdpBrowserSessionState(
        string sessionId,
        string browserContextId,
        SessionWorkMode workMode,
        BrowserOutboundProxy? outboundProxy,
        bool headless = true,
        BrowserProfileMode profileMode = BrowserProfileMode.Ephemeral,
        bool usesDefaultContext = false,
        string? workspaceRoot = null,
        string? stagingDirectory = null,
        bool isRemote = false)
    {
        SessionId = sessionId;
        BrowserContextId = browserContextId;
        _workMode = workMode;
        OutboundProxy = outboundProxy;
        IsRemote = isRemote;
        Headless = headless;
        ProfileMode = profileMode;
        UsesDefaultContext = usesDefaultContext;
        WorkspaceRoot = workspaceRoot;
        StagingDirectory = string.IsNullOrWhiteSpace(stagingDirectory)
            ? Path.Combine(Path.GetTempPath(), "cf-browser-staging", $"{sessionId}-{GenerationId}")
            : stagingDirectory;
    }

    public Task<string?> BeginTakeover(string reason, bool manual = false)
    {
        lock (_stateLock)
        {
            _takeoverActive = true;
            _isManualTakeover = manual;
            _takeoverReason = string.IsNullOrWhiteSpace(reason) ? "人工接管浏览器会话" : reason.Trim();
            _takeoverStartedAtUtc = DateTimeOffset.UtcNow;
            _takeoverTcs ??= new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdatedAtUtc = DateTimeOffset.UtcNow;
            LastActivityUtc = DateTimeOffset.UtcNow;
            return _takeoverTcs.Task;
        }
    }

    public bool CompleteTakeover(string? note)
    {
        TaskCompletionSource<string?>? tcs;
        lock (_stateLock)
        {
            if (!_takeoverActive && _takeoverTcs is null)
            {
                return false;
            }

            _takeoverActive = false;
            _isManualTakeover = false;
            _takeoverReason = null;
            _takeoverStartedAtUtc = null;
            tcs = _takeoverTcs;
            _takeoverTcs = null;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
            LastActivityUtc = DateTimeOffset.UtcNow;
        }

        tcs?.TrySetResult(note);
        return true;
    }

    public void CancelTakeover(string reason)
    {
        TaskCompletionSource<string?>? tcs;
        lock (_stateLock)
        {
            _takeoverActive = false;
            _isManualTakeover = false;
            _takeoverReason = null;
            _takeoverStartedAtUtc = null;
            tcs = _takeoverTcs;
            _takeoverTcs = null;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        tcs?.TrySetCanceled();
    }

    public void AbortTakeover(string reason)
    {
        TaskCompletionSource<string?>? tcs;
        lock (_stateLock)
        {
            if (!_takeoverActive && _takeoverTcs is null)
            {
                return;
            }

            _takeoverActive = false;
            _isManualTakeover = false;
            _takeoverReason = null;
            _takeoverStartedAtUtc = null;
            tcs = _takeoverTcs;
            _takeoverTcs = null;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        tcs?.TrySetException(new InvalidOperationException(reason));
    }

    public IReadOnlyList<BrowserTabRecord> GetTabsSnapshot()
    {
        lock (_stateLock)
        {
            return _tabs.ToArray();
        }
    }

    public bool HasTab(string targetId)
    {
        lock (_stateLock)
        {
            return _tabs.Any(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
        }
    }

    public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void MarkClosed() => _isClosed = true;

    public void SyncWorkMode(SessionWorkMode latestWorkMode)
    {
        lock (_stateLock)
        {
            if (_workMode != latestWorkMode)
            {
                _workMode = latestWorkMode;
                // Mode changed: revoke previously approved loopback ports so Dev->Daily tightening takes immediate effect.
                _sessionApprovedLoopbackPorts.Clear();
                _oneTimeLoopbackPorts.Clear();
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    public IReadOnlySet<int> GetApprovedLoopbackPortsSnapshot()
    {
        lock (_stateLock)
        {
            var merged = new HashSet<int>(_sessionApprovedLoopbackPorts);
            merged.UnionWith(_oneTimeLoopbackPorts);
            return merged;
        }
    }

    public void ApproveLoopbackPortForSession(int port)
    {
        if (port is <= 0 or > 65535) return;
        lock (_stateLock)
        {
            _sessionApprovedLoopbackPorts.Add(port);
        }
    }

    public void GrantOneTimeLoopbackPort(int port)
    {
        if (port is <= 0 or > 65535) return;
        lock (_stateLock)
        {
            _oneTimeLoopbackPorts.Add(port);
        }
    }

    public void RevokeOneTimeLoopbackPort(int port)
    {
        lock (_stateLock)
        {
            _oneTimeLoopbackPorts.Remove(port);
        }
    }

    public void AddOrUpdateTab(BrowserTabRecord tab, bool makeActive)
    {
        lock (_stateLock)
        {
            var existing = _tabs.FindIndex(t => string.Equals(t.TargetId, tab.TargetId, StringComparison.Ordinal));
            if (existing >= 0)
            {
                _tabs[existing] = tab;
            }
            else
            {
                _tabs.Add(tab);
            }

            if (makeActive || ActiveTargetId is null)
            {
                ActiveTargetId = tab.TargetId;
            }

            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public BrowserTabRecord? GetActiveTab()
    {
        lock (_stateLock)
        {
            if (ActiveTargetId is not null)
            {
                var active = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, ActiveTargetId, StringComparison.Ordinal));
                if (active is not null)
                {
                    return active;
                }
            }

            return _tabs.FirstOrDefault();
        }
    }

    public BrowserTabRecord? FindTabByCdpSessionId(string cdpSessionId)
    {
        lock (_stateLock)
        {
            return _tabs.FirstOrDefault(t => string.Equals(t.CdpSessionId, cdpSessionId, StringComparison.Ordinal));
        }
    }

    public BrowserChildFrameRecord? FindChildFrameByCdpSessionId(string cdpSessionId)
    {
        lock (_stateLock)
        {
            foreach (var tab in _tabs)
            {
                var child = tab.ChildFrames.FirstOrDefault(c => string.Equals(c.CdpSessionId, cdpSessionId, StringComparison.Ordinal));
                if (child is not null)
                {
                    return child;
                }
            }

            return null;
        }
    }

    public void AddOrUpdateChildFrame(string parentTargetId, BrowserChildFrameRecord frame)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, parentTargetId, StringComparison.Ordinal));
            if (tab is null)
            {
                return;
            }

            var existing = tab.ChildFrames.FindIndex(c => string.Equals(c.CdpSessionId, frame.CdpSessionId, StringComparison.Ordinal));
            if (existing >= 0)
            {
                tab.ChildFrames[existing] = frame;
            }
            else
            {
                tab.ChildFrames.Add(frame);
            }
        }
    }

    public IReadOnlyList<BrowserChildFrameRecord> GetChildFramesSnapshot(string parentTargetId)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, parentTargetId, StringComparison.Ordinal));
            return tab is null ? [] : tab.ChildFrames.ToArray();
        }
    }

    public void RemoveChildFrameByCdpSessionId(string cdpSessionId)
    {
        lock (_stateLock)
        {
            foreach (var tab in _tabs)
            {
                tab.ChildFrames.RemoveAll(c => string.Equals(c.CdpSessionId, cdpSessionId, StringComparison.Ordinal));
            }
        }
    }

    public void RemoveChildFrameByTargetId(string targetId)
    {
        lock (_stateLock)
        {
            foreach (var tab in _tabs)
            {
                tab.ChildFrames.RemoveAll(c => string.Equals(c.TargetId, targetId, StringComparison.Ordinal));
            }
        }
    }

    public void UpdateChildFrameUrl(string cdpSessionId, string? url)
    {
        lock (_stateLock)
        {
            foreach (var tab in _tabs)
            {
                var child = tab.ChildFrames.FirstOrDefault(c => string.Equals(c.CdpSessionId, cdpSessionId, StringComparison.Ordinal));
                if (child is not null)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        child.Url = url;
                    }

                    child.IsolatedWorldContextId = null;
                    return;
                }
            }
        }
    }

    public void ReplaceTabSecretRects(string targetId, IEnumerable<CdpBoundingBox>? rects)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is null)
            {
                return;
            }

            tab.LastSecretRects.Clear();
            if (rects is not null)
            {
                tab.LastSecretRects.AddRange(rects);
            }
        }
    }

    public IReadOnlyList<CdpBoundingBox> GetTabSecretRectsSnapshot(string targetId)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            return tab is null ? [] : tab.LastSecretRects.ToArray();
        }
    }

    public bool TrySetActiveTab(string targetId, out BrowserTabRecord? tab)
    {
        lock (_stateLock)
        {
            tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is null)
            {
                return false;
            }

            ActiveTargetId = tab.TargetId;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool RemoveTab(string targetId, out BrowserTabRecord? removed)
    {
        lock (_stateLock)
        {
            var idx = _tabs.FindIndex(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (idx < 0)
            {
                removed = null;
                return false;
            }

            removed = _tabs[idx];
            _tabs.RemoveAt(idx);
            if (string.Equals(ActiveTargetId, targetId, StringComparison.Ordinal))
            {
                ActiveTargetId = _tabs.FirstOrDefault()?.TargetId;
            }

            UpdatedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void UpdateTabMetadata(string targetId, string? url, string? title, bool invalidateIsolatedWorld = false)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                tab.Url = url;
            }

            if (title is not null)
            {
                tab.Title = title;
            }

            if (invalidateIsolatedWorld)
            {
                tab.IsolatedWorldContextId = null;
                tab.ElementRefs.Clear();
                tab.LastSecretRects.Clear();
                foreach (var child in tab.ChildFrames)
                {
                    child.IsolatedWorldContextId = null;
                }
            }

            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void ClearTabElementRefs(string targetId)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is not null)
            {
                tab.ElementRefs.Clear();
                tab.LastSecretRects.Clear();
                tab.IsolatedWorldContextId = null;
                foreach (var child in tab.ChildFrames)
                {
                    child.IsolatedWorldContextId = null;
                }
            }
        }
    }

    public void ReplaceTabElementRefs(string targetId, IEnumerable<CdpSnapshotElement>? elements)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is null)
            {
                return;
            }

            tab.ElementRefs.Clear();
            if (elements is null)
            {
                return;
            }

            foreach (var el in elements)
            {
                if (!string.IsNullOrWhiteSpace(el.Ref))
                {
                    tab.ElementRefs[el.Ref] = el;
                }
            }
        }
    }

    public bool TryGetTabElementRef(string targetId, string refId, out CdpSnapshotElement? element)
    {
        lock (_stateLock)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, targetId, StringComparison.Ordinal));
            if (tab is null)
            {
                element = null;
                return false;
            }

            return tab.ElementRefs.TryGetValue(refId, out element);
        }
    }

    public BrowserScreenshotData RecordScreenshot(byte[] jpegBytes)
    {
        lock (_stateLock)
        {
            var seq = ++_screenshotSeq;
            var data = new BrowserScreenshotData(seq, "image/jpeg", jpegBytes, DateTimeOffset.UtcNow);
            _latestScreenshot = data;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
            return data;
        }
    }

    public bool TryGetLatestScreenshot(out BrowserScreenshotData? screenshot)
    {
        lock (_stateLock)
        {
            screenshot = _latestScreenshot;
            return screenshot is not null;
        }
    }

    public void RecordAction(string tool, string action, string? target, string? url, bool succeeded, string? summary, long durationMs)
    {
        lock (_stateLock)
        {
            var entry = new BrowserActionRecordDto(
                Seq: ++_actionSeq,
                Tool: tool,
                Action: action,
                Target: target,
                Url: url,
                Succeeded: succeeded,
                Summary: summary,
                DurationMs: durationMs,
                TimestampUtc: DateTimeOffset.UtcNow);

            _actions.Enqueue(entry);
            while (_actions.Count > MaxActions)
            {
                _actions.Dequeue();
            }

            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordConsoleEntry(string level, string text, string? sourceUrl = null, int? lineNumber = null)
    {
        lock (_stateLock)
        {
            var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "log" : level.Trim().ToLowerInvariant();
            if (normalizedLevel is "error" or "exception")
            {
                _consoleErrorCount++;
            }

            _console.Enqueue(new BrowserConsoleEntryDto(
                Level: normalizedLevel,
                Text: text.Length > 1000 ? text[..1000] + "…" : text,
                SourceUrl: sourceUrl,
                LineNumber: lineNumber,
                TimestampUtc: DateTimeOffset.UtcNow));

            while (_console.Count > MaxConsoleEntries)
            {
                _console.Dequeue();
            }

            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordNetworkError(string message)
    {
        lock (_stateLock)
        {
            _networkErrors.Enqueue(message.Length > 400 ? message[..400] + "…" : message);
            while (_networkErrors.Count > MaxNetworkErrors)
            {
                _networkErrors.Dequeue();
            }
        }
    }

    public IReadOnlyList<BrowserConsoleEntryDto> GetRecentConsoleEntries()
    {
        lock (_stateLock)
        {
            return _console.ToArray();
        }
    }

    public IReadOnlyList<string> GetRecentNetworkErrors()
    {
        lock (_stateLock)
        {
            return _networkErrors.ToArray();
        }
    }

    public bool ShouldPublishThrottled(bool force, TimeSpan minInterval)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (force)
        {
            Interlocked.Exchange(ref _lastPublishedTicks, nowTicks);
            return true;
        }

        var prev = Interlocked.Read(ref _lastPublishedTicks);
        if (nowTicks - prev < minInterval.Ticks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastPublishedTicks, nowTicks, prev) == prev;
    }

    public BrowserSessionStateDto ToDto(string status = "running", string? profileMode = null)
    {
        lock (_stateLock)
        {
            var active = _tabs.FirstOrDefault(t => string.Equals(t.TargetId, ActiveTargetId, StringComparison.Ordinal))
                         ?? _tabs.FirstOrDefault();
            var effectiveStatus = _takeoverActive && string.Equals(status, "running", StringComparison.Ordinal)
                ? "takeover"
                : status;

            return new BrowserSessionStateDto(
                SessionId: SessionId,
                Status: effectiveStatus,
                Headless: Headless,
                ProfileMode: profileMode ?? ProfileMode.ToString(),
                Url: active?.Url,
                Title: active?.Title,
                ScreenshotSeq: _screenshotSeq,
                ConsoleErrorCount: _consoleErrorCount,
                Tabs: _tabs.Select(t => new BrowserTabDto(
                    t.TargetId,
                    t.Url,
                    t.Title,
                    string.Equals(t.TargetId, active?.TargetId, StringComparison.Ordinal))).ToArray(),
                RecentActions: _actions.ToArray(),
                RecentConsole: _console.TakeLast(40).ToArray(),
                UpdatedAtUtc: UpdatedAtUtc,
                TakeoverActive: _takeoverActive,
                TakeoverReason: _takeoverReason,
                TakeoverStartedAtUtc: _takeoverStartedAtUtc,
                IsRemote: IsRemote);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _isClosed = true;
        AbortTakeover("session_disposed");
        if (OutboundProxy is not null)
        {
            await OutboundProxy.DisposeAsync().ConfigureAwait(false);
        }
        BrowserWorkspacePathGuard.TryDeleteDirectory(StagingDirectory);
        // Note: Do NOT eagerly Dispose ExecutionLock here while an in-flight caller may still be releasing it in finally;
        // GC reclaims the managed SemaphoreSlim safely without throwing ObjectDisposedException on in-flight Release().
    }
}
