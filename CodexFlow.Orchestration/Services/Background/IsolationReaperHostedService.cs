// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services.Background;

/// <summary>
/// Runs the isolation reaper for the lifetime of the host: one pass at startup (crash/restart recovery of containers and
/// staging owned by DEAD instances of this machine) and then periodically. Live instances' sessions are never touched
/// (ownership lease), see IsolationReaper / DockerIsolatedExecutionService.ReapOrphansAsync.
/// Skipped when the active execution backend is Local.
/// </summary>
public sealed class IsolationReaperHostedService(
    IsolationReaper reaper,
    LocalStagingReaper? localReaper = null,
    IOptionsMonitor<ExecutionOptions>? executionOptions = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = localReaper?.Interval ?? reaper.Interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (executionOptions?.CurrentValue.Backend == ExecutionBackend.Local && localReaper is not null)
                {
                    await localReaper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await reaper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
