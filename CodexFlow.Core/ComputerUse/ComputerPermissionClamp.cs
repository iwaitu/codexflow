// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CodexFlow.Core.ComputerUse;

/// <summary>
/// Enforces tighten-only clamping on hot-reloaded <see cref="ComputerOptions"/> file changes so an untrusted
/// disk edit can never silently enable desktop control, relax approval policy, or drop blocked sensitive apps.
/// Relaxations are applied exclusively through authenticated control-plane updates.
/// </summary>
public sealed class ComputerPermissionClamp :
    IPostConfigureOptions<ComputerOptions>,
    IOptionsChangeTokenSource<ComputerOptions>,
    IDisposable
{
    private readonly object _gate = new();
    private bool _baselineCaptured;

    private bool _lastRawEnabled;
    private ComputerInteractionApprovalPolicy _lastRawApprovalPolicy;
    private bool _lastRawEnableGridOverlay;
    private bool _lastRawAutoMinimizeHostWindow;
    private List<string> _lastRawBlockedProcessNames = [];

    private bool _effectiveEnabled = true;
    private ComputerInteractionApprovalPolicy _effectiveApprovalPolicy = ComputerInteractionApprovalPolicy.AlwaysAsk;
    private ComputerCoordinateMode _effectiveCoordinateMode = ComputerCoordinateMode.Auto;
    private int _effectiveMaxImageLongEdge = 1280;
    private int _effectiveMaxScreenshotsPerTurn = 12;
    private int _effectiveSlidingVisualRetentionCount = 3;
    private bool _effectiveEnableGridOverlay = true;
    private bool _effectiveAutoMinimizeHostWindow = true;
    private string _effectiveEmergencyStopShortcut = "CommandOrControl+.";
    private int _effectiveIdleTimeoutMinutes = 15;
    private List<string> _effectiveBlockedProcessNames = [.. ComputerOptions.DefaultBlockedProcessNames];
    private CancellationTokenSource _changeCts = new();

    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changeCts.Token);
        }
    }

    public void PostConfigure(string? name, ComputerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            var rawBlocked = NormalizeProcessNames(options.BlockedProcessNames);

            if (!_baselineCaptured)
            {
                _effectiveEnabled = _lastRawEnabled = options.Enabled;
                _effectiveApprovalPolicy = _lastRawApprovalPolicy = options.InteractionApprovalPolicy;
                _effectiveCoordinateMode = options.CoordinateMode;
                _effectiveMaxImageLongEdge = Math.Clamp(options.MaxImageLongEdge, 640, 2560);
                _effectiveMaxScreenshotsPerTurn = Math.Clamp(options.MaxScreenshotsPerTurn, 1, 30);
                _effectiveSlidingVisualRetentionCount = Math.Clamp(options.SlidingVisualRetentionCount, 1, 8);
                _effectiveEnableGridOverlay = _lastRawEnableGridOverlay = options.EnableGridOverlay;
                _effectiveAutoMinimizeHostWindow = _lastRawAutoMinimizeHostWindow = options.AutoMinimizeHostWindow;
                _effectiveEmergencyStopShortcut = NormalizeShortcut(options.EmergencyStopShortcut);
                _effectiveIdleTimeoutMinutes = Math.Clamp(options.IdleTimeoutMinutes, 1, 120);
                _effectiveBlockedProcessNames = MergeDefaultBlocked(rawBlocked);
                _lastRawBlockedProcessNames = [.. rawBlocked];
                _baselineCaptured = true;
            }
            else
            {
                if (options.Enabled != _lastRawEnabled)
                {
                    _lastRawEnabled = options.Enabled;
                    if (!options.Enabled)
                    {
                        _effectiveEnabled = false;
                    }
                }

                if (options.InteractionApprovalPolicy != _lastRawApprovalPolicy)
                {
                    _lastRawApprovalPolicy = options.InteractionApprovalPolicy;
                    if (ComputerOptions.IsStricterOrEqual(options.InteractionApprovalPolicy, _effectiveApprovalPolicy))
                    {
                        _effectiveApprovalPolicy = options.InteractionApprovalPolicy;
                    }
                }

                if (options.EnableGridOverlay != _lastRawEnableGridOverlay)
                {
                    _lastRawEnableGridOverlay = options.EnableGridOverlay;
                    _effectiveEnableGridOverlay = options.EnableGridOverlay;
                }

                if (options.AutoMinimizeHostWindow != _lastRawAutoMinimizeHostWindow)
                {
                    _lastRawAutoMinimizeHostWindow = options.AutoMinimizeHostWindow;
                    if (options.AutoMinimizeHostWindow)
                    {
                        // Enabling host window avoidance tightens self-UI safety; disabling requires control-plane.
                        _effectiveAutoMinimizeHostWindow = true;
                    }
                }

                if (!rawBlocked.SequenceEqual(_lastRawBlockedProcessNames, StringComparer.OrdinalIgnoreCase))
                {
                    _lastRawBlockedProcessNames = [.. rawBlocked];
                    foreach (var proc in rawBlocked)
                    {
                        if (!_effectiveBlockedProcessNames.Contains(proc, StringComparer.OrdinalIgnoreCase))
                        {
                            _effectiveBlockedProcessNames.Add(proc);
                        }
                    }
                }
            }

            ApplyEffectiveTo(options);
        }
    }

    public void ApplyControlPlaneUpdate(ComputerOptions updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        CancellationTokenSource oldCts;
        lock (_gate)
        {
            _effectiveEnabled = updated.Enabled;
            _effectiveApprovalPolicy = updated.InteractionApprovalPolicy;
            _effectiveCoordinateMode = updated.CoordinateMode;
            _effectiveMaxImageLongEdge = Math.Clamp(updated.MaxImageLongEdge, 640, 2560);
            _effectiveMaxScreenshotsPerTurn = Math.Clamp(updated.MaxScreenshotsPerTurn, 1, 30);
            _effectiveSlidingVisualRetentionCount = Math.Clamp(updated.SlidingVisualRetentionCount, 1, 8);
            _effectiveEnableGridOverlay = updated.EnableGridOverlay;
            _effectiveAutoMinimizeHostWindow = updated.AutoMinimizeHostWindow;
            _effectiveEmergencyStopShortcut = NormalizeShortcut(updated.EmergencyStopShortcut);
            _effectiveIdleTimeoutMinutes = Math.Clamp(updated.IdleTimeoutMinutes, 1, 120);
            _effectiveBlockedProcessNames = MergeDefaultBlocked(NormalizeProcessNames(updated.BlockedProcessNames));
            _baselineCaptured = true;

            oldCts = _changeCts;
            _changeCts = new CancellationTokenSource();
        }

        try
        {
            oldCts.Cancel();
        }
        finally
        {
            oldCts.Dispose();
        }
    }

    private void ApplyEffectiveTo(ComputerOptions options)
    {
        options.Enabled = _effectiveEnabled;
        options.InteractionApprovalPolicy = _effectiveApprovalPolicy;
        options.CoordinateMode = _effectiveCoordinateMode;
        options.MaxImageLongEdge = _effectiveMaxImageLongEdge;
        options.MaxScreenshotsPerTurn = _effectiveMaxScreenshotsPerTurn;
        options.SlidingVisualRetentionCount = _effectiveSlidingVisualRetentionCount;
        options.EnableGridOverlay = _effectiveEnableGridOverlay;
        options.AutoMinimizeHostWindow = _effectiveAutoMinimizeHostWindow;
        options.EmergencyStopShortcut = _effectiveEmergencyStopShortcut;
        options.IdleTimeoutMinutes = _effectiveIdleTimeoutMinutes;
        options.BlockedProcessNames = [.. _effectiveBlockedProcessNames];
    }

    private static string NormalizeShortcut(string? shortcut)
        => string.IsNullOrWhiteSpace(shortcut) ? "CommandOrControl+." : shortcut.Trim();

    private static List<string> NormalizeProcessNames(IEnumerable<string>? names)
        => names is null
            ? []
            : names
                .Where(static n => !string.IsNullOrWhiteSpace(n))
                .Select(static n => NormalizeSingleProcessName(n))
                .Where(static n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static List<string> MergeDefaultBlocked(List<string> custom)
    {
        var merged = new List<string>(ComputerOptions.DefaultBlockedProcessNames.Length + custom.Count);
        foreach (var def in ComputerOptions.DefaultBlockedProcessNames)
        {
            merged.Add(def);
        }

        foreach (var item in custom)
        {
            if (!merged.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    internal static string NormalizeSingleProcessName(string raw)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.EndsWith(".exe", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^4];
        }
        else if (trimmed.EndsWith(".app", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _changeCts.Dispose();
        }
    }
}
