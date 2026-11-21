// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using CodexFlow.Contracts.ComputerUse;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.ComputerUse;

public sealed record ComputerPointSafetyResult(
    bool Allowed,
    bool HitProtectedSelfUi,
    bool HitBlockedProcess,
    ComputerWindowInfoDto? Window,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ComputerPointSafetyResult Pass(ComputerWindowInfoDto? window)
        => new(true, false, false, window, null, null);

    public static ComputerPointSafetyResult SelfUiBlocked(ComputerWindowInfoDto? window)
        => new(
            false,
            true,
            false,
            window,
            "self_ui_interaction_forbidden",
            "安全策略严禁 Computer Use 交互或点击 CodexFlow 自身控制台窗口（防视觉自批红线）。");

    public static ComputerPointSafetyResult BlockedProcess(ComputerWindowInfoDto window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new(
            false,
            false,
            true,
            window,
            "blocked_sensitive_application",
            $"安全策略禁止操控受保护或敏感应用程序（{window.ProcessName}）。");
    }
}

/// <summary>
/// Enforces CodexFlow's Computer Use safety boundaries:
/// 1) Self-UI exclusion zone (host PID + registered Electron client PIDs/HWNDs + process tree);
/// 2) +-4px safety margin point sampling around click targets;
/// 3) Pending-approval input freeze across all subsystems;
/// 4) Sensitive process blacklist and dangerous OS hotkey interception;
/// 5) Post-approval window identity TOCTOU verification.
/// </summary>
public sealed class ComputerSecurityGuard
{
    private static readonly (int Dx, int Dy)[] MarginOffsets =
    [
        (0, 0),
        (-4, 0),
        (4, 0),
        (0, -4),
        (0, 4)
    ];

    private readonly IOptionsMonitor<ComputerOptions> _options;
    private readonly IComputerDriver _driver;
    private readonly IComputerSessionApprovalFreezeProbe? _freezeProbe;
    private readonly ConcurrentDictionary<int, byte> _protectedPids = new();
    private readonly ConcurrentDictionary<string, byte> _protectedHandles = new(StringComparer.OrdinalIgnoreCase);

