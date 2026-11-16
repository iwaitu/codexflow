// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CodexFlow.Core.Execution;

public enum ExecutionBackend
{
    Docker = 0,
    Local = 1
}

public enum ExecutionPermissionMode
{
    Ask = 0,
    AutoInWorkspace = 1,
    FullAuto = 2,
    Deny = 3
}

public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>
    /// Execution backend for untrusted code tools. Remains Docker until P2, P1-4 Phase 1, and upgrade migration are ready.
    /// </summary>
    public ExecutionBackend Backend { get; set; } = ExecutionBackend.Docker;

    public ExecutionPermissionMode PermissionMode { get; set; } = ExecutionPermissionMode.Ask;

    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan HeadlessApprovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Controls whether background jobs without active SSE subscribers suspend into <c>WaitingUser</c>
    /// instead of failing with <c>approval_unavailable</c> after <see cref="HeadlessApprovalTimeout"/>.
    /// When <c>null</c>, managed pipe control-plane hosts suspend while standalone file hosts fail fast.
    /// </summary>
    public bool? SuspendUnsubscribedBackgroundJobs { get; set; }

    /// <summary>
    /// User-configured trusted executable files or install directories (e.g. ripgrep on Windows)
    /// eligible for <see cref="ExecutionPermissionMode.AutoInWorkspace"/> and <c>allow_always</c>.
    /// Cannot be set from project-level <c>codexflow.yml</c>.
    /// </summary>
    public IList<string> TrustedExecutablePaths { get; init; } = new List<string>();

    /// <summary>
    /// Strictness ranking: Deny (3) > Ask (2) > AutoInWorkspace (1) > FullAuto (0).
    /// Used so runtime hot-reload from appsettings.json can tighten, never relax, the permission mode.
    /// </summary>
    public static int StrictnessRank(ExecutionPermissionMode mode) => mode switch
    {
        ExecutionPermissionMode.Deny => 3,
        ExecutionPermissionMode.Ask => 2,
        ExecutionPermissionMode.AutoInWorkspace => 1,
        ExecutionPermissionMode.FullAuto => 0,
        _ => 2
    };

    public static bool IsStricterOrEqual(ExecutionPermissionMode candidate, ExecutionPermissionMode current)
        => StrictnessRank(candidate) >= StrictnessRank(current);
}

