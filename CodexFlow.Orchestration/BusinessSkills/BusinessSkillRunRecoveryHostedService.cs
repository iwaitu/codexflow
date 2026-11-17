// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.BusinessSkills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Orchestration.BusinessSkills;

/// <summary>
/// 宿主启动时对遗留的 Running / WaitingForApproval / Dispatching 业务运行执行状态对账与收敛恢复：
/// 已发出写入但进程崩溃的一律标记为 OutcomeUnknown，绝不自动重放写入。
/// </summary>
public sealed class BusinessSkillRunRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<BusinessSkillsOptions> options,
    ILogger<BusinessSkillRunRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runService = scope.ServiceProvider.GetService<IBusinessSkillRunService>();
            if (runService is not null)
            {
                await runService.ReconcileOrphanedRunsOnStartupAsync(cancellationToken).ConfigureAwait(false);
            }

            var retentionService = scope.ServiceProvider.GetService<IBusinessSkillRetentionService>();
            if (retentionService is not null)
            {
                await retentionService.PruneExpiredAsync(ct: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipped startup reconciliation of business skill runs (database may not be initialized yet).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