    public ComputerSecurityGuard(
        IOptionsMonitor<ComputerOptions> options,
        IComputerDriver driver,
        IComputerSessionApprovalFreezeProbe? freezeProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _freezeProbe = freezeProbe;
        _protectedPids[Environment.ProcessId] = 0;

        // Zero-window startup protection: trace immediate parent chain so Electron host window is protected
        // even before control-plane registers HWNDs explicitly.
        var cursorPid = Environment.ProcessId;
        for (var depth = 0; depth < 3; depth++)
        {
            if (!_driver.TryGetParentProcess(cursorPid, out var parentPid, out var parentName) || parentPid <= 0)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(parentName) &&
                (parentName.Contains("electron", StringComparison.OrdinalIgnoreCase) ||
                 parentName.Contains("codexflow", StringComparison.OrdinalIgnoreCase)))
            {
                _protectedPids[parentPid] = 0;
            }

            cursorPid = parentPid;
        }
    }

    public RegisterProtectedWindowsResponse RegisterProtectedWindows(
        int processId,
        IReadOnlyList<string>? windowHandles)
    {
        if (processId > 0)
        {
            _protectedPids[processId] = 0;
        }

        if (windowHandles is not null)
        {
            foreach (var handle in windowHandles)
            {
                if (!string.IsNullOrWhiteSpace(handle))
                {
                    _protectedHandles[handle.Trim()] = 0;
                }
            }
        }

        return new RegisterProtectedWindowsResponse(
            RegisteredProcessCount: _protectedPids.Count,
            RegisteredWindowCount: _protectedHandles.Count);
    }

    public IReadOnlySet<int> ProtectedPidsSnapshot
        => new HashSet<int>(_protectedPids.Keys);

    public IReadOnlySet<string> ProtectedHandlesSnapshot
        => new HashSet<string>(_protectedHandles.Keys, StringComparer.OrdinalIgnoreCase);

    public bool HasAnyPendingApprovalFreeze(string sessionId)
        => _freezeProbe is not null && _freezeProbe.HasAnyPendingApproval(sessionId);

    public ComputerPointSafetyResult EvaluatePointTarget(int x, int y)
    {
        var pids = ProtectedPidsSnapshot;
        var handles = ProtectedHandlesSnapshot;
        ComputerWindowInfoDto? centerWindow = null;

        foreach (var (dx, dy) in MarginOffsets)
        {
            var win = _driver.GetWindowAtPoint(x + dx, y + dy, pids, handles);
            if (dx == 0 && dy == 0)
            {
                centerWindow = win;
            }

            if (win is null)
            {
                continue;
            }

            if (IsProtectedWindow(win, pids, handles))
            {
                return ComputerPointSafetyResult.SelfUiBlocked(win);
            }

            if (IsBlockedProcessName(win.ProcessName))
            {
                return ComputerPointSafetyResult.BlockedProcess(win);
            }
        }

        return ComputerPointSafetyResult.Pass(centerWindow);
    }

    public ComputerPointSafetyResult EvaluateForegroundTarget()
    {
        var pids = ProtectedPidsSnapshot;
        var handles = ProtectedHandlesSnapshot;
        var fg = _driver.GetForegroundWindow(pids, handles);
        if (fg is null)
        {
            return ComputerPointSafetyResult.Pass(null);
        }

        if (IsProtectedWindow(fg, pids, handles))
        {
            return ComputerPointSafetyResult.SelfUiBlocked(fg);
        }

        if (IsBlockedProcessName(fg.ProcessName))
        {
            return ComputerPointSafetyResult.BlockedProcess(fg);
        }

        return ComputerPointSafetyResult.Pass(fg);
    }

    public bool IsWindowStillExpected(
        ComputerWindowInfoDto? expectedSnapshot,
        ComputerWindowInfoDto? liveWindow,
        out string? mismatchReason)
    {
        mismatchReason = null;
        if (expectedSnapshot is null)
        {
            return true;
        }

        if (liveWindow is null)
        {
            mismatchReason = $"执行前目标窗口（{expectedSnapshot.ProcessName}）已关闭或不可见。";
            return false;
        }

        var handleMatches = string.Equals(expectedSnapshot.WindowHandle, liveWindow.WindowHandle, StringComparison.OrdinalIgnoreCase);
        var processMatches = string.Equals(expectedSnapshot.ProcessName, liveWindow.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (!handleMatches || !processMatches)
        {
            mismatchReason =
                $"安全策略拦截 TOCTOU 窗口漂移：审批时目标为 {expectedSnapshot.ProcessName} ({expectedSnapshot.WindowHandle})，" +
                $"当前变更为 {liveWindow.ProcessName} ({liveWindow.WindowHandle})。请重新截屏确认后再操作。";
            return false;
        }

        return true;
    }

    public bool IsDangerousKeyCombo(IReadOnlyList<string> keys, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(keys);
        reason = null;
        if (keys.Count == 0)
        {
            reason = "快捷键组合不能为空。";
            return true;
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in keys)
        {
            var token = NormalizeKeyToken(raw);
            if (token.Length > 0)
            {
                normalized.Add(token);
            }
        }

        // Block OS security/lock/switch shortcuts on Windows & macOS to prevent lockouts or bypassing self-UI boundaries.
        var hasCtrl = normalized.Contains("ctrl");
        var hasAlt = normalized.Contains("alt");
        var hasShift = normalized.Contains("shift");
        var hasWinOrCmd = normalized.Contains("meta");

        if (hasCtrl && hasAlt && (normalized.Contains("delete") || normalized.Contains("del")))
        {
            reason = "安全策略禁止发送 Ctrl+Alt+Del 系统安全注意力热键。";
            return true;
        }

        if (hasCtrl && hasShift && normalized.Contains("esc"))
        {
            reason = "安全策略禁止发送 Ctrl+Shift+Esc。";
            return true;
        }

        if (hasWinOrCmd && (normalized.Contains("l") || normalized.Contains("r") || normalized.Contains("x") || normalized.Contains("d")))
        {
            if (OperatingSystem.IsWindows() || (hasCtrl && normalized.Contains("q")))
            {
                reason = "安全策略禁止发送系统级锁定、运行框或桌面切换快捷键（如 Win+R / Win+L / Ctrl+Cmd+Q）。";
                return true;
            }
        }

        if (hasCtrl && hasWinOrCmd && normalized.Contains("q"))
        {
            reason = "安全策略禁止发送系统锁屏快捷键 Ctrl+Cmd+Q。";
            return true;
        }

        if (hasWinOrCmd && hasAlt && normalized.Contains("esc"))
        {
            reason = "安全策略禁止发送 Cmd+Option+Esc 强制退出面板热键。";
            return true;
        }

        if (hasAlt && normalized.Contains("tab") && !hasCtrl)
        {
            // Prevent blind Alt+Tab switching onto CodexFlow itself.
            reason = "请使用 computer_screen_info 指定目标窗口或直接点击目标窗口激活，安全策略禁止盲切 Alt+Tab。";
            return true;
        }

        return false;
    }

    public bool IsProtectedWindow(
        ComputerWindowInfoDto window,
        IReadOnlySet<int>? pids = null,
        IReadOnlySet<string>? handles = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.IsProtected)
        {
            return true;
        }

        var effectivePids = pids ?? ProtectedPidsSnapshot;
        var effectiveHandles = handles ?? ProtectedHandlesSnapshot;
        if (effectiveHandles.Contains(window.WindowHandle) ||
            effectivePids.Contains(window.ProcessId) ||
            (window.ProcessId > 0 && _driver.IsProcessInTree(window.ProcessId, effectivePids)))
        {
            return true;
        }

        return IsCodexFlowHostProcess(window.ProcessName, window.Title);
    }

    public bool IsBlockedProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = ComputerPermissionClamp.NormalizeSingleProcessName(processName);
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var blocked in _options.CurrentValue.BlockedProcessNames)
        {
            var blockedNorm = ComputerPermissionClamp.NormalizeSingleProcessName(blocked);
            if (blockedNorm.Length > 0 &&
                (string.Equals(normalized, blockedNorm, StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(blockedNorm, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCodexFlowHostProcess(string? processName, string? title)
    {
        if (!string.IsNullOrWhiteSpace(processName) &&
            processName.Contains("CodexFlow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(title) &&
               title.Contains("CodexFlow Desktop", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(processName) &&
               processName.Contains("electron", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKeyToken(string raw)
    {
        var key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "control" => "ctrl",
            "option" or "menu" => "alt",
            "win" or "cmd" or "command" or "super" => "meta",
            "escape" => "esc",
            "delete" => "del",
            _ => key
        };
    }
}
