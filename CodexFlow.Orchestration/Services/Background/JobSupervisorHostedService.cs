// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using CodexFlow.Core.Abstractions;
using System.Diagnostics;

namespace CodexFlow.Services.Background;

/// <summary>
/// Level 9 后台作业宿主标准模板。
/// </summary>
/// <remarks>
/// 设计约束（来自 DB_MIGRATION_AND_SCOPED_DI_BLUEPRINT.md §3.2 / §5.3）：
/// 1. 本类是 Singleton（由 <c>IHostedService</c> 生命周期决定），禁止构造函数注入 Scoped 服务。
/// 2. 唯一允许持有的字段包括 <c>IServiceScopeFactory</c>、<c>ILogger&lt;T&gt;</c>、<c>IConfiguration</c>、<c>IOptions&lt;T&gt;</c> 和线程安全单例。
/// 3. 每次执行 claim、execute、heartbeat、checkpoint、finalize 都必须创建新的异步 scope，并在步骤完成后立即释放。
/// 4. 跨步骤只传递值对象，例如 <c>jobId</c>、<c>claimToken</c>、<c>attempt</c>、<c>resumeToken</c>。
/// 5. 不接受“1 个 job = 1 个贯穿全生命周期 DI scope”的实现。
/// </remarks>
public sealed class JobSupervisorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobSupervisorHostedService> _logger;
    private readonly IBackgroundJobSignal _jobSignal;

    /// <summary>
    /// Initializes the job supervisor hosted service.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create per-step dependency scopes.</param>
    /// <param name="logger">Logger used for dispatch loop diagnostics and lifecycle events.</param>
    /// <param name="jobSignal">Signal source used to wake the dispatcher when new jobs arrive.</param>
    public JobSupervisorHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobSupervisorHostedService> logger,
        IBackgroundJobSignal jobSignal)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobSignal = jobSignal;
    }

    private readonly string _runnerId = $"Supervisor-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";
    private int _jobsProcessed = 0;
    private readonly Stopwatch _uptimeStopwatch = new();

        /// <summary>
        /// Runs the background dispatch loop that claims and executes pending jobs.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token that stops the supervisor.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
        var processId = Environment.ProcessId;
        var startTime = DateTime.UtcNow;
        _uptimeStopwatch.Start();

        _logger.LogInformation(
            "JobSupervisorHostedService starting. RunnerId={RunnerId}, PID={ProcessId}, StartTime={StartTime:O}",
            _runnerId, processId, startTime);

        // Background maintenance loop for expired leases
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var leaseService = scope.ServiceProvider.GetRequiredService<IBackgroundJobLeaseService>();
                    await leaseService.MarkExpiredJobsAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in background maintenance loop");
                }
            }
        }, stoppingToken);

        // Exponential backoff parameters
        var minDelay = TimeSpan.FromMilliseconds(500);
        var maxDelay = TimeSpan.FromSeconds(15);
        var currentDelay = minDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await DispatchNextJobAsync(stoppingToken);
                if (claimed)
                {
                    // Reset backoff on successful claim
                    currentDelay = minDelay;
                }
                else
                {
                    // Exponential backoff when idle - wait for signal or timeout
                    await _jobSignal.WaitAsync(currentDelay, stoppingToken);
                    currentDelay = TimeSpan.FromMilliseconds(Math.Min(currentDelay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in job dispatch loop. Backing off before retry.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        var uptime = _uptimeStopwatch.Elapsed;
        _logger.LogInformation(
            "JobSupervisorHostedService stopped. RunnerId={RunnerId}, Reason={Reason}, Uptime={Uptime}, JobsProcessed={JobsProcessed}",
            _runnerId, stoppingToken.IsCancellationRequested ? "HostShutdown" : "Unknown", uptime, _jobsProcessed);
    }

    private async Task<bool> DispatchNextJobAsync(CancellationToken ct)
    {
        (string JobId, string ClaimToken)? claim;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var leaseService = scope.ServiceProvider.GetRequiredService<IBackgroundJobLeaseService>();
            claim = await leaseService.TryClaimNextAsync(_runnerId, ct);
        }

        if (claim == null)
        {
            return false;
        }

        var jobId = claim.Value.JobId;
        var claimToken = claim.Value.ClaimToken;
        Interlocked.Increment(ref _jobsProcessed);

        _logger.LogInformation("Job {JobId} claimed by {RunnerId}. Starting execution...", jobId, _runnerId);

        // Execute in background to allow supervisor to continue claiming
        // Note: In Phase 2+ we would limit concurrency here
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<IBackgroundJobRunner>();
                await runner.ExecuteAsync(jobId, claimToken, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Panic in job execution thread for job {JobId}", jobId);
            }
        }, ct);

        return true;
    }
}
