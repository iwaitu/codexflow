// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

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

    public TimeSpan HeadlessApprovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
/// Enforces runtime tighten-only permission clamping across <see cref="Microsoft.Extensions.Options.IOptionsMonitor{ExecutionOptions}"/>
/// reloads: file reloads can tighten the active permission mode, never relax it. Only an explicit
/// control-plane update may relax the clamp.
/// </summary>
public sealed class ExecutionPermissionClamp : Microsoft.Extensions.Options.IPostConfigureOptions<ExecutionOptions>
{
    private readonly object _gate = new();
    private ExecutionPermissionMode? _activeFloor;

    public void PostConfigure(string? name, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_activeFloor is null || ExecutionOptions.IsStricterOrEqual(options.PermissionMode, _activeFloor.Value))
            {
                _activeFloor = options.PermissionMode;
            }
            else
            {
                options.PermissionMode = _activeFloor.Value;
            }
        }
    }

    public void SetControlPlaneMode(ExecutionPermissionMode mode)
    {
        lock (_gate)
        {
            _activeFloor = mode;
        }
    }
}
