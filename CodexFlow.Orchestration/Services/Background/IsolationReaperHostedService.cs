// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Microsoft.Extensions.Hosting;

namespace CodexFlow.Services.Background;

/// <summary>
/// Runs the isolation reaper for the lifetime of the host: one pass at startup (crash/restart recovery of containers and
/// staging owned by DEAD instances of this machine) and then periodically. Live instances' sessions are never touched
/// (ownership lease), see IsolationReaper / DockerIsolatedExecutionService.ReapOrphansAsync.
/// </summary>
public sealed class IsolationReaperHostedService(IsolationReaper reaper) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => reaper.RunAsync(stoppingToken);
}
