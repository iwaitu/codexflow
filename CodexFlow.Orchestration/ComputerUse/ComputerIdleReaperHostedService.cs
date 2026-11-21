// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.ComputerUse;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.ComputerUse;

/// <summary>
/// Periodically reclaims idle desktop Computer Use leases and releases resources cleanly on host exit.
/// </summary>
public sealed class ComputerIdleReaperHostedService(
    IComputerSessionManager computerSessionManager,
    ILogger<ComputerIdleReaperHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await computerSessionManager.CleanupIdleSessionsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Computer Use idle session sweep encountered an error.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await computerSessionManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disposing ComputerSessionManager during host shutdown.");
        }
    }
}
