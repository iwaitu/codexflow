// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace CodexFlow.Local;

/// <summary>
/// Watches <c>Local:ParentPid</c> when hosted by a parent desktop process and stops the host application
/// if the parent exits unexpectedly so no orphaned backend processes remain.
/// </summary>
public sealed class ParentProcessWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watchTask;

    public ParentProcessWatcher(int parentPid, Action onParentExited, ILogger? logger = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(onParentExited);
        if (parentPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentPid));
        }

        _watchTask = Task.Run(() => WatchLoopAsync(parentPid, onParentExited, logger, pollInterval ?? TimeSpan.FromSeconds(1), _cts.Token));
    }

    private static async Task WatchLoopAsync(
        int parentPid,
        Action onParentExited,
        ILogger? logger,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        try
        {
            Process? parentProcess;
            DateTime? initialStartTimeUtc = null;
            try
            {
                parentProcess = Process.GetProcessById(parentPid);
                try
                {
                    initialStartTimeUtc = parentProcess.StartTime.ToUniversalTime();
                }
                catch
                {
                    // StartTime may be inaccessible across privilege boundaries; WaitForExit/HasExited still works.
                }
            }
            catch (ArgumentException)
            {
                logger?.LogWarning("Parent process {ParentPid} was not running at startup; stopping host.", parentPid);
                onParentExited();
                return;
            }

            using (parentProcess)
            {
                while (!ct.IsCancellationRequested)
                {
                    if (parentProcess.HasExited)
                    {
                        logger?.LogInformation("Parent process {ParentPid} exited; stopping host.", parentPid);
                        onParentExited();
                        return;
                    }

                    try
                    {
                        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        pollCts.CancelAfter(pollInterval);
                        await parentProcess.WaitForExitAsync(pollCts.Token).ConfigureAwait(false);
                        logger?.LogInformation("Parent process {ParentPid} exited; stopping host.", parentPid);
                        onParentExited();
                        return;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Periodic re-check for PID reuse or missed handle signal.
                        if (initialStartTimeUtc.HasValue)
                        {
                            try
                            {
                                using var probe = Process.GetProcessById(parentPid);
                                if (probe.StartTime.ToUniversalTime() != initialStartTimeUtc.Value)
                                {
                                    logger?.LogInformation("Parent PID {ParentPid} was reused by another process; stopping host.", parentPid);
                                    onParentExited();
                                    return;
                                }
                            }
                            catch (ArgumentException)
                            {
                                logger?.LogInformation("Parent process {ParentPid} no longer exists; stopping host.", parentPid);
                                onParentExited();
                                return;
                            }
                            catch
                            {
                                // Ignore transient probe errors.
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down normally.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Parent process watcher stopped unexpectedly for PID {ParentPid}.", parentPid);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _watchTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best effort shutdown.
        }

        _cts.Dispose();
    }
}
