// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Routes new execution sessions to the backend currently selected by <see cref="ExecutionOptions.Backend"/>.
/// Sessions already opened keep using the backend they started on.
/// Never falls back from Docker to Local when Docker is unavailable.
/// </summary>
public sealed class ExecutionBackendRouter : IIsolatedExecutionService
{
    private readonly IOptionsMonitor<ExecutionOptions> _options;
    private readonly DockerIsolatedExecutionService _docker;
    private readonly IIsolatedExecutionService? _local;

    public ExecutionBackendRouter(
        IOptionsMonitor<ExecutionOptions> options,
        DockerIsolatedExecutionService docker,
        IIsolatedExecutionService? local = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _local = local;
    }

    public ExecutionBackend ActiveBackend => _options.CurrentValue.Backend;

    public bool SupportsNetworkMode(ExecutionNetworkMode mode)
    {
        var backend = _options.CurrentValue.Backend;
        return backend switch
        {
            ExecutionBackend.Docker => _docker.SupportsNetworkMode(mode),
            ExecutionBackend.Local => _local?.SupportsNetworkMode(mode) ?? (mode is ExecutionNetworkMode.None or ExecutionNetworkMode.Host),
            _ => false
        };
    }

    public Task<IsolatedSessionOpenResult> OpenSessionAsync(
        TrustedExecutionContext context,
        IsolatedSessionOptions options,
        CancellationToken ct)
        => OpenPinnedBackendSessionAsync(_options.CurrentValue.Backend, context, options, ct);

    public Task<IsolatedSessionOpenResult> OpenPinnedBackendSessionAsync(
        ExecutionBackend pinnedBackend,
        TrustedExecutionContext context,
        IsolatedSessionOptions options,
        CancellationToken ct)
    {
        if (_options.CurrentValue.Backend != pinnedBackend)
        {
            return Task.FromResult(new IsolatedSessionOpenResult(
                ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    $"Execution backend changed from approved '{pinnedBackend}' to '{_options.CurrentValue.Backend}' before session start."),
                null));
        }

        return pinnedBackend switch
        {
            ExecutionBackend.Docker => _docker.OpenSessionAsync(context, options, ct),
            ExecutionBackend.Local when _local is not null => _local.OpenSessionAsync(context, options, ct),
            ExecutionBackend.Local => Task.FromResult(new IsolatedSessionOpenResult(
                ExecutionDecision.Unavailable(
                    ExecutionReasonCodes.IsolationNotConfigured,
                    "Local execution backend is selected, but no local execution service is registered yet."),
                null)),
            _ => Task.FromResult(new IsolatedSessionOpenResult(
                ExecutionDecision.Unavailable(
                    ExecutionReasonCodes.UnsupportedIsolation,
                    $"Unknown execution backend '{pinnedBackend}'."),
                null))
        };
    }
}
