// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;

namespace CodexFlow.Orchestration.Hosting;

public sealed record HostPreflightStatus(
    bool ModelConfigured,
    bool? ModelReachable,
    string ModelProbeStatus,
    bool ActiveBackendAvailable,
    string? ActiveBackendReason,
    bool DockerAvailable,
    string? DockerReason,
    LocalPreflightMode PreflightMode,
    IReadOnlyList<PreflightIssue> Issues,
    DateTimeOffset CheckedAtUtc);

public interface IHostPreflightService
{
    Task<HostPreflightStatus> GetStatusAsync(
        ExecutionBackend activeBackend,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

public sealed class HostPreflightService(
    IConfiguration configuration,
    Func<ExecutionBackend, CancellationToken, Task<HostPreflightStatus>>? customProbe = null,
    HttpMessageHandler? httpHandler = null) : IHostPreflightService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly Lock _stateLock = new();
    private readonly Dictionary<ExecutionBackend, HostPreflightStatus> _cachedByBackend = new();
    private readonly Dictionary<ExecutionBackend, Task<HostPreflightStatus>> _inFlightByBackend = new();

    public async Task<HostPreflightStatus> GetStatusAsync(
        ExecutionBackend activeBackend,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        Task<HostPreflightStatus> task;
        lock (_stateLock)
        {
            if (!forceRefresh && _cachedByBackend.TryGetValue(activeBackend, out var hit) &&
                DateTimeOffset.UtcNow - hit.CheckedAtUtc < CacheTtl)
            {
                return hit;
            }

            if (_inFlightByBackend.TryGetValue(activeBackend, out var current) && !current.IsCompleted)
            {
                task = current;
            }
            else
            {
                task = RunAndCacheAsync(activeBackend);
                _inFlightByBackend[activeBackend] = task;
            }
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<HostPreflightStatus> RunAndCacheAsync(ExecutionBackend activeBackend)
    {
        try
        {
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = customProbe is not null
                ? await customProbe(activeBackend, probeTimeout.Token).ConfigureAwait(false)
                : await ProbeAsync(activeBackend, probeTimeout.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                _cachedByBackend[activeBackend] = status;
            }

            return status;
        }
        finally
        {
            lock (_stateLock)
            {
                if (_inFlightByBackend.TryGetValue(activeBackend, out var current) && current.IsCompleted)
                {
                    _inFlightByBackend.Remove(activeBackend);
                }
            }
        }
    }

    private async Task<HostPreflightStatus> ProbeAsync(ExecutionBackend activeBackend, CancellationToken ct)
    {
        var modelSection = configuration.GetSection("VllmAgent");
        var modelConfigured =
            !string.IsNullOrWhiteSpace(modelSection["ApiUrl"]) &&
            !string.IsNullOrWhiteSpace(modelSection["ApiKey"]) &&
            !string.IsNullOrWhiteSpace(modelSection["Model"]);
        var apiMode = modelSection["ApiMode"];
        var supportsModelReachabilityProbe =
            string.IsNullOrWhiteSpace(apiMode) ||
            string.Equals(apiMode.Trim(), "ChatCompletions", StringComparison.OrdinalIgnoreCase);

        // Pass the effective runtime backend to HostPreflight.RunAsync so runtime clamp overrides
        // (e.g. Docker fallback to Local) are reflected accurately in preflight issues.
        var effectiveConfig = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ExecutionOptions.SectionName}:Backend"] = activeBackend.ToString()
            })
            .Build();

        // Probe Docker regardless of active backend so the UI can show whether Docker is available as an option.
        var dockerFailure = await ProbeDockerDaemonAsync(ct).ConfigureAwait(false);
        var dockerAvailable = dockerFailure is null;

        // Run HostPreflight with injected dockerProbe result to avoid spawning docker info twice.
        var issues = await HostPreflight.RunAsync(
            effectiveConfig,
            handler: httpHandler,
            dockerProbe: _ => Task.FromResult(dockerFailure),
            cancellationToken: ct).ConfigureAwait(false);

        var hasModelIssue = issues.Any(i => string.Equals(i.Component, "Model", StringComparison.OrdinalIgnoreCase));
        bool? modelReachable;
        string modelProbeStatus;
        if (!modelConfigured)
        {
            modelReachable = false;
            modelProbeStatus = "NotConfigured";
        }
        else if (!supportsModelReachabilityProbe)
        {
            modelReachable = null;
            modelProbeStatus = "NotProbed";
        }
        else
        {
            modelReachable = !hasModelIssue;
            modelProbeStatus = modelReachable == true ? "Reachable" : "Unreachable";
        }

        var activeAvailable = activeBackend == ExecutionBackend.Local || dockerAvailable;
        var activeReason = activeBackend == ExecutionBackend.Local
            ? null
            : dockerFailure;

        var preflightMode = HostPreflight.ResolvePreflightMode(effectiveConfig);
        return new HostPreflightStatus(
            ModelConfigured: modelConfigured,
            ModelReachable: modelReachable,
            ModelProbeStatus: modelProbeStatus,
            ActiveBackendAvailable: activeAvailable,
            ActiveBackendReason: activeReason,
            DockerAvailable: dockerAvailable,
            DockerReason: dockerFailure,
            PreflightMode: preflightMode,
            Issues: issues.ToArray(),
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    private static async Task<string?> ProbeDockerDaemonAsync(CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("info");
            info.ArgumentList.Add("--format");
            info.ArgumentList.Add("{{.ServerVersion}}");

            using var process = Process.Start(info);
            if (process is null)
            {
                return "the docker command did not start.";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (!ct.IsCancellationRequested)
                {
                    return "the docker daemon did not answer within 5 seconds.";
                }

                throw;
            }

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(await stdout.ConfigureAwait(false)))
            {
                return null;
            }

            var reason = (await stderr.ConfigureAwait(false)).Trim();
            return reason.Length == 0 ? $"docker info exited with code {process.ExitCode}." : reason.Split('\n')[0].Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "the docker command was not found on PATH.";
        }
    }
}

/// <summary>
/// Resolves the root directory where managed session workspaces are created, decoupling them from <c>cwd</c>
/// when <c>Local:DataDirectory</c> is configured.
/// </summary>
public interface IWorkspaceRootProvider
{
    string WorkspacesRoot { get; }
    string? DataDirectory { get; }
}

public sealed class WorkspaceRootProvider : IWorkspaceRootProvider
{
    public WorkspaceRootProvider(string workspacesRoot, string? dataDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacesRoot);
        WorkspacesRoot = Path.GetFullPath(workspacesRoot);
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.GetFullPath(dataDirectory);
    }

    public string WorkspacesRoot { get; }
    public string? DataDirectory { get; }
}
