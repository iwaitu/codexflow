// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Liveness lease of one isolation service instance: an exclusively opened file under the staging root that exists as
/// long as the instance runs. Another instance (or a restart of the same host) can tell "owner still alive" from "owner
/// dead" by whether the file is still locked, without any shared state beyond the staging directory.
/// </summary>
internal sealed class IsolationOwnerLease : IDisposable
{
    private readonly FileStream _stream;

    private IsolationOwnerLease(FileStream stream) => _stream = stream;

    private static string LeasePath(string stagingRoot, string ownerId) => Path.Combine(stagingRoot, "owners", ownerId + ".lease");

    public static IsolationOwnerLease Acquire(string stagingRoot, string ownerId)
    {
        Directory.CreateDirectory(Path.Combine(stagingRoot, "owners"));
        var stream = new FileStream(LeasePath(stagingRoot, ownerId), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new IsolationOwnerLease(stream);
    }

    /// <summary>True when some process still holds the owner's lease. A missing or unlocked lease means the owner is dead.</summary>
    public static bool IsAlive(string stagingRoot, string ownerId)
    {
        var path = LeasePath(stagingRoot, ownerId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Startup + periodic recovery of containers/staging left by dead instances (crash, restart). It reaps only what
/// <see cref="DockerIsolatedExecutionService.ReapOrphansAsync"/> proves is owned by a dead instance of this host.
/// Core has no hosting dependency, so a host-side IHostedService runs <see cref="RunAsync"/> (see the integration note).
/// </summary>
public sealed class IsolationReaper(
    DockerIsolatedExecutionService service,
    IOptions<IsolationOptions> options,
    ILogger<IsolationReaper> logger)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ReapInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await service.ReapOrphansAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    logger.LogWarning("Isolation reaper removed {Count} orphaned container(s) of dead instances.", removed);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Isolation reaper pass failed; will retry.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