/// <summary>
/// Enforces runtime tighten-only permission clamping across <see cref="IOptionsMonitor{ExecutionOptions}"/>
/// reloads AND serves as an immediate in-memory <see cref="IOptionsChangeTokenSource{ExecutionOptions}"/> when
/// authenticated control-plane endpoints update <see cref="ExecutionBackend"/> or <see cref="ExecutionPermissionMode"/>,
/// so active runtime options update synchronously even when file reload is disabled or shadowed by env/CLI sources.
/// </summary>
public sealed class ExecutionPermissionClamp :
    IPostConfigureOptions<ExecutionOptions>,
    IOptionsChangeTokenSource<ExecutionOptions>,
    IDisposable
{
    private readonly object _gate = new();
    private volatile bool _hasObservedBaseline;
    private ExecutionBackend? _lastObservedRawBackend;
    private ExecutionPermissionMode? _lastObservedRawConfigMode;
    private ExecutionPermissionMode? _effectiveMode;
    private ExecutionBackend? _controlPlaneBackendOverride;
    private CancellationTokenSource _changeCts = new();

    /// <summary>Deterministic synchronization hook invoked right before acquiring <c>_gate</c> inside <see cref="TryApplySettingsUpdateAtomically"/> in tests.</summary>
    internal Action<ExecutionPermissionMode?>? OnEnteringAtomicUpdateForTesting { get; set; }

    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changeCts.Token);
        }
    }

    public void PostConfigure(string? name, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            var rawBackend = options.Backend;
            var rawConfigMode = options.PermissionMode;
            _lastObservedRawBackend = rawBackend;
            if (_controlPlaneBackendOverride is { } backendOverride)
            {
                options.Backend = backendOverride;
            }

            if (_lastObservedRawConfigMode is null)
            {
                _lastObservedRawConfigMode = rawConfigMode;
                _effectiveMode ??= rawConfigMode;
            }
            else if (rawConfigMode != _lastObservedRawConfigMode.Value)
            {
                // Underlying configuration source genuinely changed: enforce tighten-only clamping against active effective mode
                // (so a config reload to a stricter mode ALWAYS tightens, even after an earlier control-plane relaxation).
                _lastObservedRawConfigMode = rawConfigMode;
                if (_effectiveMode is null || ExecutionOptions.IsStricterOrEqual(rawConfigMode, _effectiveMode.Value))
                {
                    _effectiveMode = rawConfigMode;
                }
            }

            options.PermissionMode = _effectiveMode ?? rawConfigMode;
            _hasObservedBaseline = true;
        }
    }

    public void SetControlPlaneMode(ExecutionPermissionMode mode)
        => ApplyControlPlaneUpdate(backend: null, permissionMode: mode);

    public void ApplyControlPlaneUpdate(ExecutionBackend? backend, ExecutionPermissionMode? permissionMode)
    {
        CancellationTokenSource oldCts;
        lock (_gate)
        {
            if (backend is { } b)
            {
                _controlPlaneBackendOverride = b;
            }

            if (permissionMode is { } m)
            {
                _effectiveMode = m;
                _hasObservedBaseline = true;
            }

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

    /// <summary>
    /// Resolves the live effective execution backend and permission mode without ever reading
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> while holding <c>_gate</c> (preventing lock-order
    /// inversion with <see cref="PostConfigure"/> during concurrent options cache reloads, B7-1).
    /// </summary>
    public (ExecutionBackend Backend, ExecutionPermissionMode PermissionMode) ResolveEffective(
        IOptionsMonitor<ExecutionOptions> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);

        // Only sample CurrentValue OUTSIDE `_gate` when this clamp has never observed an initial baseline yet.
        ExecutionOptions? fallbackSnapshot = _hasObservedBaseline ? null : optionsMonitor.CurrentValue;

        lock (_gate)
        {
            if (fallbackSnapshot is not null)
            {
                _lastObservedRawBackend ??= fallbackSnapshot.Backend;
                _lastObservedRawConfigMode ??= fallbackSnapshot.PermissionMode;
                _effectiveMode ??= fallbackSnapshot.PermissionMode;
                _hasObservedBaseline = true;
            }

            var backend = _controlPlaneBackendOverride ?? _lastObservedRawBackend ?? ExecutionBackend.Docker;
            var mode = _effectiveMode ?? _lastObservedRawConfigMode ?? ExecutionPermissionMode.Ask;
            return (backend, mode);
        }
    }

    /// <summary>
    /// Atomically evaluates caller privileges against the live effective state, persists the update,
    /// and updates the in-memory clamp under <c>_gate</c> WITHOUT ever invoking <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>
    /// inside <c>_gate</c> (preserving strict lock order <c>OptionsCache.Lazy -&gt; _gate -&gt; SettingsFile</c>, B6-1 &amp; B7-1).
    /// </summary>
    public bool TryApplySettingsUpdateAtomically(
        IOptionsMonitor<ExecutionOptions> optionsMonitor,
        ExecutionBackend? backend,
        ExecutionPermissionMode? permissionMode,
        bool isControlPlaneCaller,
        out ExecutionBackend effectiveBackend,
        out ExecutionPermissionMode effectiveMode,
        Action<ExecutionBackend?, ExecutionPermissionMode?>? persistUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);

        // Sample CurrentValue STRICTLY OUTSIDE `_gate` and only if no baseline has been observed yet.
        ExecutionOptions? fallbackSnapshot = _hasObservedBaseline ? null : optionsMonitor.CurrentValue;

        OnEnteringAtomicUpdateForTesting?.Invoke(permissionMode);
        CancellationTokenSource oldCts;
        lock (_gate)
        {
            if (fallbackSnapshot is not null)
            {
                _lastObservedRawBackend ??= fallbackSnapshot.Backend;
                _lastObservedRawConfigMode ??= fallbackSnapshot.PermissionMode;
                _effectiveMode ??= fallbackSnapshot.PermissionMode;
                _hasObservedBaseline = true;
            }

            var liveBackend = _controlPlaneBackendOverride ?? _lastObservedRawBackend ?? ExecutionBackend.Docker;
            var liveMode = _effectiveMode ?? _lastObservedRawConfigMode ?? ExecutionPermissionMode.Ask;

            var isRelaxingMode = permissionMode is { } targetMode &&
                                 !ExecutionOptions.IsStricterOrEqual(targetMode, liveMode);
            var isChangingBackend = backend is { } targetBackend &&
                                    targetBackend != liveBackend;

            if ((isRelaxingMode || isChangingBackend) && !isControlPlaneCaller)
            {
                effectiveBackend = liveBackend;
                effectiveMode = liveMode;
                return false;
            }

            persistUpdate?.Invoke(backend, permissionMode);

            if (backend is { } b)
            {
                _controlPlaneBackendOverride = b;
                liveBackend = b;
            }

            if (permissionMode is { } m)
            {
                _effectiveMode = m;
                liveMode = m;
            }

            effectiveBackend = liveBackend;
            effectiveMode = liveMode;
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

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _changeCts.Dispose();
        }
    }
}
