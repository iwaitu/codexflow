// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Application.Jobs;
using CodexFlow.Application.Hooks;
using CodexFlow.Application.Notifications;
using CodexFlow.Application.Services;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Jobs;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Workers;
using CodexFlow.Core.Constants;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Xml.Linq;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// Executes claimed background jobs, including heartbeat renewal, checkpoints, hooks, and terminal persistence.
    /// </summary>
    public class BackgroundJobRunner : IBackgroundJobRunner
    {
        // Read-only workers change nothing, so a roomier budget costs time, not risk. A dependency review that reads several projects and their
        // lock files did not fit in 10 rounds (live: ExploreWorker stopped with MaxRoundsReached before reporting).
        private const int DefaultReadOnlyWorkerMaxRounds = 16;
        private const int MaxReadOnlyWorkerMaxRounds = 30;
        private const int DefaultForgeWorkerMaxRounds = 48;
        private const int MaxForgeWorkerMaxRounds = 80;
        private const int MaxTransientWorkerRetryAttempts = 4;

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BackgroundJobRunner> _logger;
        private readonly IOutboxSignal _outboxSignal;
        private readonly IWorkerHookDispatcher _workerHookDispatcher;
        private readonly IPlanTaskDispatchService _planTaskDispatchService;

        /// <summary>
        /// Initializes the background job runner.
        /// </summary>
        /// <param name="serviceProvider">Service provider used to create scoped execution services.</param>
        /// <param name="logger">Logger used for job execution diagnostics.</param>
        /// <param name="outboxSignal">Signal used to wake read-model projection after job state changes.</param>
        /// <param name="workerHookDispatcher">Dispatcher used to run worker lifecycle hooks.</param>
        /// <param name="planTaskDispatchService">Service used to continue approved plan execution after a worker completes.</param>
        public BackgroundJobRunner(
            IServiceProvider serviceProvider,
            ILogger<BackgroundJobRunner> logger,
            IOutboxSignal outboxSignal,
            IWorkerHookDispatcher workerHookDispatcher,
            IPlanTaskDispatchService planTaskDispatchService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _outboxSignal = outboxSignal;
            _workerHookDispatcher = workerHookDispatcher;
            _planTaskDispatchService = planTaskDispatchService;
        }

        private async Task RecordTokenUsageAsync(
            IServiceProvider serviceProvider,
            string? userId,
            int? promptTokens,
            int? completionTokens,
            int? cachedPromptTokens = null)
        {
            var prompt = promptTokens ?? 0;
            var completion = completionTokens ?? 0;
            if (string.IsNullOrWhiteSpace(userId) || (prompt <= 0 && completion <= 0))
            {
                return;
            }

            try
            {
                var tokenUsageService = serviceProvider.GetService<ITokenUsageService>();
                if (tokenUsageService == null)
                {
                    return;
                }

                await tokenUsageService.RecordTokenUsageAsync(userId, prompt, completion, cachedPromptTokens ?? 0);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping background token usage recording because the service provider is disposed. user={UserId}",
                    userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to record background token usage for user {UserId}. promptTokens={PromptTokens} completionTokens={CompletionTokens}",
                    userId,
                    prompt,
                    completion);
            }
        }

        /// <summary>
        /// Executes a claimed background job until it reaches a terminal or resumable state.
        /// </summary>
        /// <param name="jobId">Background job identifier.</param>
        /// <param name="claimToken">Lease claim token owned by the runner.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteAsync(string jobId, string claimToken, CancellationToken ct)
            => await ExecuteCoreAsync(
                jobId: jobId,
                claimToken: claimToken,
                ct: ct,
                enableHeartbeat: true,
                suppressNotifications: false);

        /// <summary>
        /// Executes a background job inline on the current request thread without lease heartbeats
        /// and without main-session notification injection.
        /// </summary>
        /// <param name="jobId">Background job identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteInlineAsync(string jobId, CancellationToken ct)
            => await ExecuteCoreAsync(
                jobId: jobId,
                claimToken: null,
                ct: ct,
                enableHeartbeat: false,
                suppressNotifications: true);

        private async Task ExecuteCoreAsync(
            string jobId,
            string? claimToken,
            CancellationToken ct,
            bool enableHeartbeat,
            bool suppressNotifications)
        {
            _logger.LogInformation(
                "BackgroundJobRunner entry. job={JobId} hasClaimToken={HasClaimToken} enableHeartbeat={EnableHeartbeat} suppressNotifications={SuppressNotifications} cancellationRequested={CancellationRequested}",
                jobId,
                !string.IsNullOrWhiteSpace(claimToken),
                enableHeartbeat,
                suppressNotifications,
                ct.IsCancellationRequested);

            CancellationTokenSource? heartbeatCts = null;
            Task? heartbeatTask = null;
            if (enableHeartbeat && !string.IsNullOrWhiteSpace(claimToken))
            {
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeatTask = Task.Run(async () =>
                {
                    while (!heartbeatCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), heartbeatCts.Token);

                            // Heartbeat must have its own scope to avoid DbContext concurrency with the main thread
                            using var hbScope = _serviceProvider.CreateScope();
                            var hbLeaseService = hbScope.ServiceProvider.GetRequiredService<IBackgroundJobLeaseService>();
                            await hbLeaseService.RenewLeaseAsync(jobId, claimToken, heartbeatCts.Token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Heartbeat failed for job {JobId}", jobId);
                        }
                    }
                }, heartbeatCts.Token);
            }

            try
            {
                using var execScope = _serviceProvider.CreateScope();
                var dbContext = execScope.ServiceProvider.GetRequiredService<IJobDbContext>();

                var job = await dbContext.BackgroundJobs.FindAsync(jobId);
                if (job == null) throw new InvalidOperationException($"Job {jobId} not found");
                var latestCheckpoint = await GetLatestCheckpointAsync(dbContext, jobId, ct);

                _logger.LogInformation(
                    "BackgroundJobRunner loaded job. job={JobId} type={JobType} status={Status} session={SessionId} task={TaskId} checkpoint={Checkpoint}",
                    job.JobId,
                    job.JobType,
                    job.Status,
                    job.SessionId ?? string.Empty,
                    job.TaskId ?? string.Empty,
                    latestCheckpoint?.CurrentStep ?? "<none>");

                if (!CanStartClaimedExecution(job, claimToken))
                {
                    _logger.LogInformation(
                        "Skipping stale background job execution before start. job={JobId} status={Status} storedClaimTokenPresent={StoredClaimTokenPresent} suppliedClaimTokenPresent={SuppliedClaimTokenPresent}",
                        job.JobId,
                        job.Status,
                        !string.IsNullOrWhiteSpace(job.ClaimToken),
                        !string.IsNullOrWhiteSpace(claimToken));
                    return;
                }

                // Shared pre-start authorization for EVERY worker start (fresh, queued-before-the-policy, resumed, retried): the stored
                // payload is not trusted - SQL session owner, authorized repository location and verified shadow worktrees are checked.
                var preflight = await WorkerExecutionPreflight.EvaluateAsync(
                    execScope.ServiceProvider.GetRequiredService<ISessionDirectory>(),
                    job,
                    execScope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>(),
                    ct).ConfigureAwait(false);
                string? preflightDeniedPayload = null;
                if (!preflight.Allowed)
                {
                    _logger.LogWarning(
                        "Worker execution refused by preflight. job={JobId} type={JobType} code={Code}",
                        job.JobId,
                        job.JobType,
                        preflight.Code);
                    preflightDeniedPayload = BuildPreflightDeniedPayload(job, preflight);
                }
                else if (preflight.Payload is not null)
                {
                    job.PayloadJson = preflight.Payload.ToString(Formatting.None);
                }

                if (preflightDeniedPayload is null &&
                    await TryFinalizeCommittedForgeRetryBeforeStartAsync(
                        dbContext,
                        job,
                        latestCheckpoint,
                        suppressNotifications,
                        ct).ConfigureAwait(false))
                {
                    return;
                }

                if ((preflightDeniedPayload is null || preflight.Kind == WorkerPreflightKind.WorkspaceUnavailable) &&
                    await TryDeferRetryNotBeforeAsync(dbContext, job, latestCheckpoint, ct).ConfigureAwait(false))
                {
                    return;
                }

                job.Status = BackgroundJobStatus.Running;
                job.StartedAtUtc = DateTime.UtcNow;
                job.UpdatedAtUtc = DateTime.UtcNow;
                
                // Close the outbox circuit: Started
                dbContext.OutboxEvents.Add(new OutboxEvent
                {
                    EventType = "JobStarted",
                    JobId = job.JobId,
                    SessionId = job.SessionId,
                    TaskId = job.TaskId,
                    Payload = "{}"
                });

                await dbContext.SaveChangesAsync(ct);
                _outboxSignal.Pulse();

                _logger.LogInformation("Executing job {JobId} of type {JobType}", jobId, job.JobType);

                if (ShouldEnterWaitingUser(job.PayloadJson, latestCheckpoint))
                {
                    await EnterWaitingUserAsync(dbContext, job, latestCheckpoint, suppressNotifications, ct);
                    return;
                }

                // Real handler dispatch by JobType
                var resultPayload = preflightDeniedPayload ?? job.JobType switch
                {
                    "ExploreWorker" => await ExecuteReadOnlyWorkerAsync(execScope.ServiceProvider, job, WorkerType.Explore, ct),
                    "ForgeWorker" => await ExecuteForgeWorkerAsync(execScope.ServiceProvider, job, ct),
                    "PlanWorker" => await ExecuteReadOnlyWorkerAsync(execScope.ServiceProvider, job, WorkerType.Plan, ct),
                    "VerifyWorker" => await ExecuteVerifyWorkerAsync(execScope.ServiceProvider, job, ct),
                    "MockSimpleTask" => await RunMockSimpleTaskAsync(job, ct),
                    _ => await SimulateJobAsync(ct)
                };

                // Determine final status from result payload
                var resultObj = TryParsePayload(resultPayload);
                var jobSucceeded = resultObj?["success"]?.Value<bool>() ?? true;
                latestCheckpoint = await GetLatestCheckpointAsync(dbContext, jobId, ct);

                if (!jobSucceeded &&
                    await TryPreserveQueuedRetryAsync(dbContext, job, resultObj, latestCheckpoint, suppressNotifications, ct).ConfigureAwait(false))
                {
                    return;
                }

                // A forge attempt that left the project unchanged gets one more attempt, told what the first one did.
                if (!jobSucceeded &&
                    string.Equals(job.JobType, "ForgeWorker", StringComparison.Ordinal) &&
                    job.Attempt < MaxTransientWorkerRetryAttempts &&
                    ForgeQualityRetry.ShouldRetry(resultObj, TryParsePayload(resultObj?["updatedPayloadJson"]?.Value<string>()) ?? TryParsePayload(job.PayloadJson)))
                {
                    await RequeueTransientWorkerFailureAsync(
                        dbContext,
                        execScope.ServiceProvider,
                        job,
                        latestCheckpoint,
                        resultPayload,
                        ct,
                        adjustPayload: retryPayload => ForgeQualityRetry.Apply(retryPayload, resultObj!),
                        reason: "forge_attempt_changed_nothing").ConfigureAwait(false);
                    return;
                }

                if (!jobSucceeded &&
                    ShouldRetryTransientWorkerFailure(resultObj) &&
                    job.Attempt < MaxTransientWorkerRetryAttempts)
                {
                    await RequeueTransientWorkerFailureAsync(
                        dbContext,
                        execScope.ServiceProvider,
                        job,
                        latestCheckpoint,
                        resultPayload,
                        ct).ConfigureAwait(false);
                    return;
                }

                if (!await IsCurrentExecutionOwnerAsync(dbContext, job.JobId, claimToken, ct).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Skipping terminal update from stale background job execution. job={JobId}",
                        job.JobId);
                    return;
                }

                if (ShouldMarkRecoveryNeeded(resultObj))
                {
                    var recoveryMessage = resultObj?["message"]?.Value<string>()
                        ?? resultObj?["summary"]?.Value<string>()
                        ?? "Worker requires recovery before it can continue.";
                    await MarkJobRecoveryNeededAsync(
                        dbContext,
                        job,
                        resultPayload,
                        recoveryMessage,
                        latestCheckpoint,
                        suppressNotifications,
                        ct).ConfigureAwait(false);
                    if (!suppressNotifications)
                    {
                        await ContinuePlanAfterRecoveryNeededWorkerAsync(job, resultPayload, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    return;
                }

                dbContext.JobCheckpoints.Add(CreateCheckpoint(
                    job,
                    latestCheckpoint,
                    jobSucceeded ? "Completed" : "Failed",
                    checkpointData: JsonConvert.SerializeObject(new
                    {
                        completedAtUtc = DateTime.UtcNow,
                        success = jobSucceeded
                    })));

                job.Status = jobSucceeded ? BackgroundJobStatus.Completed : BackgroundJobStatus.Failed;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.UpdatedAtUtc = DateTime.UtcNow;
                job.NotificationStatus = suppressNotifications
                    ? Domain.Jobs.NotificationStatus.Suppressed
                    : Domain.Jobs.NotificationStatus.Pending;
                if (!jobSucceeded)
                {
                    var baseMessage = resultObj?["message"]?.Value<string>() ?? "Task execution failed";
                    var detailCode = resultObj?["detailCode"]?.Value<string>();
                    var terminationReason = resultObj?["terminationReason"]?.Value<string>();
                    var totalRounds = resultObj?["totalRounds"]?.Value<int>();
                    var totalToolCalls = resultObj?["totalToolCalls"]?.Value<int>();

                    job.ErrorMessage = detailCode != null
                        ? $"{baseMessage} [DetailCode={detailCode}, Termination={terminationReason}, Rounds={totalRounds}, ToolCalls={totalToolCalls}]"
                        : baseMessage;
                }
                if (job.JobType == WorkerJobTypes.ToJobType(WorkerType.Forge))
                {
                    job.PayloadJson = resultObj?["updatedPayloadJson"]?.Value<string>() ?? job.PayloadJson;
                }
                resultPayload = await AttachJobNotificationXmlAsync(job, resultPayload, ct);
                job.ResultPayloadJson = resultPayload;

                // Close the outbox circuit: Completed or Failed
                dbContext.OutboxEvents.Add(new OutboxEvent
                {
                    EventType = jobSucceeded ? "JobCompleted" : "JobFailed",
                    JobId = job.JobId,
                    SessionId = job.SessionId,
                    TaskId = job.TaskId,
                    Payload = resultPayload
                });

                try
                {
                    await dbContext.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Skipping terminal update because the job claim changed before save. job={JobId}",
                        job.JobId);
                    dbContext.ChangeTracker.Clear();
                    return;
                }
                _outboxSignal.Pulse();
                if (!suppressNotifications)
                {
                    await DispatchNotificationImmediatelyAsync(job.JobId, CancellationToken.None);
                }

                if (jobSucceeded)
                {
                    await ContinuePlanAfterCompletedWorkerAsync(job, resultPayload, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown or external cancellation — mark for recovery, not as business failure
                _logger.LogWarning("Job {JobId} interrupted by shutdown/cancellation, marking for recovery", jobId);
                
                using var shutdownScope = _serviceProvider.CreateScope();
                var dbContext = shutdownScope.ServiceProvider.GetRequiredService<IJobDbContext>();
                
                var job = await dbContext.BackgroundJobs.FindAsync(jobId);
                if (job != null)
                {
                    var latestCheckpoint = await GetLatestCheckpointAsync(dbContext, jobId, CancellationToken.None);
                    if (!await IsCurrentExecutionOwnerAsync(dbContext, job.JobId, claimToken, CancellationToken.None).ConfigureAwait(false))
                    {
                        _logger.LogInformation(
                            "Skipping cancellation recovery update from stale background job execution. job={JobId}",
                            job.JobId);
                        return;
                    }

                    await MarkJobRecoveryNeededAsync(
                        dbContext,
                        job,
                        BuildRecoveryNeededPayload(
                            workerType: ResolveWorkerTypeName(job.JobType),
                            summary: "Worker execution was interrupted by host shutdown.",
                            resultText: null,
                            durationMs: null,
                            totalRounds: null,
                            totalToolCalls: null,
                            terminationReason: null,
                            detailCode: null,
                            recoveryReason: "host_shutdown",
                            runtimeFlags: null,
                            serviceDiagnostics: null),
                        "Interrupted by host shutdown",
                        latestCheckpoint,
                        suppressNotifications,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId} failed", jobId);
                
                using var errorScope = _serviceProvider.CreateScope();
                var dbContext = errorScope.ServiceProvider.GetRequiredService<IJobDbContext>();
                
                var job = await dbContext.BackgroundJobs.FindAsync(jobId);
                if (job != null)
                {
                    var latestCheckpoint = await GetLatestCheckpointAsync(dbContext, jobId, CancellationToken.None);
                    if (await TryPreserveQueuedRetryAsync(
                            dbContext,
                            job,
                            resultObj: null,
                            latestCheckpoint,
                            suppressNotifications,
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        _logger.LogInformation(
                            "Skipping terminal failure update because job is already queued for retry. job={JobId}",
                            jobId);
                        return;
                    }

                    if (!await IsCurrentExecutionOwnerAsync(dbContext, job.JobId, claimToken, CancellationToken.None).ConfigureAwait(false))
                    {
                        _logger.LogInformation(
                            "Skipping failure update from stale background job execution. job={JobId}",
                            job.JobId);
                        return;
                    }

                    job.Status = BackgroundJobStatus.Failed;
                    job.NotificationStatus = suppressNotifications
                        ? Domain.Jobs.NotificationStatus.Suppressed
                        : Domain.Jobs.NotificationStatus.Pending;
                    job.ErrorMessage = ex.Message;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.UpdatedAtUtc = DateTime.UtcNow;
                    var failurePayload = await AttachJobNotificationXmlAsync(
                        job,
                        Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            success = false,
                            message = ex.Message,
                            error = ex.Message
                        }),
                        CancellationToken.None);
                    job.ResultPayloadJson = failurePayload;

                    // Close the outbox circuit: Failed
                    dbContext.OutboxEvents.Add(new OutboxEvent
                    {
                        EventType = "JobFailed",
                        JobId = job.JobId,
                        SessionId = job.SessionId,
                        TaskId = job.TaskId,
                        Payload = failurePayload
                    });
                    
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                    _outboxSignal.Pulse();
                    if (!suppressNotifications)
                    {
                        await DispatchNotificationImmediatelyAsync(job.JobId, CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (heartbeatCts != null)
                {
                    heartbeatCts.Cancel();
                }

                if (heartbeatTask != null)
                {
                    await Task.WhenAny(heartbeatTask, Task.Delay(1000));
                }

                await TryReleaseLeaseAsync(jobId, claimToken, enableHeartbeat).ConfigureAwait(false);
            }
        }

        private static bool CanStartClaimedExecution(BackgroundJob job, string? claimToken)
        {
            if (string.IsNullOrWhiteSpace(claimToken))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(job.ClaimToken) ||
                !string.Equals(job.ClaimToken, claimToken, StringComparison.Ordinal))
            {
                return false;
            }

            return job.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Running;
        }

        private static async Task<bool> IsCurrentExecutionOwnerAsync(
            IJobDbContext dbContext,
            string jobId,
            string? claimToken,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(claimToken))
            {
                return true;
            }

            var current = await dbContext.BackgroundJobs
                .AsNoTracking()
                .Where(x => x.JobId == jobId)
                .Select(x => new
                {
                    x.Status,
                    x.ClaimToken
                })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (current == null || current.Status != BackgroundJobStatus.Running)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(current.ClaimToken) ||
                string.Equals(current.ClaimToken, claimToken, StringComparison.Ordinal);
        }

        private async Task TryReleaseLeaseAsync(string jobId, string? claimToken, bool enableHeartbeat)
        {
            if (!enableHeartbeat || string.IsNullOrWhiteSpace(claimToken))
            {
                return;
            }

            try
            {
                using var finalScope = _serviceProvider.CreateScope();
                var leaseService = finalScope.ServiceProvider.GetRequiredService<IBackgroundJobLeaseService>();
                await leaseService.ReleaseAsync(jobId, claimToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping lease release because host service provider is disposed. JobId={JobId}",
                    jobId);
            }
        }

        private async Task ContinuePlanAfterCompletedWorkerAsync(
            BackgroundJob job,
            string? resultPayloadJson,
            CancellationToken ct)
        {
            try
            {
                var result = await _planTaskDispatchService
                    .ContinueAfterCompletedWorkerAsync(job, resultPayloadJson, ct)
                    .ConfigureAwait(false);

                if (result.Dispatched)
                {
                    _logger.LogInformation(
                        "Plan auto-continuation dispatched next task. completedJob={CompletedJobId} completedTask={CompletedTaskId} nextTask={NextTaskId} nextJob={NextJobId}",
                        job.JobId,
                        job.TaskId ?? string.Empty,
                        result.TaskId ?? string.Empty,
                        result.JobId ?? string.Empty);
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping plan auto-continuation because the service provider is disposed. job={JobId}",
                    job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Plan auto-continuation failed after completed worker. job={JobId} task={TaskId}",
                    job.JobId,
                job.TaskId ?? string.Empty);
            }
        }

        private async Task ContinuePlanAfterRecoveryNeededWorkerAsync(
            BackgroundJob job,
            string? resultPayloadJson,
            CancellationToken ct)
        {
            try
            {
                var result = await _planTaskDispatchService
                    .ContinueAfterRecoveryNeededWorkerAsync(job, resultPayloadJson, ct)
                    .ConfigureAwait(false);

                if (result.Status == PlanTaskDispatchStatus.RecoveryResumed)
                {
                    _logger.LogInformation(
                        "Plan recovery auto-continuation resumed worker. job={JobId} task={TaskId}",
                        job.JobId,
                        job.TaskId ?? string.Empty);
                }
                else
                {
                    _logger.LogInformation(
                        "Plan recovery auto-continuation skipped. job={JobId} task={TaskId} status={Status} message={Message}",
                        job.JobId,
                        job.TaskId ?? string.Empty,
                        result.Status,
                        result.Message);
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping plan recovery auto-continuation because the service provider is disposed. job={JobId}",
                    job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Plan recovery auto-continuation failed. job={JobId} task={TaskId}",
                    job.JobId,
                    job.TaskId ?? string.Empty);
            }
        }

        private async Task RequeueTransientWorkerFailureAsync(
            IJobDbContext dbContext,
            IServiceProvider serviceProvider,
            BackgroundJob job,
            JobCheckpoint? latestCheckpoint,
            string resultPayload,
            CancellationToken ct,
            Action<JObject>? adjustPayload = null,
            string reason = "transient_provider_failure")
        {
            var resultObj = TryParsePayload(resultPayload);
            var detailCode = resultObj?["detailCode"]?.Value<string>()
                ?? "Transient worker failure";
            var nextAttempt = job.Attempt + 1;
            var retryDelay = ResolveTransientRetryDelay(detailCode, nextAttempt);
            var retryAvailableAtUtc = DateTime.UtcNow.Add(retryDelay);
            var updatedPayload = TryParsePayload(resultObj?["updatedPayloadJson"]?.Value<string>());
            var jobPayload = updatedPayload ?? TryParsePayload(job.PayloadJson) ?? new JObject();
            NormalizeForgePayloadForTransientRetry(jobPayload, resultObj);
            adjustPayload?.Invoke(jobPayload);
            jobPayload["retryNotBeforeUtc"] = retryAvailableAtUtc;
            jobPayload["retryDelaySeconds"] = (int)retryDelay.TotalSeconds;

            dbContext.JobCheckpoints.Add(CreateCheckpoint(
                job,
                latestCheckpoint,
                "TransientRetryQueued",
                checkpointData: JsonConvert.SerializeObject(new
                {
                    retryQueuedAtUtc = DateTime.UtcNow,
                    retryAvailableAtUtc,
                    retryDelaySeconds = (int)retryDelay.TotalSeconds,
                    nextAttempt,
                    detailCode
                })));

            job.Status = BackgroundJobStatus.Queued;
            job.Attempt = nextAttempt;
            job.ErrorMessage = $"Transient provider failure; retry queued after {(int)retryDelay.TotalSeconds}s. {TrimForLog(detailCode, 500)}";
            job.RunnerId = null;
            job.ClaimToken = null;
            job.LeaseExpiresAtUtc = null;
            job.DequeuedAtUtc = null;
            job.StartedAtUtc = null;
            job.FinishedAtUtc = null;
            job.CreatedAtUtc = retryAvailableAtUtc;
            job.UpdatedAtUtc = DateTime.UtcNow;
            job.PayloadJson = jobPayload.ToString(Formatting.None);

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                EventType = "JobRetried",
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                Payload = JsonConvert.SerializeObject(new
                {
                    reason,
                    detailCode,
                    retryAvailableAtUtc,
                    retryDelaySeconds = (int)retryDelay.TotalSeconds,
                    attempt = nextAttempt,
                    maxAttempts = MaxTransientWorkerRetryAttempts
                })
            });

            try
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping transient retry requeue because the job claim changed before save. job={JobId}",
                    job.JobId);
                dbContext.ChangeTracker.Clear();
                return;
            }
            _outboxSignal.Pulse();

            _logger.LogWarning(
                "Transient worker failure detected; job requeued. job={JobId} task={TaskId} attempt={Attempt}/{MaxAttempts} retryDelaySeconds={RetryDelaySeconds} retryAvailableAtUtc={RetryAvailableAtUtc:O} detail={Detail}",
                job.JobId,
                job.TaskId ?? string.Empty,
                nextAttempt,
                MaxTransientWorkerRetryAttempts,
                (int)retryDelay.TotalSeconds,
                retryAvailableAtUtc,
                TrimForLog(detailCode, 300));
        }

        private static TimeSpan ResolveTransientRetryDelay(string detail, int nextAttempt)
        {
            if (IsProviderThrottle(detail))
            {
                return nextAttempt switch
                {
                    <= 1 => TimeSpan.FromSeconds(60),
                    2 => TimeSpan.FromSeconds(120),
                    _ => TimeSpan.FromSeconds(240)
                };
            }

            return nextAttempt switch
            {
                <= 1 => TimeSpan.FromSeconds(15),
                2 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromSeconds(60)
            };
        }

        private static void NormalizeForgePayloadForTransientRetry(JObject payload, JObject? resultObj)
        {
            var mainWorkspacePath = payload["mainWorkspacePath"]?.Value<string>();
            var retained = resultObj?["worktreeRetained"]?.Value<bool?>()
                ?? payload["worktreeRetained"]?.Value<bool?>()
                ?? false;
            var shadowPath = payload["shadowWorktreePath"]?.Value<string>();
            if (!retained || string.IsNullOrWhiteSpace(shadowPath) || !Directory.Exists(shadowPath))
            {
                if (!string.IsNullOrWhiteSpace(mainWorkspacePath))
                {
                    payload["workspacePath"] = mainWorkspacePath;
                }

                payload.Remove("shadowWorktreePath");
                payload["worktreeRetained"] = false;
                return;
            }

            payload["workspacePath"] = shadowPath;
            payload["shadowWorktreePath"] = shadowPath;
            payload["worktreeRetained"] = true;
        }

        private static bool IsProviderThrottle(string detail) => TransientFailures.IsProviderThrottle(detail);

        private static bool ShouldRetryTransientWorkerFailure(JObject? payload)
        {
            if (payload == null)
            {
                return false;
            }

            return TransientFailures.IsTransientWorkerFailure(
                payload["detailCode"]?.Value<string>(),
                payload["message"]?.Value<string>());
        }

        private async Task<bool> TryDeferRetryNotBeforeAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            JobCheckpoint? latestCheckpoint,
            CancellationToken ct)
        {
            var payload = TryParsePayload(job.PayloadJson);
            var retryNotBeforeUtc = TryGetRetryNotBeforeUtc(payload)
                ?? TryGetCreatedAtNotBeforeUtc(job);
            if (retryNotBeforeUtc == null)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (retryNotBeforeUtc <= now)
            {
                if (payload?["retryNotBeforeUtc"] != null)
                {
                    payload.Remove("retryNotBeforeUtc");
                    payload.Remove("retryDelaySeconds");
                    job.PayloadJson = payload.ToString(Formatting.None);
                    await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return false;
            }

            dbContext.JobCheckpoints.Add(CreateCheckpoint(
                job,
                latestCheckpoint,
                "RetryDeferred",
                checkpointData: JsonConvert.SerializeObject(new
                {
                    deferredAtUtc = DateTime.UtcNow,
                    retryNotBeforeUtc
                })));

            job.Status = BackgroundJobStatus.Queued;
            job.RunnerId = null;
            job.ClaimToken = null;
            job.LeaseExpiresAtUtc = null;
            job.DequeuedAtUtc = null;
            job.StartedAtUtc = null;
            job.FinishedAtUtc = null;
            job.CreatedAtUtc = retryNotBeforeUtc.Value.UtcDateTime;
            job.UpdatedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = $"Transient provider failure; retry deferred until {retryNotBeforeUtc:O}.";

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                EventType = "JobRetried",
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                Payload = JsonConvert.SerializeObject(new
                {
                    reason = "retry_not_before_deferred",
                    retryNotBeforeUtc
                })
            });

            try
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping committed retry finalization because the job claim changed before save. job={JobId}",
                    job.JobId);
                dbContext.ChangeTracker.Clear();
                return true;
            }
            _outboxSignal.Pulse();

            _logger.LogInformation(
                "Background job retry claimed before retryNotBefore; deferred again. job={JobId} task={TaskId} retryNotBeforeUtc={RetryNotBeforeUtc:O}",
                job.JobId,
                job.TaskId ?? string.Empty,
                retryNotBeforeUtc.Value);

            return true;
        }

        /// <summary>
        /// Terminal failure for a worker the preflight refused. The unverified retry claims are removed from the stored payload so no
        /// later path (retry preservation, fast finalization) can complete the job from them.
        /// </summary>
        private static string BuildPreflightDeniedPayload(BackgroundJob job, WorkerPreflightOutcome outcome)
        {
            var payload = TryParsePayload(job.PayloadJson) ?? new JObject();
            payload["unverifiedLastCommitHash"] = payload["lastCommitHash"];
            payload["unverifiedLastChangedFiles"] = payload["lastChangedFiles"];
            payload.Remove("lastCommitHash");
            payload.Remove("lastChangedFiles");
            payload.Remove("shadowWorktreePath");
            payload["worktreeRetained"] = false;
            payload["preflightDenied"] = outcome.Code;
            job.PayloadJson = payload.ToString(Formatting.None);

            if (outcome.Kind == WorkerPreflightKind.WorkspaceUnavailable)
            {
                // Same shape the Forge worker itself reports for a missing workspace, so the existing transient-requeue handling applies.
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = $"Worker workspace is unavailable: {outcome.Path}",
                    workerType = ResolveWorkerTypeName(job.JobType),
                    detailCode = $"WorkspaceUnavailable: {outcome.Path}",
                    worktreePath = (string?)null,
                    workspacePath = outcome.Path,
                    worktreeRetained = false,
                    serviceDiagnostics = new Dictionary<string, object?>
                    {
                        ["workspaceUnavailable"] = true,
                        ["mainWorkspacePath"] = outcome.Path,
                        ["preflight"] = outcome.Code
                    },
                    updatedPayloadJson = job.PayloadJson
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = false,
                message = $"Worker execution is not authorized: {outcome.Message}",
                workerType = ResolveWorkerTypeName(job.JobType),
                detailCode = $"WorkerExecutionNotAuthorized:{outcome.Code}",
                worktreePath = (string?)null,
                workspacePath = (string?)null,
                worktreeRetained = false,
                updatedPayloadJson = job.PayloadJson
            });
        }

        private async Task<bool> TryFinalizeCommittedForgeRetryBeforeStartAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            JobCheckpoint? latestCheckpoint,
            bool suppressNotifications,
            CancellationToken ct)
        {
            if (!string.Equals(job.JobType, WorkerJobTypes.ToJobType(WorkerType.Forge), StringComparison.Ordinal))
            {
                return false;
            }

            var payload = TryParsePayload(job.PayloadJson);
            if (payload == null)
            {
                return false;
            }

            var lastCommitHash = payload["lastCommitHash"]?.Value<string>();
            var lastChangedFilePaths = payload["lastChangedFiles"]?
                .Values<string?>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? [];
            var hasTransientRetryMetadata = payload["retryNotBeforeUtc"] is not null
                || payload["retryDelaySeconds"] is not null;

            if (!CanFinalizeRetriedForgeArtifacts(
                    job.Attempt,
                    hasTransientRetryMetadata,
                    lastCommitHash,
                    lastChangedFilePaths))
            {
                return false;
            }

            var mainWorkspacePath = payload["mainWorkspacePath"]?.Value<string>()
                ?? payload["workspacePath"]?.Value<string>()
                ?? string.Empty;
            var worktreeKey = payload["worktreeKey"]?.Value<string>()
                ?? (string.IsNullOrWhiteSpace(job.TaskId) ? job.JobId : job.TaskId);

            // lastCommitHash / lastChangedFiles are claims copied between attempts, not evidence. A retained artifact is
            // reused only when the real repository agrees: the retained worktree exists, is clean, belongs to this task and
            // repository, HEAD is the claimed full SHA, the approved plan is unchanged and the diff matches the claim.
            var evidence = await TryVerifyRetainedForgeArtifactAsync(
                    dbContext, job, payload, lastCommitHash, lastChangedFilePaths, worktreeKey, ct)
                .ConfigureAwait(false);
            if (evidence.Manifest is null)
            {
                // Insufficient evidence: never claim completion. Strip the unverified claims so no later fast path
                // (including the one at ForgeWorker start) can finalize from them, and let the normal worker run.
                payload["unverifiedLastCommitHash"] = lastCommitHash;
                payload["unverifiedLastChangedFiles"] = JArray.FromObject(lastChangedFilePaths);
                payload["retryReuseRejectedReason"] = evidence.Reason;
                payload.Remove("lastCommitHash");
                payload.Remove("lastChangedFiles");
                job.PayloadJson = payload.ToString(Formatting.None);
                _logger.LogWarning(
                    "Retained forge artifact not reusable; running the normal worker. job={JobId} task={TaskId} reason={Reason}",
                    job.JobId,
                    job.TaskId ?? string.Empty,
                    evidence.Reason);
                return false;
            }

            var manifest = evidence.Manifest;
            var retainedWorktreePath = manifest.WorktreePath;
            lastChangedFilePaths = manifest.ChangedPaths.ToArray();

            payload["mainWorkspacePath"] = mainWorkspacePath;
            payload["workspacePath"] = retainedWorktreePath;
            payload["worktreeKey"] = worktreeKey;
            payload["shadowWorktreePath"] = retainedWorktreePath;
            payload["lastCommitHash"] = manifest.HeadCommit;
            payload["lastChangedFiles"] = JArray.FromObject(lastChangedFilePaths);
            payload.Remove("retryNotBeforeUtc");
            payload.Remove("retryDelaySeconds");
            payload["worktreeRetained"] = true;

            var serviceDiagnostics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["finalizedUsingRetriedCommittedChanges"] = true,
                ["finalizedBeforeWorkerStart"] = true,
                ["retainedArtifactVerified"] = true,
                ["artifactId"] = manifest.ArtifactId,
                ["retryAttempt"] = job.Attempt,
                ["transientRetryMetadataPresent"] = hasTransientRetryMetadata,
                ["priorRetryCommitHash"] = manifest.HeadCommit,
                ["priorRetryChangedFileCount"] = lastChangedFilePaths.Length
            };
            var summary = $"ForgeWorker completed by finalizing {lastChangedFilePaths.Length} verified committed change(s) retained from the previous retry attempt.";
            var resultPayload = JsonConvert.SerializeObject(new
            {
                success = true,
                message = summary,
                summary,
                result = "已复用重试前已提交且经 Git 校验的保留工作区变更完成当前任务。",
                workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                durationMs = 0,
                totalRounds = 0,
                totalToolCalls = 0,
                writeToolCalls = 0,
                terminationReason = QueryTerminationReason.NoToolCalls.ToString(),
                detailCode = "RetriedCommittedChangesFinalized",
                worktreePath = retainedWorktreePath,
                workspacePath = retainedWorktreePath,
                changedFiles = lastChangedFilePaths,
                commitHash = manifest.HeadCommit,
                artifactManifest = manifest,
                worktreeRetained = true,
                serviceDiagnostics,
                updatedPayloadJson = payload.ToString(Formatting.None)
            });

            dbContext.JobCheckpoints.Add(CreateCheckpoint(
                job,
                latestCheckpoint,
                "Completed",
                checkpointData: JsonConvert.SerializeObject(new
                {
                    completedAtUtc = DateTime.UtcNow,
                    success = true,
                    finalizedUsingRetriedCommittedChanges = true
                })));

            job.Status = BackgroundJobStatus.Completed;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.UpdatedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = null;
            job.PayloadJson = payload.ToString(Formatting.None);
            job.NotificationStatus = suppressNotifications
                ? Domain.Jobs.NotificationStatus.Suppressed
                : Domain.Jobs.NotificationStatus.Pending;
            resultPayload = await AttachJobNotificationXmlAsync(job, resultPayload, ct).ConfigureAwait(false);
            job.ResultPayloadJson = resultPayload;

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                EventType = "JobCompleted",
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                Payload = resultPayload
            });

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _outboxSignal.Pulse();

            _logger.LogInformation(
                "BackgroundJobRunner finalized committed forge retry before worker start. job={JobId} task={TaskId} commit={CommitHash} changedFiles={ChangedFileCount}",
                job.JobId,
                job.TaskId ?? string.Empty,
                lastCommitHash,
                lastChangedFilePaths.Length);

            if (!suppressNotifications)
            {
                await DispatchNotificationImmediatelyAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
                await ContinuePlanAfterCompletedWorkerAsync(job, resultPayload, CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }

        private sealed record RetainedForgeArtifactEvidence(CodexFlow.Contracts.ForgeArtifactManifest? Manifest, string Reason);

        /// <summary>
        /// Verifies, against the real Git repository, that a forge artifact retained from a previous attempt can be reused,
        /// and builds the trusted manifest for it. Any missing/unverifiable evidence returns a rejection (no manifest).
        /// </summary>
        private async Task<RetainedForgeArtifactEvidence> TryVerifyRetainedForgeArtifactAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            JObject payload,
            string? claimedCommitHash,
            IReadOnlyCollection<string> claimedChangedFiles,
            string worktreeKey,
            CancellationToken ct)
        {
            static RetainedForgeArtifactEvidence Reject(string reason) => new(null, reason);

            try
            {
                var sessionId = payload["sessionId"]?.Value<string>();
                var userId = payload["userId"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(userId) ||
                    !string.Equals(sessionId, job.SessionId, StringComparison.Ordinal))
                {
                    return Reject("identity_unbound");
                }

                var payloadTaskId = payload["taskId"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(payloadTaskId) && !string.IsNullOrWhiteSpace(job.TaskId) &&
                    !string.Equals(payloadTaskId, job.TaskId, StringComparison.Ordinal))
                {
                    return Reject("task_mismatch");
                }

                SessionRecord? owner;
                await using (var sessionScope = _serviceProvider.CreateAsyncScope())
                {
                    owner = await sessionScope.ServiceProvider.GetRequiredService<ISessionDirectory>()
                        .FindAsync(sessionId, ct).ConfigureAwait(false);
                }

                if (owner is null || !string.Equals(owner.UserId, userId, StringComparison.Ordinal))
                {
                    return Reject("session_not_owned_by_user");
                }

                if (claimedCommitHash is not { Length: 40 or 64 } || !claimedCommitHash.All(Uri.IsHexDigit))
                {
                    return Reject("commit_not_full_sha");
                }

                var mainWorkspace = payload["mainWorkspacePath"]?.Value<string>() ?? payload["workspacePath"]?.Value<string>();
                var retainedWorktree = payload["shadowWorktreePath"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(mainWorkspace) || !Path.IsPathRooted(mainWorkspace) || !Directory.Exists(mainWorkspace) ||
                    string.IsNullOrWhiteSpace(retainedWorktree) || !Path.IsPathRooted(retainedWorktree) || !Directory.Exists(retainedWorktree))
                {
                    return Reject("retained_worktree_missing");
                }

                await using var scope = _serviceProvider.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                var git = sp.GetService<IGitService>();
                var planStore = sp.GetService<IPlanArtifactStore>();
                var sessionManager = sp.GetService<CodexSessionManager>();
                if (git is null || planStore is null || sessionManager is null)
                {
                    return Reject("verification_services_unavailable");
                }

                // The retained worktree is the one created for this task (branch task-<key>), not another task's directory.
                var branch = await git.GetCurrentBranchAsync(retainedWorktree).ConfigureAwait(false);
                if (!string.Equals(branch, $"task-{worktreeKey}", StringComparison.Ordinal))
                {
                    return Reject("worktree_not_bound_to_task");
                }

                // Same approved plan as the dispatch that created this job.
                var planId = payload["planArtifactId"]?.Value<string>();
                var planVersion = payload["planVersion"]?.Value<string>();
                var planHash = payload["planHash"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(planId) || string.IsNullOrWhiteSpace(planVersion) || string.IsNullOrWhiteSpace(planHash) ||
                    payload["requiredTaskIds"] is not JArray { Count: > 0 })
                {
                    return Reject("plan_binding_missing");
                }

                var plan = await planStore.GetCurrentAsync(sessionId, ct).ConfigureAwait(false);
                if (plan is null || plan.SessionId != sessionId || plan.Id != planId || plan.Version != planVersion ||
                    !string.Equals(CodexFlow.Core.Planning.Artifacts.PlanArtifactSupport.ComputeMarkdownHash(plan.Markdown), planHash, StringComparison.Ordinal))
                {
                    return Reject("plan_changed");
                }

                if (plan.Approval is null || plan.Approval.ApprovedByUserId != userId ||
                    !string.Equals(plan.Approval.ApprovedMarkdownHash, planHash, StringComparison.Ordinal) ||
                    plan.Approval.ApprovedPlanVersion != planVersion ||
                    plan.Status is CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.Rejected
                        or CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.Superseded
                        or CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.Failed
                        or CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.Draft
                        or CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.NeedsClarification
                        or CodexFlow.Core.Planning.Artifacts.PlanArtifactStatus.ReadyForApproval)
                {
                    return Reject("plan_not_approved");
                }

                var baseline = await git.GetHeadHashAsync(mainWorkspace).ConfigureAwait(false);
                if (baseline is not { Length: 40 or 64 } || !baseline.All(Uri.IsHexDigit))
                {
                    return Reject("baseline_unavailable");
                }

                var session = await sessionManager
                    .GetOrCreateSessionAsync(sessionId, userId, mainWorkspace, (Uri?)null)
                    .ConfigureAwait(false);
                var plannedTask = session.Plan.FirstOrDefault(task =>
                    string.Equals(task.Id, job.TaskId, StringComparison.OrdinalIgnoreCase));
                var taskFileScope = plannedTask is null ? null : TaskFileScopeGuard.BuildTaskFileScope(plannedTask, mainWorkspace);
                AddHostKnownDependencyManifests(sp, sessionId, plannedTask, taskFileScope);

                var manifest = await ForgeArtifactManifestFactory.CreateAsync(
                        sp, job, userId, sessionId, mainWorkspace, retainedWorktree, baseline, claimedCommitHash,
                        taskFileScope, git, ct)
                    .ConfigureAwait(false);
                if (manifest is null)
                {
                    return Reject("manifest_unavailable");
                }

                var db = sp.GetRequiredService<IJobDbContext>();
                var parentManifests = (await db.BackgroundJobs.AsNoTracking()
                        .Where(x => x.SessionId == sessionId && x.JobId != job.JobId && x.Status == BackgroundJobStatus.Completed)
                        .Select(x => x.ResultPayloadJson)
                        .ToListAsync(ct).ConfigureAwait(false))
                    .Select(ForgeArtifactManifestFactory.ParseManifest)
                    .OfType<CodexFlow.Contracts.ForgeArtifactManifest>()
                    .Where(x => manifest.ParentArtifactIds.Contains(x.ArtifactId, StringComparer.Ordinal))
                    .ToArray();

                // Real-Git checks: worktree identity/top-level, HEAD == claimed SHA, clean tree, base is an ancestor,
                // every required parent is an ancestor, and the diff equals the manifest's changed paths.
                var error = await new CodexFlow.Core.Services.GitArtifactInspector()
                    .ValidateAsync(manifest, mainWorkspace, userId, sessionId, job.JobId, job.Attempt, parentManifests, ct)
                    .ConfigureAwait(false);
                if (error is not null)
                {
                    return Reject($"git_{error}");
                }

                var claimed = claimedChangedFiles.Order(StringComparer.Ordinal);
                if (!claimed.SequenceEqual(manifest.ChangedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    return Reject("claimed_changes_mismatch");
                }

                return new RetainedForgeArtifactEvidence(manifest, "verified");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or
                ArgumentException or System.ComponentModel.Win32Exception or Newtonsoft.Json.JsonException)
            {
                return Reject("retained_artifact_check_failed");
            }
        }

        private async Task<bool> TryPreserveQueuedRetryAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            JObject? resultObj,
            JobCheckpoint? latestCheckpoint,
            bool suppressNotifications,
            CancellationToken ct)
        {
            var updatedPayload = TryParsePayload(resultObj?["updatedPayloadJson"]?.Value<string>());
            try
            {
                await dbContext.Entry(job).ReloadAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The job may have been detached in a test or unusual recovery path; fall back to the tracked values.
            }

            if (HasFinalizableCommittedForgeRetryPayload(updatedPayload, job.Attempt))
            {
                job.PayloadJson = updatedPayload!.ToString(Formatting.None);
                if (await TryFinalizeCommittedForgeRetryBeforeStartAsync(
                        dbContext,
                        job,
                        latestCheckpoint,
                        suppressNotifications,
                        ct).ConfigureAwait(false))
                {
                    return true;
                }
            }

            if (await TryFinalizeCommittedForgeRetryBeforeStartAsync(
                    dbContext,
                    job,
                    latestCheckpoint,
                    suppressNotifications,
                    ct).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryDeferRetryNotBeforeAsync(dbContext, job, latestCheckpoint, ct).ConfigureAwait(false))
            {
                return true;
            }

            if (job.Status != BackgroundJobStatus.Running)
            {
                _logger.LogInformation(
                    "Skipping terminal failure update because persisted job status changed. job={JobId} status={Status}",
                    job.JobId,
                    job.Status);
                return true;
            }

            var updatedRetryNotBeforeUtc = TryGetRetryNotBeforeUtc(updatedPayload);
            if (updatedRetryNotBeforeUtc == null || updatedRetryNotBeforeUtc <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            job.PayloadJson = updatedPayload!.ToString(Formatting.None);
            return await TryDeferRetryNotBeforeAsync(dbContext, job, latestCheckpoint, ct).ConfigureAwait(false);
        }

        private static DateTimeOffset? TryGetRetryNotBeforeUtc(JObject? payload)
        {
            var value = payload?["retryNotBeforeUtc"]?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTimeOffset.TryParse(value, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }

        private static DateTimeOffset? TryGetCreatedAtNotBeforeUtc(BackgroundJob job)
        {
            if (job.CreatedAtUtc <= DateTime.UtcNow)
            {
                return null;
            }

            return new DateTimeOffset(
                DateTime.SpecifyKind(job.CreatedAtUtc, DateTimeKind.Utc));
        }

        private static string TrimForLog(string value, int maxChars)
            => value.Length <= maxChars ? value : value[..maxChars] + "...";

        private static bool ShouldEnterWaitingUser(string payloadJson, JobCheckpoint? latestCheckpoint)
        {
            if (latestCheckpoint?.CurrentStep is "ResumedFromWaitingUser" or "RecoveryResumed")
            {
                return false;
            }

            var payload = TryParsePayload(payloadJson);
            return payload?["requiresUserConfirmation"]?.Value<bool>() == true;
        }

        private async Task EnterWaitingUserAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            JobCheckpoint? latestCheckpoint,
            bool suppressNotifications,
            CancellationToken ct)
        {
            var resumeToken = Guid.NewGuid().ToString("N");
            var payload = TryParsePayload(job.PayloadJson);
            var waitingReason = payload?["waitingReason"]?.Value<string>() ?? "User confirmation required";
            var waitingContext = ResolveWaitingUserContext(payload);

            dbContext.JobCheckpoints.Add(CreateCheckpoint(
                job,
                latestCheckpoint,
                "AwaitingUserConfirmation",
                pendingActionType: "UserConfirmation",
                pendingPayloadJson: job.PayloadJson,
                resumeToken: resumeToken,
                checkpointData: JsonConvert.SerializeObject(new
                {
                    waitingReason,
                    resumeToken
                })));

            job.Status = BackgroundJobStatus.WaitingUser;
            job.WaitingReason = waitingReason;
            job.UpdatedAtUtc = DateTime.UtcNow;
            var waitingPayload = await AttachWaitingUserNotificationXmlAsync(job, waitingReason, resumeToken, waitingContext, ct);

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                EventType = "JobWaitingUser",
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                Payload = waitingPayload
            });

            await dbContext.SaveChangesAsync(ct);
            _outboxSignal.Pulse();
            if (!suppressNotifications)
            {
                await DispatchNotificationImmediatelyAsync(job.JobId, CancellationToken.None);
            }
        }

        private async Task DispatchNotificationImmediatelyAsync(string jobId, CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
                await dispatcher.DispatchAsync(jobId, ct, "immediate");
            }
            catch (Exception ex)
            {
                // Immediate dispatch is best-effort. The heartbeat scanner remains the fallback path.
                _logger.LogWarning(ex, "Immediate notification dispatch failed for job {JobId}; heartbeat fallback will retry", jobId);
            }
        }

        private static async Task<JobCheckpoint?> GetLatestCheckpointAsync(IJobDbContext dbContext, string jobId, CancellationToken ct)
        {
            return await dbContext.JobCheckpoints
                .Where(c => c.JobId == jobId)
                .OrderByDescending(c => c.SequenceNumber)
                .FirstOrDefaultAsync(ct);
        }

        private static JobCheckpoint CreateCheckpoint(
            BackgroundJob job,
            JobCheckpoint? latestCheckpoint,
            string currentStep,
            string? pendingActionType = null,
            string? pendingPayloadJson = null,
            string? resumeToken = null,
            string? checkpointData = null)
        {
            return new JobCheckpoint
            {
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                SequenceNumber = (latestCheckpoint?.SequenceNumber ?? -1) + 1,
                CurrentStep = currentStep,
                PendingActionType = pendingActionType,
                PendingPayloadJson = pendingPayloadJson,
                ResumeToken = resumeToken,
                Attempt = job.Attempt,
                LastEventSeq = job.LastEventSeq,
                CheckpointData = checkpointData ?? "{}"
            };
        }

        private static JObject? TryParsePayload(string? payloadJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(payloadJson) ? null : JObject.Parse(payloadJson);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> AttachJobNotificationXmlAsync(BackgroundJob job, string? payloadJson, CancellationToken ct)
        {
            var payload = TryParsePayload(payloadJson ?? "{}") ?? new JObject();
            WorkerHookResult hookResult = job.Status switch
            {
                BackgroundJobStatus.Completed => await _workerHookDispatcher.DispatchWorkerCompletedAsync(
                    new WorkerCompletedContext
                    {
                        Job = job,
                        PayloadJson = payload.ToString(Formatting.None)
                    },
                    ct),
                _ => await _workerHookDispatcher.DispatchWorkerFailedAsync(
                    new WorkerFailedContext
                    {
                        Job = job,
                        PayloadJson = payload.ToString(Formatting.None)
                    },
                    ct)
            };

            if (!string.IsNullOrWhiteSpace(hookResult.WorkerNotificationXml))
            {
                payload["workerNotificationXml"] = hookResult.WorkerNotificationXml;
            }

            return payload.ToString(Formatting.None);
        }

        private async Task<string> AttachWaitingUserNotificationXmlAsync(
            BackgroundJob job,
            string waitingReason,
            string resumeToken,
            string? waitingContext,
            CancellationToken ct)
        {
            var payload = new JObject
            {
                ["waitingReason"] = waitingReason,
                ["resumeToken"] = resumeToken,
                ["summary"] = waitingReason
            };

            if (!string.IsNullOrWhiteSpace(waitingContext))
            {
                payload["context"] = waitingContext;
            }

            var hookResult = await _workerHookDispatcher.DispatchWorkerWaitingUserAsync(
                new WorkerWaitingUserContext
                {
                    Job = job,
                    PayloadJson = payload.ToString(Formatting.None),
                    ResumeToken = resumeToken,
                    Reason = waitingReason,
                    Context = waitingContext
                },
                ct);

            if (!string.IsNullOrWhiteSpace(hookResult.WorkerNotificationXml))
            {
                payload["workerNotificationXml"] = hookResult.WorkerNotificationXml;
            }

            return payload.ToString(Formatting.None);
        }

        private static string? ResolveWaitingUserContext(JObject? payload)
        {
            if (payload == null)
            {
                return null;
            }

            var context = payload["waitingContext"]?.Value<string>()
                ?? payload["context"]?.Value<string>()
                ?? payload["message"]?.Value<string>();

            return string.IsNullOrWhiteSpace(context) ? null : context;
        }

        private async Task<string> ExecuteReadOnlyWorkerAsync(
            IServiceProvider sp,
            BackgroundJob job,
            WorkerType workerType,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var payload = TryParsePayload(job.PayloadJson);
            var sessionId = payload?["sessionId"]?.Value<string>() ?? job.SessionId ?? string.Empty;
            var userId = payload?["userId"]?.Value<string>() ?? string.Empty;
            var workspacePath = payload?["workspacePath"]?.Value<string>() ?? string.Empty;
            var prompt = payload?["prompt"]?.Value<string>()
                         ?? payload?["message"]?.Value<string>()
                         ?? GetDefaultWorkerPrompt(workerType);
            var maxRounds = Math.Clamp(
                payload?["maxRounds"]?.Value<int?>() ?? DefaultReadOnlyWorkerMaxRounds,
                1,
                MaxReadOnlyWorkerMaxRounds);

            var serviceDiagnostics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var engine = ResolveRequiredService<IQueryRuntimeEngine>(sp, serviceDiagnostics);
                var toolRegistry = ResolveRequiredService<IToolRegistry>(sp, serviceDiagnostics);
                var sessionManager = ResolveRequiredService<CodexSessionManager>(sp, serviceDiagnostics);
                var workerDefinitions = ResolveRequiredService<IWorkerDefinitionRegistry>(sp, serviceDiagnostics);
                _ = TryResolveOptionalService<IChatClient>(sp, serviceDiagnostics);

                var bootstrapper = TryResolveOptionalService<ToolRegistryBootstrapper>(sp, serviceDiagnostics);
                bootstrapper?.EnsureRegistered(toolRegistry);

                var session = await sessionManager
                    .GetOrCreateSessionAsync(sessionId, userId, workspacePath, (Uri?)null)
                    .ConfigureAwait(false);
                var workerSession = WorkerExecutionSessionFactory.CreateForWorker(session, workerType);

                var workerDefinition = workerDefinitions.GetRequired(workerType);
                ActivateWorkerDeferredTools(toolRegistry, workerDefinition);
                // The host already resolved this session's dependency upgrade: the worker gets that answer and no web tools to second-guess it with.
                var fetchedUpgrades = sp.GetService<CodexFlow.Services.ArtifactAudit.IDependencyUpgradeTargets>()?.FetchedUpgrades(sessionId);
                prompt += ReadOnlyWorkerDependencyBriefing.Build(fetchedUpgrades);
                serviceDiagnostics["webToolsRemoved"] = ReadOnlyWorkerDependencyBriefing.Applies(fetchedUpgrades);
                IReadOnlyList<ICodexTool> BuildAvailableCodexTools(bool includeDeferred = false)
                    => ReadOnlyWorkerDependencyBriefing.FilterTools(workerDefinitions
                        .FilterAvailableTools(workerType, (includeDeferred ? toolRegistry.GetAvailableTools(workerSession, true) : toolRegistry.GetAvailableTools(workerSession)))
                        .ToList(), fetchedUpgrades);

                IReadOnlyList<AIFunction> BuildAvailableTools()
                    => BuildAvailableCodexTools()
                        .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                        .Cast<AIFunction>()
                        .ToList();

                var availableCodexTools = BuildAvailableCodexTools();
                var availableTools = availableCodexTools
                    .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                    .Cast<AIFunction>()
                    .ToList();
                var runtimeContext = workerDefinitions.BuildRuntimeContext(workerType);
                var systemPrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
                    workerDefinition.BuildSystemPrompt(workerSession),
                    availableCodexTools,
                    runtimeContext);
                serviceDiagnostics["availableToolCount"] = availableTools.Count;
                serviceDiagnostics["availableToolNames"] = availableTools.Select(t => t.Name).ToArray();

                var request = new QueryRuntimeRequest
                {
                    SessionId = session.Id,
                    EntryPoint = WorkerJobTypes.ToEntryPoint(workerType),
                    InitialMessages =
                    [
                        new ChatMessage(
                            ChatRole.System,
                            systemPrompt),
                        new ChatMessage(ChatRole.User, prompt)
                    ],
                    Session = workerSession,
                    Scenario = Core.Models.MemoryInjectionScenario.Chat,
                    MaxRounds = maxRounds,
                    EnableTools = availableTools.Count > 0,
                    AvailableTools = availableTools,
                    AvailableToolsProvider = BuildAvailableTools,
                    FrozenToolCatalog = BuildAvailableCodexTools(includeDeferred: true).Select(CodexToolFunctionAdapterFactory.CreateAIFunction).ToArray(),
                    AvailableCodexTools = availableCodexTools,
                    AvailableCodexToolsProvider = () => BuildAvailableCodexTools(),
                    WorkerContext = runtimeContext,
                    RequiredToolContract = runtimeContext.RequiredToolContract,
                    AdapterHints = new AdapterHints(
                        EnableEmptyResponseRecovery: true,
                        EnableMalformedProtocolRecovery: true,
                        EnableTransportFailureRecovery: true,
                        EnableStallDetection: true,
                        EnableToolDeduplication: true,
                        MaxRecoveryAttempts: 3)
                };

                var loggingSink = new LoggingQueryRuntimeEventSink(_logger);
                var sink = CreateRuntimeEventSink(sp, sessionId, job.JobId, job.TaskId, _logger, loggingSink);
                var result = await engine.ExecuteAsync(request, sink, ct).ConfigureAwait(false);
                await RecordTokenUsageAsync(sp, userId, result.TotalPromptTokens, result.TotalCompletionTokens, result.TotalCachedPromptTokens).ConfigureAwait(false);

                serviceDiagnostics["runtimeEventCount"] = loggingSink.EventCount;
                serviceDiagnostics["runtimeEventTypes"] = loggingSink.EventTypes.ToArray();

                if (ShouldMarkRecoveryNeeded(result))
                {
                    return BuildRecoveryNeededPayload(
                        workerType: workerType.ToString().ToLowerInvariant(),
                        summary: BuildRecoveryNeededSummary(workerType, result),
                        resultText: result.FinalText,
                        durationMs: started.ElapsedMilliseconds,
                        totalRounds: result.TotalRounds,
                        totalToolCalls: result.TotalToolCalls,
                        terminationReason: result.TerminationReason.ToString(),
                        detailCode: result.TerminalDetailCode,
                        recoveryReason: ResolveRecoveryReason(result),
                        runtimeFlags: GetRuntimeFlags(result.Flags),
                        serviceDiagnostics: serviceDiagnostics);
                }

                var success = !IsTechnicalRuntimeFailure(result);
                RecordProviderParameterFailureIfPresent(result, serviceDiagnostics);
                var summary = BuildReadOnlyWorkerSummary(workerType, result);

                return JsonConvert.SerializeObject(new
                {
                    success,
                    message = summary,
                    summary,
                    result = result.FinalText,
                    workerType = workerType.ToString().ToLowerInvariant(),
                    durationMs = started.ElapsedMilliseconds,
                    totalRounds = result.TotalRounds,
                    totalToolCalls = result.TotalToolCalls,
                    terminationReason = result.TerminationReason.ToString(),
                    detailCode = result.TerminalDetailCode,
                    serviceDiagnostics
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackgroundJobRunner: {WorkerType} threw. job={JobId} session={SessionId}", workerType, job.JobId, sessionId);
                serviceDiagnostics["exception"] = ex.GetType().Name;

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = ex.Message,
                    workerType = workerType.ToString().ToLowerInvariant(),
                    durationMs = started.ElapsedMilliseconds,
                    serviceDiagnostics
                });
            }
        }

        private async Task<string> ExecuteVerifyWorkerAsync(
            IServiceProvider sp,
            BackgroundJob job,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var payload = TryParsePayload(job.PayloadJson) ?? new JObject();
            var sessionId = payload["sessionId"]?.Value<string>() ?? job.SessionId ?? string.Empty;
            var userId = payload["userId"]?.Value<string>() ?? string.Empty;
            var workspacePath = payload["workspacePath"]?.Value<string>() ?? string.Empty;
            var prompt = payload["prompt"]?.Value<string>()
                         ?? payload["message"]?.Value<string>()
                         ?? GetDefaultWorkerPrompt(WorkerType.Verify);
            var maxRounds = Math.Clamp(
                payload["maxRounds"]?.Value<int?>() ?? DefaultReadOnlyWorkerMaxRounds,
                1,
                MaxReadOnlyWorkerMaxRounds);

            var serviceDiagnostics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var engine = ResolveRequiredService<IQueryRuntimeEngine>(sp, serviceDiagnostics);
                var toolRegistry = ResolveRequiredService<IToolRegistry>(sp, serviceDiagnostics);
                var sessionManager = ResolveRequiredService<CodexSessionManager>(sp, serviceDiagnostics);
                var workerDefinitions = ResolveRequiredService<IWorkerDefinitionRegistry>(sp, serviceDiagnostics);
                _ = TryResolveOptionalService<IChatClient>(sp, serviceDiagnostics);

                var bootstrapper = TryResolveOptionalService<ToolRegistryBootstrapper>(sp, serviceDiagnostics);
                bootstrapper?.EnsureRegistered(toolRegistry);

                var session = await sessionManager
                    .GetOrCreateSessionAsync(sessionId, userId, workspacePath, (Uri?)null)
                    .ConfigureAwait(false);
                var workerSession = WorkerExecutionSessionFactory.CreateForWorker(session, WorkerType.Verify);

                var workerDefinition = workerDefinitions.GetRequired(WorkerType.Verify);
                ActivateWorkerDeferredTools(toolRegistry, workerDefinition);
                IReadOnlyList<ICodexTool> BuildAvailableCodexTools(bool includeDeferred = false)
                    => workerDefinitions
                        .FilterAvailableTools(WorkerType.Verify, (includeDeferred ? toolRegistry.GetAvailableTools(workerSession, true) : toolRegistry.GetAvailableTools(workerSession)))
                        .ToList();

                IReadOnlyList<AIFunction> BuildAvailableTools()
                    => BuildAvailableCodexTools()
                        .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                        .Cast<AIFunction>()
                        .ToList();

                var availableCodexTools = BuildAvailableCodexTools();
                var availableTools = availableCodexTools
                    .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                    .Cast<AIFunction>()
                    .ToList();
                var runtimeContext = workerDefinitions.BuildRuntimeContext(WorkerType.Verify);
                var systemPrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
                    workerDefinition.BuildSystemPrompt(workerSession),
                    availableCodexTools,
                    runtimeContext);
                serviceDiagnostics["availableToolCount"] = availableTools.Count;
                serviceDiagnostics["availableToolNames"] = availableTools.Select(t => t.Name).ToArray();

                var chatOptions = new VllmChatOptions
                {
                    ThinkingEnabled = true,
                    EnableLegacyToolCallTextFallback = true,
                };

                var request = new QueryRuntimeRequest
                {
                    SessionId = session.Id,
                    EntryPoint = WorkerJobTypes.ToEntryPoint(WorkerType.Verify),
                    InitialMessages =
                    [
                        new ChatMessage(
                            ChatRole.System,
                            systemPrompt),
                        new ChatMessage(ChatRole.User, prompt)
                    ],
                    Session = workerSession,
                    Scenario = Core.Models.MemoryInjectionScenario.Chat,
                    MaxRounds = maxRounds,
                    EnableTools = availableTools.Count > 0,
                    AvailableTools = availableTools,
                    AvailableToolsProvider = BuildAvailableTools,
                    FrozenToolCatalog = BuildAvailableCodexTools(includeDeferred: true).Select(CodexToolFunctionAdapterFactory.CreateAIFunction).ToArray(),
                    AvailableCodexTools = availableCodexTools,
                    AvailableCodexToolsProvider = () => BuildAvailableCodexTools(),
                    WorkerContext = runtimeContext,
                    RequiredToolContract = runtimeContext.RequiredToolContract,
                    Options = chatOptions,
                    AdapterHints = new AdapterHints(
                        EnableEmptyResponseRecovery: true,
                        EnableMalformedProtocolRecovery: true,
                        EnableTransportFailureRecovery: true,
                        EnableStallDetection: true,
                        EnableToolDeduplication: true,
                        MaxRecoveryAttempts: 3)
                };

                var loggingSink = new LoggingQueryRuntimeEventSink(_logger);
                var sink = CreateRuntimeEventSink(sp, sessionId, job.JobId, job.TaskId, _logger, loggingSink);
                var trustedExecution = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ForJob(
                    userId, sessionId, job.JobId, job.TaskId ?? job.JobId, job.Attempt,
                    Path.GetFullPath(workspacePath), baseCommit: null, taskScope: null,
                    deadline: DateTimeOffset.UtcNow.AddMinutes(30));
                var executionAccessor = sp.GetService<CodexFlow.Core.Execution.ITrustedExecutionContextAccessor>()
                    ?? new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
                QueryRuntimeResult result;
                using (executionAccessor.Begin(trustedExecution))
                {
                    result = await engine.ExecuteAsync(request, sink, ct).ConfigureAwait(false);
                }
                await RecordTokenUsageAsync(sp, userId, result.TotalPromptTokens, result.TotalCompletionTokens, result.TotalCachedPromptTokens).ConfigureAwait(false);

                serviceDiagnostics["runtimeEventCount"] = loggingSink.EventCount;
                serviceDiagnostics["runtimeEventTypes"] = loggingSink.EventTypes.ToArray();

                if (ShouldMarkRecoveryNeeded(result))
                {
                    return BuildRecoveryNeededPayload(
                        workerType: WorkerType.Verify.ToString().ToLowerInvariant(),
                        summary: BuildRecoveryNeededSummary(WorkerType.Verify, result),
                        resultText: result.FinalText,
                        durationMs: started.ElapsedMilliseconds,
                        totalRounds: result.TotalRounds,
                        totalToolCalls: result.TotalToolCalls,
                        terminationReason: result.TerminationReason.ToString(),
                        detailCode: result.TerminalDetailCode,
                        recoveryReason: ResolveRecoveryReason(result),
                        runtimeFlags: GetRuntimeFlags(result.Flags),
                        serviceDiagnostics: serviceDiagnostics,
                        extraFields: new JObject
                        {
                            ["writeToolCalls"] = result.WriteToolCalls,
                            ["inputTokens"] = result.TotalPromptTokens,
                            ["cachedInputTokens"] = result.TotalCachedPromptTokens,
                            ["outputTokens"] = result.TotalCompletionTokens
                        });
                }

                // Technical runtime failure (exception, context hard limit, stall, empty-response fallback) is a job
                // failure; a genuine business FAIL verdict from a healthy run keeps success=true further below.
                if (IsTechnicalRuntimeFailure(result))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        message = $"VerifyWorker runtime terminated with {result.TerminationReason}.",
                        workerType = WorkerType.Verify.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        totalRounds = result.TotalRounds,
                        totalToolCalls = result.TotalToolCalls,
                        terminationReason = result.TerminationReason.ToString(),
                        detailCode = result.TerminalDetailCode,
                        serviceDiagnostics
                    });
                }

                var verificationReport = ParseVerificationReport(result.FinalText, job.TaskId ?? job.JobId, job.JobId);
                if (!verificationReport.HasCommandEvidence)
                {
                    verificationReport = verificationReport.WithIssue(
                        "验证报告缺少命令证据，自动降级为 FAIL。",
                        forcedVerdict: "fail");
                }

                if (!verificationReport.HasHappyPathEvidence)
                {
                    verificationReport = verificationReport.WithIssue(
                        "验证报告缺少 happy_path:* 证据，自动降级为 FAIL。",
                        forcedVerdict: "fail");
                }

                if (!verificationReport.HasAdversarialProbeEvidence)
                {
                    verificationReport = verificationReport.WithIssue(
                        "验证报告缺少 adversarial_probe:* 证据，自动降级为 FAIL。",
                        forcedVerdict: "fail");
                }

                var summary = verificationReport.Summary;
                var verificationChecklist = BuildVerificationChecklist(verificationReport);
                payload["verificationVerdict"] = verificationReport.Verdict;
                payload["verificationPassed"] = verificationReport.Passed;
                payload["verificationSummary"] = summary;
                payload["verificationIssues"] = JArray.FromObject(verificationReport.Issues);
                payload["verificationEvidence"] = JArray.FromObject(
                    verificationReport.Evidence.Select(e => new
                    {
                        check = e.Check,
                        passed = e.Passed,
                        command = e.Command,
                        exitCode = e.ExitCode,
                        observation = e.Observation
                    }));
                payload["verificationChecklist"] = JObject.FromObject(new
                {
                    completedItems = verificationChecklist.CompletedItems.Select(item => new
                    {
                        check = item.Check,
                        evidence = item.Evidence.ToArray()
                    }),
                    failedItems = verificationChecklist.FailedItems.Select(item => new
                    {
                        check = item.Check,
                        evidence = item.Evidence.ToArray()
                    }),
                    pendingItems = verificationChecklist.PendingItems.Select(item => new
                    {
                        check = item.Check,
                        evidence = item.Evidence.ToArray()
                    }),
                    focusItems = verificationChecklist.FocusItems.ToArray()
                });

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"VerifyWorker concluded {verificationReport.Verdict.ToUpperInvariant()} with {verificationReport.Evidence.Count} evidence item(s).",
                    summary,
                    result = result.FinalText,
                    workerType = WorkerType.Verify.ToString().ToLowerInvariant(),
                    durationMs = started.ElapsedMilliseconds,
                    totalRounds = result.TotalRounds,
                    totalToolCalls = result.TotalToolCalls,
                    writeToolCalls = result.WriteToolCalls,
                    inputTokens = result.TotalPromptTokens,
                    cachedInputTokens = result.TotalCachedPromptTokens,
                    outputTokens = result.TotalCompletionTokens,
                    terminationReason = result.TerminationReason.ToString(),
                    detailCode = result.TerminalDetailCode,
                    verificationVerdict = verificationReport.Verdict,
                    verificationPassed = verificationReport.Passed,
                    verificationSummary = summary,
                    verificationIssues = verificationReport.Issues,
                    verificationEvidence = verificationReport.Evidence.Select(e => new
                    {
                        check = e.Check,
                        passed = e.Passed,
                        command = e.Command,
                        exitCode = e.ExitCode,
                        observation = e.Observation
                    }).ToArray(),
                    verificationChecklist = new
                    {
                        completedItems = verificationChecklist.CompletedItems.Select(item => new
                        {
                            check = item.Check,
                            evidence = item.Evidence.ToArray()
                        }).ToArray(),
                        failedItems = verificationChecklist.FailedItems.Select(item => new
                        {
                            check = item.Check,
                            evidence = item.Evidence.ToArray()
                        }).ToArray(),
                        pendingItems = verificationChecklist.PendingItems.Select(item => new
                        {
                            check = item.Check,
                            evidence = item.Evidence.ToArray()
                        }).ToArray(),
                        focusItems = verificationChecklist.FocusItems.ToArray()
                    },
                    serviceDiagnostics
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackgroundJobRunner: VerifyWorker threw. job={JobId} session={SessionId}", job.JobId, sessionId);
                serviceDiagnostics["exception"] = ex.GetType().Name;

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = ex.Message,
                    workerType = WorkerType.Verify.ToString().ToLowerInvariant(),
                    durationMs = started.ElapsedMilliseconds,
                    serviceDiagnostics
                });
            }
        }

        private async Task<string> ExecuteForgeWorkerAsync(
            IServiceProvider sp,
            BackgroundJob job,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var payload = TryParsePayload(job.PayloadJson) ?? new JObject();
            var sessionId = payload["sessionId"]?.Value<string>() ?? job.SessionId ?? string.Empty;
            var userId = payload["userId"]?.Value<string>() ?? string.Empty;
            var mainWorkspacePath = payload["mainWorkspacePath"]?.Value<string>()
                ?? payload["workspacePath"]?.Value<string>()
                ?? string.Empty;
            var prompt = payload["prompt"]?.Value<string>()
                ?? payload["message"]?.Value<string>()
                ?? "请在隔离工作区中完成当前实现任务。";
            var maxRounds = Math.Clamp(
                payload["maxRounds"]?.Value<int?>() ?? DefaultForgeWorkerMaxRounds,
                1,
                MaxForgeWorkerMaxRounds);
            var worktreeKey = payload["worktreeKey"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(worktreeKey))
            {
                worktreeKey = string.IsNullOrWhiteSpace(job.TaskId) ? job.JobId : job.TaskId;
                payload["worktreeKey"] = worktreeKey;
            }

            var serviceDiagnostics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var priorResultPayload = TryParsePayload(job.ResultPayloadJson);
            var continuedFromJobId = payload["continuedFromJobId"]?.Value<string>();
            var lastCommitHash = payload["lastCommitHash"]?.Value<string>();
            var lastChangedFilePaths = payload["lastChangedFiles"]?
                .Values<string?>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? [];
            var priorCommitHash = priorResultPayload?["commitHash"]?.Value<string>()
                ?? payload["previousCommitHash"]?.Value<string>()
                ?? lastCommitHash;
            var priorChangedFilePaths = priorResultPayload?["changedFiles"]?
                .Values<string?>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? payload["previousChangedFiles"]?
                    .Values<string?>()
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Select(static path => path!.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                ?? lastChangedFilePaths;
            string? shadowWorkspacePath = payload["shadowWorktreePath"]?.Value<string>();
            var retainedShadowWorktreeAvailable = !string.IsNullOrWhiteSpace(shadowWorkspacePath) &&
                                                  Directory.Exists(shadowWorkspacePath);
            var activeWorkspacePath = mainWorkspacePath;
            var usingShadowWorktree = retainedShadowWorktreeAvailable;
            if (usingShadowWorktree)
            {
                activeWorkspacePath = shadowWorkspacePath!;
            }

            var keepShadowOnFailure = false;
            var keepShadowOnInfrastructureError = true;

            try
            {
                var hasTransientRetryMetadata = payload["retryNotBeforeUtc"] is not null
                    || payload["retryDelaySeconds"] is not null;
                if (CanFinalizeRetriedForgeArtifacts(
                        job.Attempt,
                        hasTransientRetryMetadata,
                        lastCommitHash,
                        lastChangedFilePaths))
                {
                    serviceDiagnostics["finalizedUsingRetriedCommittedChanges"] = true;
                    serviceDiagnostics["retryAttempt"] = job.Attempt;
                    serviceDiagnostics["transientRetryMetadataPresent"] = hasTransientRetryMetadata;
                    serviceDiagnostics["priorRetryCommitHash"] = lastCommitHash;
                    serviceDiagnostics["priorRetryChangedFileCount"] = lastChangedFilePaths.Length;

                    payload["mainWorkspacePath"] = mainWorkspacePath;
                    payload["workspacePath"] = mainWorkspacePath;
                    payload["worktreeKey"] = worktreeKey;
                    payload["lastCommitHash"] = lastCommitHash;
                    payload["lastChangedFiles"] = JArray.FromObject(lastChangedFilePaths);
                    payload.Remove("shadowWorktreePath");
                    payload.Remove("retryNotBeforeUtc");
                    payload.Remove("retryDelaySeconds");
                    payload["worktreeRetained"] = false;

                    var summary = $"ForgeWorker completed by finalizing {lastChangedFilePaths.Length} committed change(s) from the previous retry attempt.";
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = summary,
                        summary,
                        result = "已复用重试前已提交的工作区变更完成当前任务。",
                        workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        totalRounds = 0,
                        totalToolCalls = 0,
                        writeToolCalls = 0,
                        terminationReason = QueryTerminationReason.NoToolCalls.ToString(),
                        detailCode = "RetriedCommittedChangesFinalized",
                        worktreePath = (string?)null,
                        workspacePath = mainWorkspacePath,
                        changedFiles = lastChangedFilePaths,
                        commitHash = lastCommitHash,
                        worktreeRetained = false,
                        serviceDiagnostics,
                        updatedPayloadJson = payload.ToString(Formatting.None)
                    });
                }

                if ((string.IsNullOrWhiteSpace(mainWorkspacePath) || !Directory.Exists(mainWorkspacePath)) &&
                    !retainedShadowWorktreeAvailable)
                {
                    serviceDiagnostics["workspaceUnavailable"] = true;
                    serviceDiagnostics["mainWorkspacePath"] = mainWorkspacePath;
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        message = $"Forge worker workspace is unavailable: {mainWorkspacePath}",
                        workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        detailCode = $"WorkspaceUnavailable: {mainWorkspacePath}",
                        worktreePath = (string?)null,
                        workspacePath = mainWorkspacePath,
                        worktreeRetained = false,
                        serviceDiagnostics,
                        updatedPayloadJson = payload.ToString(Formatting.None)
                    });
                }

                if (retainedShadowWorktreeAvailable && !Directory.Exists(mainWorkspacePath))
                {
                    serviceDiagnostics["mainWorkspaceUnavailableUsingRetainedShadow"] = true;
                    serviceDiagnostics["retainedShadowWorktreePath"] = shadowWorkspacePath;
                }

                var engine = ResolveRequiredService<IQueryRuntimeEngine>(sp, serviceDiagnostics);
                var toolRegistry = ResolveRequiredService<IToolRegistry>(sp, serviceDiagnostics);
                var sessionManager = ResolveRequiredService<CodexSessionManager>(sp, serviceDiagnostics);
                var workerDefinitions = ResolveRequiredService<IWorkerDefinitionRegistry>(sp, serviceDiagnostics);
                var gitService = ResolveRequiredService<IGitService>(sp, serviceDiagnostics);
                var configuration = TryResolveOptionalService<IConfiguration>(sp, serviceDiagnostics);
                _ = TryResolveOptionalService<IChatClient>(sp, serviceDiagnostics);

                var bootstrapper = TryResolveOptionalService<ToolRegistryBootstrapper>(sp, serviceDiagnostics);
                bootstrapper?.EnsureRegistered(toolRegistry);

                var sessionWorkspacePath = retainedShadowWorktreeAvailable
                    ? shadowWorkspacePath!
                    : mainWorkspacePath;
                var session = await sessionManager
                    .GetOrCreateSessionAsync(sessionId, userId, sessionWorkspacePath, (Uri?)null)
                    .ConfigureAwait(false);
                if (session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetRequired, out var resetRequiredRaw) &&
                    bool.TryParse(resetRequiredRaw, out var resetRequired) &&
                    resetRequired)
                {
                    session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetReason, out var resetReason);
                    serviceDiagnostics["planResetRequired"] = true;
                    serviceDiagnostics["planResetReason"] = resetReason;
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        message = string.IsNullOrWhiteSpace(resetReason)
                            ? "当前计划已失效，ForgeWorker 已暂停执行。请先重新生成计划。"
                            : $"当前计划已失效，ForgeWorker 已暂停执行。原因：{resetReason}",
                        workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        detailCode = "PlanResetRequired",
                        worktreePath = (string?)null,
                        workspacePath = mainWorkspacePath,
                        worktreeRetained = false,
                        serviceDiagnostics,
                        updatedPayloadJson = payload.ToString(Formatting.None)
                    });
                }

                if (session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanPatchPending, out var patchPendingRaw) &&
                    bool.TryParse(patchPendingRaw, out var patchPending) &&
                    patchPending)
                {
                    session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanPatchTaskId, out var patchTaskId);
                    session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanPatchReason, out var patchReason);
                    serviceDiagnostics["planPatchPending"] = true;
                    serviceDiagnostics["planPatchTaskId"] = patchTaskId;
                    serviceDiagnostics["planPatchReason"] = patchReason;
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        message = string.IsNullOrWhiteSpace(patchReason)
                            ? "当前计划存在待确认的局部 Plan Patch，ForgeWorker 已暂停所有任务执行。请先确认或清理 Plan Patch。"
                            : $"当前计划存在待确认的局部 Plan Patch，ForgeWorker 已暂停所有任务执行。原因：{patchReason}",
                        workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        detailCode = "PlanPatchPending",
                        worktreePath = (string?)null,
                        workspacePath = mainWorkspacePath,
                        worktreeRetained = false,
                        serviceDiagnostics,
                        updatedPayloadJson = payload.ToString(Formatting.None)
                    });
                }

                var workerSession = WorkerExecutionSessionFactory.CreateForWorker(session, WorkerType.Forge);
                var plannedTask = session.Plan.FirstOrDefault(task =>
                    string.Equals(task.Id, job.TaskId, StringComparison.OrdinalIgnoreCase));

                var branchProbePath = Directory.Exists(mainWorkspacePath)
                    ? mainWorkspacePath
                    : retainedShadowWorktreeAvailable
                        ? shadowWorkspacePath!
                        : mainWorkspacePath;
                var baseBranch = await gitService.GetCurrentBranchAsync(branchProbePath).ConfigureAwait(false) ?? "main";
                var shadowBaseRef = payload["baseRef"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(shadowBaseRef))
                {
                    shadowBaseRef = priorCommitHash;
                }

                if (string.IsNullOrWhiteSpace(shadowBaseRef))
                {
                    shadowBaseRef = baseBranch;
                }

                serviceDiagnostics["baseBranch"] = baseBranch;
                serviceDiagnostics["shadowBaseRef"] = shadowBaseRef;

                var enableShadowWorkspace = configuration?.GetValue<bool?>("Workspace:EnableShadowWorkspace") ?? true;
                var enableTaskFileScopeGuard = configuration?.GetValue<bool?>("Workspace:EnableTaskFileScopeGuard") ?? true;
                keepShadowOnFailure = configuration?.GetValue<bool?>("Workspace:KeepShadowOnFailure") ?? false;
                keepShadowOnInfrastructureError = configuration?.GetValue<bool?>("Workspace:KeepShadowOnInfrastructureError") ?? true;
                serviceDiagnostics["enableShadowWorkspace"] = enableShadowWorkspace;
                serviceDiagnostics["enableTaskFileScopeGuard"] = enableTaskFileScopeGuard;
                serviceDiagnostics["keepShadowOnFailure"] = keepShadowOnFailure;
                serviceDiagnostics["keepShadowOnInfrastructureError"] = keepShadowOnInfrastructureError;
                serviceDiagnostics["plannedTaskFound"] = plannedTask is not null;
                serviceDiagnostics["plannedTaskTitle"] = plannedTask?.Title;
                serviceDiagnostics["continuedFromJobId"] = continuedFromJobId;
                serviceDiagnostics["priorContinuationCommitHash"] = priorCommitHash;
                serviceDiagnostics["priorContinuationChangedFileCount"] = priorChangedFilePaths.Length;

                var taskFileScope = plannedTask is null
                    ? null
                    : TaskFileScopeGuard.BuildTaskFileScope(plannedTask, mainWorkspacePath);
                AddHostKnownDependencyManifests(sp, sessionId, plannedTask, taskFileScope);
                AddHostGrantedRetryManifests(payload, taskFileScope);
                if (taskFileScope is { HasConstraints: true })
                {
                    serviceDiagnostics["taskScopeAllowedFiles"] = taskFileScope.AllowedFiles.ToArray();
                    serviceDiagnostics["taskScopeAllowedFileNames"] = taskFileScope.AllowedFileNames.ToArray();
                    serviceDiagnostics["taskScopeAllowedDirectories"] = taskFileScope.AllowedDirectories.ToArray();
                    serviceDiagnostics["taskScopeDisallowedDirectories"] = taskFileScope.DisallowedDirectories.ToArray();
                }

                if (enableShadowWorkspace)
                {
                    retainedShadowWorktreeAvailable = !string.IsNullOrWhiteSpace(shadowWorkspacePath) &&
                                                      Directory.Exists(shadowWorkspacePath);
                    if (string.IsNullOrWhiteSpace(shadowWorkspacePath) || !Directory.Exists(shadowWorkspacePath))
                    {
                        shadowWorkspacePath = await gitService
                            .CreateShadowWorktreeAsync(mainWorkspacePath, worktreeKey, shadowBaseRef)
                            .ConfigureAwait(false);
                    }

                    if (string.IsNullOrWhiteSpace(shadowWorkspacePath))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            message = "forge worker 无法创建 shadow worktree。",
                            workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                            durationMs = started.ElapsedMilliseconds,
                            serviceDiagnostics
                        });
                    }

                    if (retainedShadowWorktreeAvailable)
                    {
                        // An attempt that ended normally always leaves its retained worktree clean (the runner commits whatever the
                        // attempt changed), so uncommitted files here are residue of an attempt that died mid-write: possibly
                        // truncated and never reviewed. Adopting them would commit them under this attempt, so they are discarded
                        // and the model redoes that work.
                        var residue = await gitService.HasUncommittedChangesAsync(shadowWorkspacePath).ConfigureAwait(false);
                        if (residue == true)
                        {
                            var discarded = await gitService.DiscardUncommittedChangesAsync(shadowWorkspacePath).ConfigureAwait(false);
                            serviceDiagnostics["crashResidueDiscarded"] = discarded;
                            _logger.LogWarning(
                                "BackgroundJobRunner: retained shadow worktree had uncommitted residue from a previous attempt. job={JobId} path={Path} discarded={Discarded}",
                                job.JobId,
                                shadowWorkspacePath,
                                discarded);
                            if (!discarded)
                            {
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    message = "forge worker 无法清理保留的 shadow worktree 中上一次尝试遗留的未提交改动。",
                                    workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                                    durationMs = started.ElapsedMilliseconds,
                                    detailCode = "ShadowWorktreeResidueNotDiscarded",
                                    worktreePath = shadowWorkspacePath,
                                    workspacePath = mainWorkspacePath,
                                    worktreeRetained = true,
                                    serviceDiagnostics,
                                    updatedPayloadJson = payload.ToString(Formatting.None)
                                });
                            }
                        }
                    }

                    activeWorkspacePath = shadowWorkspacePath;
                    usingShadowWorktree = true;
                }
                else
                {
                    shadowWorkspacePath = null;
                    activeWorkspacePath = mainWorkspacePath;
                    usingShadowWorktree = false;
                    payload.Remove("shadowWorktreePath");
                }

                payload["mainWorkspacePath"] = mainWorkspacePath;
                payload["workspacePath"] = activeWorkspacePath;
                payload["worktreeKey"] = worktreeKey;
                if (usingShadowWorktree)
                {
                    payload["shadowWorktreePath"] = shadowWorkspacePath;
                }
                else
                {
                    payload.Remove("shadowWorktreePath");
                }

                var workerDefinition = workerDefinitions.GetRequired(WorkerType.Forge);
                ActivateWorkerDeferredTools(toolRegistry, workerDefinition);
                var runtimeContext = workerDefinitions.BuildRuntimeContext(WorkerType.Forge);
                if (!usingShadowWorktree)
                {
                    runtimeContext = runtimeContext with { IsolationMode = WorkerIsolationMode.Workspace };
                }

                var originalWorkspacePath = workerSession.WorkspacePath;
                workerSession.WorkspacePath = activeWorkspacePath;

                try
                {
                    IReadOnlyList<ICodexTool> BuildAvailableCodexTools(bool includeDeferred = false)
                        => workerDefinitions
                            .FilterAvailableTools(WorkerType.Forge, (includeDeferred ? toolRegistry.GetAvailableTools(workerSession, true) : toolRegistry.GetAvailableTools(workerSession)))
                            .ToList();

                    IReadOnlyList<AIFunction> BuildAvailableTools()
                        => BuildAvailableCodexTools()
                            .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                            .Cast<AIFunction>()
                            .ToList();

                    var availableCodexTools = BuildAvailableCodexTools();
                    var availableTools = availableCodexTools
                        .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                        .Cast<AIFunction>()
                        .ToList();
                    var executionBackend = sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<CodexFlow.Core.Execution.ExecutionOptions>>()?.CurrentValue.Backend
                        ?? CodexFlow.Core.Execution.ExecutionBackend.Docker;
                    var rawSystemPrompt = CodexFlow.Core.Constants.CodexPrompts.ApplyExecutionBackendEnvironment(
                        workerDefinition.BuildSystemPrompt(workerSession),
                        executionBackend);
                    var systemPrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
                        rawSystemPrompt,
                        availableCodexTools,
                        runtimeContext);
                    serviceDiagnostics["availableToolCount"] = availableTools.Count;
                    serviceDiagnostics["availableToolNames"] = availableTools.Select(t => t.Name).ToArray();

                    var baseHead = await gitService.GetHeadHashAsync(activeWorkspacePath).ConfigureAwait(false) ?? "HEAD";
                    serviceDiagnostics["baseHead"] = baseHead;

                    // The commit the TASK started from. A retry runs in the worktree the failed attempt left behind, so baseHead is that attempt's
                    // commit; the artifact manifest must start where the task started, or the failed attempt's changes belong to no artifact.
                    var artifactBaseCommit = baseHead;
                    if (CodexFlow.Core.Execution.PinnedCommitSnapshotBuilder.IsFullCommitId(baseHead))
                    {
                        var inspectorForBase = new CodexFlow.Core.Services.GitArtifactInspector();
                        artifactBaseCommit = await TaskBaseCommit.ResolveAsync(
                            payload[TaskBaseCommit.PayloadKey]?.Value<string>(),
                            baseHead,
                            (ancestor, descendant) => inspectorForBase.IsAncestorAsync(activeWorkspacePath, ancestor, descendant, ct)).ConfigureAwait(false);
                        payload[TaskBaseCommit.PayloadKey] = artifactBaseCommit;
                    }

                    serviceDiagnostics["taskBaseCommit"] = artifactBaseCommit;
                    var effectivePrompt = prompt;
                    if (enableTaskFileScopeGuard && taskFileScope is { HasConstraints: true })
                    {
                        effectivePrompt += BuildForgeTaskScopePrompt(taskFileScope);
                    }

                    // What the host fetched for this session, and where it is mounted: facts the worker otherwise has to guess at.
                    effectivePrompt += ForgeOfflinePackagesBriefing.Build(
                        sp.GetService<CodexFlow.Services.ArtifactAudit.IDependencyUpgradeTargets>()?.FetchedUpgrades(sessionId),
                        executionBackend);

                    // Build scope intervention hook (pre-execution guard) for write/edit tools
                    IQueryRuntimeInterventionHook scopeHook = new CodexFlow.Core.Agents.Adapters.KernelRuntimeEventAdapter(
                        workerSession, CodexAgentRole.Forge,
                        sp.GetService<ICodexCritiqueService>(),
                        sp.GetRequiredService<ICodexGuardrail>(), _logger, null);
                    TaskScopeInterventionHook? taskScopeHook = null;
                    if (enableTaskFileScopeGuard && taskFileScope is { HasConstraints: true })
                    {
                        taskScopeHook = new TaskScopeInterventionHook(taskFileScope, _logger);
                        scopeHook = new CompositeInterventionHook([taskScopeHook, scopeHook], _logger);
                    }

                    var request = new QueryRuntimeRequest
                    {
                        SessionId = session.Id,
                        EntryPoint = WorkerJobTypes.ToEntryPoint(WorkerType.Forge),
                        InitialMessages =
                        [
                            new ChatMessage(
                                ChatRole.System,
                                systemPrompt),
                            new ChatMessage(ChatRole.User, effectivePrompt)
                        ],
                        Session = workerSession,
                        Scenario = Core.Models.MemoryInjectionScenario.Chat,
                        MaxRounds = maxRounds,
                        EnableTools = availableTools.Count > 0,
                        Options = new VllmChatOptions
                        {
                            ThinkingEnabled = false,
                            EnableLegacyToolCallTextFallback = true,
                        },
                        AvailableTools = availableTools,
                        AvailableToolsProvider = BuildAvailableTools,
                        FrozenToolCatalog = BuildAvailableCodexTools(includeDeferred: true).Select(CodexToolFunctionAdapterFactory.CreateAIFunction).ToArray(),
                        AvailableCodexTools = availableCodexTools,
                        AvailableCodexToolsProvider = () => BuildAvailableCodexTools(),
                        WorkerContext = runtimeContext,
                        RequiredToolContract = runtimeContext.RequiredToolContract,
                        AdapterHints = new AdapterHints(
                            EnableEmptyResponseRecovery: true,
                            EnableMalformedProtocolRecovery: true,
                            EnableTransportFailureRecovery: true,
                            EnableStallDetection: true,
                            EnableToolDeduplication: true,
                            MaxRecoveryAttempts: 3),
                        InterventionHook = scopeHook
                    };

                    var loggingSink = new LoggingQueryRuntimeEventSink(_logger);
                    var sink = CreateRuntimeEventSink(sp, sessionId, job.JobId, job.TaskId, _logger, loggingSink);
                    var trustedExecution = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ForJob(
                        userId, sessionId, job.JobId, job.TaskId ?? job.JobId, job.Attempt,
                        Path.GetFullPath(activeWorkspacePath),
                        CodexFlow.Core.Execution.PinnedCommitSnapshotBuilder.IsFullCommitId(baseHead) ? baseHead : null,
                        enableTaskFileScopeGuard ? taskFileScope : null,
                        deadline: DateTimeOffset.UtcNow.AddMinutes(30));
                    var executionAccessor = sp.GetService<CodexFlow.Core.Execution.ITrustedExecutionContextAccessor>()
                        ?? new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
                    QueryRuntimeResult result;
                    using (executionAccessor.Begin(trustedExecution))
                    {
                        result = await engine.ExecuteAsync(request, sink, ct).ConfigureAwait(false);
                    }
                    await RecordTokenUsageAsync(sp, userId, result.TotalPromptTokens, result.TotalCompletionTokens, result.TotalCachedPromptTokens).ConfigureAwait(false);
                    serviceDiagnostics["runtimeEventCount"] = loggingSink.EventCount;
                    serviceDiagnostics["runtimeEventTypes"] = loggingSink.EventTypes.ToArray();

                    IReadOnlyList<GitFileChange> changedFiles = await gitService
                        .GetWorkingTreeChangedFilesAsync(activeWorkspacePath, baseHead)
                        .ConfigureAwait(false);
                    var changedFilePaths = changedFiles.Select(change => change.FilePath).ToArray();
                    string? commitHash = null;

                    if (enableTaskFileScopeGuard && taskFileScope is { HasConstraints: true } && changedFiles.Count > 0)
                    {
                        var outOfScope = TaskFileScopeGuard.GetOutOfScopeChanges(changedFiles, taskFileScope);
                        var attemptedOutOfScope = outOfScope.ToArray();
                        if (outOfScope.Count > 0)
                        {
                            var restored = await gitService
                                .RestoreFilesAsync(activeWorkspacePath, baseHead, outOfScope)
                                .ConfigureAwait(false);
                            serviceDiagnostics["scopeViolationAutoRestoreAttempted"] = true;
                            serviceDiagnostics["scopeViolationAutoRestoreSucceeded"] = restored;
                            serviceDiagnostics["scopeViolationAutoRestoredFiles"] = outOfScope.ToArray();

                            if (restored)
                            {
                                changedFiles = await gitService
                                    .GetWorkingTreeChangedFilesAsync(activeWorkspacePath, baseHead)
                                    .ConfigureAwait(false);
                                changedFilePaths = changedFiles.Select(change => change.FilePath).ToArray();
                                outOfScope = TaskFileScopeGuard.GetOutOfScopeChanges(changedFiles, taskFileScope);
                                serviceDiagnostics["scopeViolationRemainingFilesAfterRestore"] = outOfScope.ToArray();
                            }
                        }

                        if (attemptedOutOfScope.Length > 0)
                        {
                            var reason = $"检测到超出当前任务范围的文件改动，恢复操作不能使本次尝试通过：{string.Join(", ", attemptedOutOfScope.Take(8))}{(attemptedOutOfScope.Length > 8 ? " ..." : "")}";
                            serviceDiagnostics["scopeViolation"] = true;
                            serviceDiagnostics["outOfScopeFiles"] = attemptedOutOfScope;

                            if (!usingShadowWorktree)
                            {
                                var resetOk = await gitService.ResetToCommitAsync(activeWorkspacePath, baseHead).ConfigureAwait(false);
                                serviceDiagnostics["scopeGuardResetToBaseHead"] = resetOk;
                                if (!resetOk)
                                {
                                    reason += " (警告：回滚主工作区失败，请手动检查)";
                                }
                            }

                            payload["mainWorkspacePath"] = mainWorkspacePath;
                            payload["workspacePath"] = activeWorkspacePath;
                            payload["worktreeKey"] = worktreeKey;
                            payload["lastCommitHash"] = null;
                            payload["lastChangedFiles"] = JArray.FromObject(changedFilePaths);
                            payload["outOfScopeFiles"] = JArray.FromObject(outOfScope);
                            if (usingShadowWorktree)
                            {
                                payload["shadowWorktreePath"] = shadowWorkspacePath;
                            }
                            else
                            {
                                payload.Remove("shadowWorktreePath");
                            }
                            var scopeViolationWorktreeRetained = await ApplyForgeShadowRetentionPolicyAsync(
                                sp,
                                gitService,
                                mainWorkspacePath,
                                worktreeKey,
                                shadowWorkspacePath,
                                usingShadowWorktree,
                                retainShadow: keepShadowOnFailure,
                                payload,
                                serviceDiagnostics,
                                "scope_violation").ConfigureAwait(false);

                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                message = reason,
                                summary = reason,
                                result = result.FinalText,
                                workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                                durationMs = started.ElapsedMilliseconds,
                                totalRounds = result.TotalRounds,
                                totalToolCalls = result.TotalToolCalls,
                                terminationReason = result.TerminationReason.ToString(),
                                detailCode = result.TerminalDetailCode,
                                worktreePath = usingShadowWorktree ? activeWorkspacePath : null,
                                workspacePath = activeWorkspacePath,
                                changedFiles = changedFilePaths,
                                outOfScopeFiles = outOfScope.ToArray(),
                                scopeDenials = taskScopeHook?.Denials,
                                commitHash,
                                worktreeRetained = scopeViolationWorktreeRetained,
                                serviceDiagnostics,
                                updatedPayloadJson = payload.ToString(Formatting.None)
                            });
                        }
                    }

                    if (changedFiles.Count > 0)
                    {
                        await gitService.CommitAsync(activeWorkspacePath, $"feat: forge worker {job.JobId}").ConfigureAwait(false);
                        commitHash = await gitService.GetHeadHashAsync(activeWorkspacePath).ConfigureAwait(false);
                    }

                    var usedPriorCommittedChanges = false;
                    var effectiveChangedFilePaths = changedFilePaths;
                    var effectiveCommitHash = commitHash;
                    if (CanReusePriorForgeArtifacts(
                            continuedFromJobId,
                            priorCommitHash,
                            priorChangedFilePaths,
                            changedFiles.Count,
                            result.FinalText))
                    {
                        usedPriorCommittedChanges = true;
                        effectiveChangedFilePaths = priorChangedFilePaths;
                        effectiveCommitHash = priorCommitHash;
                    }

                    serviceDiagnostics["resumedUsingPriorCommittedChanges"] = usedPriorCommittedChanges;
                    RecordProviderParameterFailureIfPresent(result, serviceDiagnostics);

                    payload["mainWorkspacePath"] = mainWorkspacePath;
                    payload["workspacePath"] = activeWorkspacePath;
                    payload["worktreeKey"] = worktreeKey;
                    payload["lastCommitHash"] = effectiveCommitHash;
                    payload["lastChangedFiles"] = JArray.FromObject(effectiveChangedFilePaths);
                    if (usingShadowWorktree)
                    {
                        payload["shadowWorktreePath"] = shadowWorkspacePath;
                    }
                    else
                    {
                        payload.Remove("shadowWorktreePath");
                    }

                    if (ShouldMarkForgeRecoveryNeeded(result, changedFiles.Count))
                    {
                        var recoveryReason = ResolveRecoveryReason(result);
                        _logger.LogWarning(
                            "BackgroundJobRunner: ForgeWorker entering recovery-needed. job={JobId} session={SessionId} termination={TerminationReason} recoveryReason={RecoveryReason} changedFiles={ChangedFileCount} totalToolCalls={TotalToolCalls} runtimeFlags={RuntimeFlags}",
                            job.JobId,
                            sessionId,
                            result.TerminationReason,
                            recoveryReason,
                            changedFiles.Count,
                            result.TotalToolCalls,
                            string.Join(",", GetRuntimeFlags(result.Flags)));

                        return BuildRecoveryNeededPayload(
                            workerType: WorkerType.Forge.ToString().ToLowerInvariant(),
                            summary: BuildRecoveryNeededSummary(WorkerType.Forge, result),
                            resultText: result.FinalText,
                            durationMs: started.ElapsedMilliseconds,
                            totalRounds: result.TotalRounds,
                            totalToolCalls: result.TotalToolCalls,
                            terminationReason: result.TerminationReason.ToString(),
                            detailCode: result.TerminalDetailCode,
                            recoveryReason: recoveryReason,
                            runtimeFlags: GetRuntimeFlags(result.Flags),
                            serviceDiagnostics: serviceDiagnostics,
                            extraFields: new JObject
                            {
                                ["worktreePath"] = usingShadowWorktree ? activeWorkspacePath : null,
                                ["workspacePath"] = activeWorkspacePath,
                                ["changedFiles"] = JArray.FromObject(effectiveChangedFilePaths),
                                ["commitHash"] = effectiveCommitHash,
                                ["worktreeRetained"] = usingShadowWorktree,
                                ["updatedPayloadJson"] = payload.ToString(Formatting.None)
                            });
                    }

                    var incompleteFinalText = IsLikelyIncompleteForgeFinalText(result.FinalText);
                    if (incompleteFinalText)
                    {
                        serviceDiagnostics["incompleteFinalText"] = true;
                    }

                    var success = IsSuccessfulForgeCompletion(
                        result,
                        effectiveChangedFilePaths.Length,
                        usedPriorCommittedChanges);

                    // A forge task that needed no change: accepted on evidence only (see VerifiedNoChangeCompletion) - the worker's structured
                    // "completed", a clean build or test in this turn, and every file assertion the task declares already holding in the
                    // workspace. Every other completion rule still applies. Otherwise the worker is driven to invent a change.
                    var verifiedNoChange = !success && effectiveChangedFilePaths.Length == 0 && result.VerifiedWithoutChange &&
                        IsSuccessfulForgeCompletion(result, changedFileCount: 1, usedPriorCommittedChanges) &&
                        VerifiedNoChangeCompletion.UnmetAssertions(activeWorkspacePath, plannedTask).Count == 0;
                    if (verifiedNoChange)
                    {
                        success = true;
                        serviceDiagnostics["verifiedNoChange"] = true;
                    }

                    // The write contract is met by the name of a tool, so a notes file next to an unchanged project used to count as the work. The
                    // real change set decides: only newly created documentation files (unless the task names them) is no change to the project.
                    var noRelevantChange = success && !verifiedNoChange && !usedPriorCommittedChanges &&
                        !ForgeChangeRelevance.HasRelevantChange(changedFiles, taskFileScope?.AllowedFiles);
                    if (noRelevantChange)
                    {
                        success = false;
                        serviceDiagnostics["noRelevantChange"] = true;
                    }

                    // The worker stated, through the structured status tool, that its task is only partly done or blocked. Whatever else the host
                    // measured (a changed file, a clean restore), a task its own worker calls blocked is not a completed task. Not retried: it was
                    // a deliberate statement, and the work done so far stays in the retained shadow worktree.
                    var workerReport = result.WorkerReport;
                    var workerBlocked = success && workerReport is { IsBlocked: true };
                    if (workerBlocked)
                    {
                        success = false;
                        serviceDiagnostics["workerReportedBlocked"] = true;
                    }

                    // The host chose (and fetched) a version for each vulnerable direct package. The task that finishes the plan's code changes
                    // must leave every one of them at that version where the project references it (a weaker model upgraded 3 of 7 and said done).
                    IReadOnlyList<UnmetPackage> unmetPackages = [];
                    if (success && ForgeRestorePreflight.TouchesDependencyManifests(effectiveChangedFilePaths) &&
                        sp.GetService<CodexFlow.Services.ArtifactAudit.IDependencyUpgradeTargets>()?.ApprovedTargets(sessionId) is { Count: > 0 } upgradeTargets &&
                        PackageVersionContract.IsFinalCodeTask(job.TaskId, session.Plan))
                    {
                        unmetPackages = PackageVersionContract.Unmet(activeWorkspacePath, upgradeTargets);
                        if (unmetPackages.Count > 0)
                        {
                            success = false;
                            serviceDiagnostics["dependencyContractUnmet"] = unmetPackages.Select(static u => u.Package).ToArray();
                        }
                    }

                    var retainShadowForTransientRetry = usingShadowWorktree
                        && !success
                        && job.Attempt < MaxTransientWorkerRetryAttempts
                        && (IsTransientForgeRuntimeFailure(result) || unmetPackages.Count > 0);
                    if (retainShadowForTransientRetry)
                    {
                        serviceDiagnostics["shadowRetainedForTransientRetry"] = true;
                    }

                    var worktreeRetained = await ApplyForgeShadowRetentionPolicyAsync(
                        sp,
                        gitService,
                        mainWorkspacePath,
                        worktreeKey,
                        shadowWorkspacePath,
                        usingShadowWorktree,
                        retainShadow: success || keepShadowOnFailure || retainShadowForTransientRetry || workerBlocked,
                        payload,
                        serviceDiagnostics,
                        success ? "worker_success" : "worker_failed").ConfigureAwait(false);
                    var summary = BuildForgeWorkerSummary(
                        result,
                        usingShadowWorktree,
                        effectiveChangedFilePaths.Length,
                        usedPriorCommittedChanges);
                    if (noRelevantChange)
                    {
                        summary = "ForgeWorker 只留下了说明类文件，没有对项目文件的改动：" +
                                  string.Join(", ", ForgeChangeRelevance.NoteOnlyPaths(changedFiles).Take(8)) +
                                  "。任务要求的修改没有写入项目。";
                    }

                    if (verifiedNoChange)
                    {
                        summary = "ForgeWorker 确认本任务无需修改：" +
                                  (string.IsNullOrWhiteSpace(result.WorkerReport?.Reason) ? "任务要求的状态已经满足" : result.WorkerReport!.Reason) +
                                  "。未改动任何文件；它运行的构建或测试通过，任务声明的产物断言全部成立。";
                    }

                    if (workerBlocked)
                    {
                        summary = "ForgeWorker 报告任务受阻：" + (workerReport!.Reason ?? "报告了 completed，但列出了没有写入的修改") +
                                  (workerReport.Unfinished.Count > 0 ? "。未完成：" + string.Join("、", workerReport.Unfinished.Take(10)) : string.Empty) + "。";
                    }

                    if (unmetPackages.Count > 0)
                    {
                        summary = "依赖升级未完成：下列包仍低于宿主选定的版本 — " +
                                  string.Join("；", unmetPackages.Take(10).Select(static u => $"{u.Package} 在 {u.Project} 是 {u.Actual}，应为 {u.Expected}")) + "。";
                    }

                    var artifactManifest = success
                        ? await ForgeArtifactManifestFactory.CreateAsync(sp, job, userId, sessionId,
                            mainWorkspacePath, activeWorkspacePath, artifactBaseCommit,
                            // no change, no new commit: the artifact is the commit the task started from (an empty change set)
                            effectiveCommitHash ?? (verifiedNoChange ? baseHead : null),
                            taskFileScope, gitService, ct).ConfigureAwait(false)
                        : null;

                    // A dependency change that the network-less verification cannot restore fails HERE, on the task that made it, instead of
                    // after every later task has run. The worker's own sandbox reaches the network, so its restore proves nothing about that.
                    var restorePreflightFailure = artifactManifest is null
                        ? null
                        : await ForgeRestorePreflight.CheckAsync(sp.GetService<CodexFlow.Services.ArtifactAudit.IDependencyRiskScanService>(), artifactManifest, ct).ConfigureAwait(false);
                    if (restorePreflightFailure is not null)
                    {
                        success = false;
                        summary = restorePreflightFailure;
                        serviceDiagnostics["restorePreflightFailed"] = true;
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success,
                        message = summary,
                        summary,
                        result = result.FinalText,
                        workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                        durationMs = started.ElapsedMilliseconds,
                        totalRounds = result.TotalRounds,
                        totalToolCalls = result.TotalToolCalls,
                        writeToolCalls = result.WriteToolCalls,
                        terminationReason = result.TerminationReason.ToString(),
                        detailCode = ForgeDetailCode.Resolve(
                            restorePreflightFailure is not null, workerBlocked, noRelevantChange, unmetPackages.Count,
                            success, effectiveChangedFilePaths.Length, result.TotalToolCalls, result.TerminalDetailCode),
                        workerReport = workerReport is null ? null : new { status = workerReport.Status, reason = workerReport.Reason, unfinished = workerReport.Unfinished, unverified = workerReport.Unverified },
                        unmetPackages = unmetPackages.Select(static u => new { package = u.Package, project = u.Project, expected = u.Expected, actual = u.Actual }).ToArray(),
                        scopeDenials = taskScopeHook?.Denials,
                        detailMessage = result.TerminalDetailMessage,
                        worktreePath = usingShadowWorktree ? activeWorkspacePath : null,
                        workspacePath = activeWorkspacePath,
                        changedFiles = effectiveChangedFilePaths,
                        commitHash = effectiveCommitHash,
                        artifactManifest,
                        worktreeRetained = worktreeRetained,
                        serviceDiagnostics,
                        updatedPayloadJson = payload.ToString(Formatting.None)
                    });
                }
                finally
                {
                    workerSession.WorkspacePath = originalWorkspacePath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackgroundJobRunner: ForgeWorker threw. job={JobId} session={SessionId}", job.JobId, sessionId);
                serviceDiagnostics["exception"] = ex.GetType().Name;
                var worktreeRetained = await ApplyForgeShadowRetentionPolicyAsync(
                    sp,
                    gitService: null,
                    mainWorkspacePath,
                    worktreeKey,
                    shadowWorkspacePath,
                    usingShadowWorktree,
                    retainShadow: keepShadowOnInfrastructureError,
                    payload,
                    serviceDiagnostics,
                    "worker_infrastructure_error").ConfigureAwait(false);

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = ex.Message,
                    workerType = WorkerType.Forge.ToString().ToLowerInvariant(),
                    durationMs = started.ElapsedMilliseconds,
                    detailCode = $"{ex.GetType().Name}: {ex.Message}",
                    worktreePath = usingShadowWorktree ? activeWorkspacePath : null,
                    workspacePath = activeWorkspacePath,
                    worktreeRetained = worktreeRetained,
                    serviceDiagnostics,
                    updatedPayloadJson = payload.ToString(Formatting.None)
                });
            }
        }

        private async Task<bool> ApplyForgeShadowRetentionPolicyAsync(
            IServiceProvider serviceProvider,
            IGitService? gitService,
            string mainWorkspacePath,
            string? worktreeKey,
            string? shadowWorkspacePath,
            bool usingShadowWorktree,
            bool retainShadow,
            JObject payload,
            IDictionary<string, object?> serviceDiagnostics,
            string outcome)
        {
            serviceDiagnostics["shadowRetentionOutcome"] = outcome;
            serviceDiagnostics["shadowRetentionRequested"] = retainShadow;

            if (!usingShadowWorktree || string.IsNullOrWhiteSpace(shadowWorkspacePath))
            {
                payload.Remove("shadowWorktreePath");
                payload["worktreeRetained"] = false;
                return false;
            }

            if (retainShadow)
            {
                payload["shadowWorktreePath"] = shadowWorkspacePath;
                payload["worktreeRetained"] = true;
                return true;
            }

            if (string.IsNullOrWhiteSpace(mainWorkspacePath) || string.IsNullOrWhiteSpace(worktreeKey))
            {
                serviceDiagnostics["shadowCleanupSkipped"] = "missing_workspace_metadata";
                payload["shadowWorktreePath"] = shadowWorkspacePath;
                payload["worktreeRetained"] = true;
                return true;
            }

            gitService ??= serviceProvider.GetService<IGitService>();
            if (gitService == null)
            {
                serviceDiagnostics["shadowCleanupSkipped"] = "git_service_unavailable";
                payload["shadowWorktreePath"] = shadowWorkspacePath;
                payload["worktreeRetained"] = true;
                return true;
            }

            try
            {
                var removed = await gitService.RemoveShadowWorktreeAsync(mainWorkspacePath, worktreeKey).ConfigureAwait(false);
                serviceDiagnostics["shadowCleanupAttempted"] = true;
                serviceDiagnostics["shadowCleanupRemoved"] = removed;

                if (removed)
                {
                    payload["lastShadowWorktreePath"] = shadowWorkspacePath;
                    payload.Remove("shadowWorktreePath");
                    payload["worktreeRetained"] = false;
                    payload["worktreeCleanedAtUtc"] = DateTime.UtcNow;
                    return false;
                }
            }
            catch (Exception ex)
            {
                serviceDiagnostics["shadowCleanupAttempted"] = true;
                serviceDiagnostics["shadowCleanupException"] = ex.GetType().Name;
                _logger.LogWarning(
                    ex,
                    "Forge worker shadow cleanup failed. workspace={WorkspacePath} worktreeKey={WorktreeKey} outcome={Outcome}",
                    mainWorkspacePath,
                    worktreeKey,
                    outcome);
            }

            payload["shadowWorktreePath"] = shadowWorkspacePath;
            payload["worktreeRetained"] = true;
            return true;
        }

        private static string BuildReadOnlyWorkerSummary(WorkerType workerType, QueryRuntimeResult result)
        {
            var workerName = WorkerJobTypes.ToJobType(workerType);
            if (IsProviderToolChoiceThinkingModeFailure(result))
            {
                return $"{workerName} failed because the model provider rejected required tool_choice while thinking mode was enabled after {result.TotalRounds} round(s).";
            }

            if (!string.IsNullOrWhiteSpace(result.FinalText))
            {
                return $"{workerName} completed with {result.TotalToolCalls} tool call(s) across {result.TotalRounds} round(s).";
            }

            return $"{workerName} terminated with {result.TerminationReason} after {result.TotalRounds} round(s).";
        }

        private static bool IsSuccessfulForgeCompletion(
            QueryRuntimeResult result,
            int changedFileCount,
            bool usingPriorCommittedChanges = false)
        {
            if (result.TerminationReason == QueryTerminationReason.Exception)
            {
                return false;
            }

            if (result.TerminationReason is QueryTerminationReason.RecoveryExhausted
                or QueryTerminationReason.ContextHardLimit
                or QueryTerminationReason.StallDetected
                or QueryTerminationReason.EmptyResponseFallback)
            {
                return false;
            }

            // wrap-up 收尾阶段被 runtime 抑制说明模型仍在探索而非完成；视为失败以暴露真实状态。
            if (result.Flags.HasFlag(RuntimeState.WrapUpToolCallsSuppressed)
                || string.Equals(
                    result.TerminalDetailCode,
                    QueryTerminalDetailCodes.WrapUpToolCallsSuppressed,
                    StringComparison.Ordinal))
            {
                return false;
            }

            // Forge 任务必须留下持久化工作区变更；仅有工具调用但没有任何落盘结果会造成假成功。
            if (changedFileCount <= 0)
            {
                return false;
            }

            // Exhausting the round budget means the worker never reached a completion decision; persisted changes
            // alone are not completion. Never a success (handled as recovery-needed, which retains the shadow).
            if (result.TerminationReason == QueryTerminationReason.MaxRoundsReached)
            {
                return false;
            }

            if (usingPriorCommittedChanges && string.IsNullOrWhiteSpace(result.FinalText))
            {
                return false;
            }

            // Some forge runs leave valid file changes but end on an obvious mid-edit utterance
            // like "现在更新 Program.cs：" instead of a completion summary. Treat those as failed.
            if (IsLikelyIncompleteForgeFinalText(result.FinalText))
            {
                return false;
            }

            return true;
        }

        private const int MidEditUtteranceMaxChars = 200;
        private static readonly string[] OutcomeMarkers =
            ["已完成", "已修改", "已更新", "已新增", "已升级", "已修复", "通过", "成功", "完成了", "无需", "done", "passed", "succeeded"];

        // A worker that stopped mid-edit leaves a SHORT line announcing the next action and reporting no outcome. A real summary is long and says
        // what happened; one that merely opens with "接下来" or contains a colon is not a worker that gave up.
        internal static bool IsLikelyIncompleteForgeFinalText(string? finalText)
        {
            if (string.IsNullOrWhiteSpace(finalText))
            {
                return false;
            }

            var text = finalText.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            // The Hashline phrase is a prompt leak specific to a worker that never edited.
            if (text.Contains("使用 Hashline 模式精准修改", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.Length > MidEditUtteranceMaxChars ||
                OutcomeMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (text.EndsWith(":", StringComparison.Ordinal) || text.EndsWith("：", StringComparison.Ordinal))
            {
                return true;
            }

            var normalized = text.Replace("\r", string.Empty).Replace("\n", " ").Trim();
            return normalized.StartsWith("现在开始", StringComparison.Ordinal)
                || normalized.StartsWith("现在更新", StringComparison.Ordinal)
                || normalized.StartsWith("现在继续", StringComparison.Ordinal)
                || normalized.StartsWith("下一步", StringComparison.Ordinal)
                || normalized.StartsWith("接下来", StringComparison.Ordinal)
                || normalized.StartsWith("让我先", StringComparison.Ordinal);
        }

        private static string BuildForgeWorkerSummary(
            QueryRuntimeResult result,
            bool usingShadowWorktree,
            int changedFileCount,
            bool usingPriorCommittedChanges = false)
        {
            var modeLabel = usingShadowWorktree ? "isolated shadow worktree" : "workspace mode";
            if (IsSuccessfulForgeCompletion(result, changedFileCount, usingPriorCommittedChanges))
            {
                if (usingPriorCommittedChanges)
                {
                    return $"ForgeWorker completed in {modeLabel} by finalizing {changedFileCount} previously committed change(s).";
                }

                return $"ForgeWorker completed in {modeLabel} with {changedFileCount} changed file(s).";
            }

            if (result.Flags.HasFlag(RuntimeState.WrapUpToolCallsSuppressed)
                || string.Equals(
                    result.TerminalDetailCode,
                    QueryTerminalDetailCodes.WrapUpToolCallsSuppressed,
                    StringComparison.Ordinal))
            {
                return $"ForgeWorker exhausted wrap-up continuation in {modeLabel}: model kept emitting tool calls during the reserved summary round; runtime suppressed final tool batch after {result.TotalRounds} round(s) and {result.TotalToolCalls} tool call(s).";
            }

            if (IsProviderToolChoiceThinkingModeFailure(result))
            {
                return $"ForgeWorker failed in {modeLabel} because the model provider rejected required tool_choice while thinking mode was enabled after {result.TotalRounds} round(s).";
            }

            if (result.TotalToolCalls == 0 && changedFileCount == 0)
            {
                return $"ForgeWorker terminated with {result.TerminationReason} in {modeLabel} before issuing any tool calls.{FormatTerminalDetailSuffix(result)}";
            }

            if (result.TotalToolCalls > 0 && changedFileCount == 0)
            {
                return $"ForgeWorker terminated with {result.TerminationReason} in {modeLabel} after {result.TotalToolCalls} tool call(s) but without any persisted workspace changes.{FormatTerminalDetailSuffix(result)}";
            }

            return $"ForgeWorker terminated with {result.TerminationReason} in {modeLabel} after {result.TotalToolCalls} tool call(s) and {changedFileCount} changed file(s).{FormatTerminalDetailSuffix(result)}";
        }

        /// <summary>
        /// Appends the runtime's own explanation (e.g. which stop hook failed the turn closed) so a failed job is
        /// diagnosable from its error message instead of only "terminated with Exception".
        /// </summary>
        private static string FormatTerminalDetailSuffix(QueryRuntimeResult result)
        {
            var message = result.TerminalDetailMessage?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            const int maxLength = 300;
            if (message.Length > maxLength)
            {
                message = message[..maxLength] + "...";
            }

            return $" Reason: {message}";
        }

        private static void RecordProviderParameterFailureIfPresent(
            QueryRuntimeResult result,
            IDictionary<string, object?> serviceDiagnostics)
        {
            if (!IsProviderToolChoiceThinkingModeFailure(result))
            {
                return;
            }

            serviceDiagnostics["failureCategory"] = "provider_parameter_incompatibility";
            serviceDiagnostics["providerParameterFailure"] = "tool_choice_required_in_thinking_mode";
        }

        private static bool IsProviderToolChoiceThinkingModeFailure(QueryRuntimeResult result)
        {
            if (result.TerminationReason != QueryTerminationReason.Exception)
            {
                return false;
            }

            var detail = $"{result.TerminalDetailCode} {result.FinalText}";
            return detail.Contains("tool_choice", StringComparison.OrdinalIgnoreCase) &&
                   detail.Contains("thinking mode", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransientForgeRuntimeFailure(QueryRuntimeResult result)
        {
            if (result.TerminationReason != QueryTerminationReason.Exception)
            {
                return false;
            }

            // the reason travels in the detail message; the final text of a run that never got an answer is empty
            return TransientFailures.IsTransientProviderFailure(result.TerminalDetailCode, result.FinalText + "\n" + result.TerminalDetailMessage);
        }

        private static bool CanReusePriorForgeArtifacts(
            string? continuedFromJobId,
            string? priorCommitHash,
            IReadOnlyCollection<string> priorChangedFilePaths,
            int currentChangedFileCount,
            string? finalText)
        {
            if (string.IsNullOrWhiteSpace(continuedFromJobId))
            {
                return false;
            }

            if (currentChangedFileCount > 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(finalText))
            {
                return false;
            }

            if (priorChangedFilePaths.Count == 0)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(priorCommitHash);
        }

        private static bool CanFinalizeRetriedForgeArtifacts(
            int attempt,
            bool hasTransientRetryMetadata,
            string? lastCommitHash,
            IReadOnlyCollection<string> lastChangedFilePaths)
            => (attempt > 0 || hasTransientRetryMetadata)
                && !string.IsNullOrWhiteSpace(lastCommitHash)
                && lastChangedFilePaths.Count > 0;

        private static bool HasFinalizableCommittedForgeRetryPayload(JObject? payload, int attempt)
        {
            if (payload == null)
            {
                return false;
            }

            var lastCommitHash = payload["lastCommitHash"]?.Value<string>();
            var lastChangedFilePaths = payload["lastChangedFiles"]?
                .Values<string?>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? [];
            var hasTransientRetryMetadata = payload["retryNotBeforeUtc"] is not null
                || payload["retryDelaySeconds"] is not null;

            return CanFinalizeRetriedForgeArtifacts(
                attempt,
                hasTransientRetryMetadata,
                lastCommitHash,
                lastChangedFilePaths);
        }

        /// <summary>
        /// Terminations that mean the runtime itself failed (as opposed to a worker's business verdict). Aligned with
        /// the Forge non-success set; RecoveryExhausted is routed to recovery-needed before this is consulted.
        /// </summary>
        private static bool IsTechnicalRuntimeFailure(QueryRuntimeResult result)
            => result.TerminationReason is QueryTerminationReason.Exception
                or QueryTerminationReason.MaxRoundsReached
                or QueryTerminationReason.ContextHardLimit
                or QueryTerminationReason.StallDetected
                or QueryTerminationReason.EmptyResponseFallback;

        private static bool ShouldMarkRecoveryNeeded(QueryRuntimeResult result)
            => result.TerminationReason == QueryTerminationReason.RecoveryExhausted;

        private static bool ShouldMarkForgeRecoveryNeeded(QueryRuntimeResult result, int changedFileCount)
        {
            if (ShouldMarkRecoveryNeeded(result))
            {
                return true;
            }

            if (IsForgeUnexecutedWriteRecoveryCandidate(result, changedFileCount))
            {
                return true;
            }

            if (changedFileCount <= 0)
            {
                return false;
            }

            // Round-budget exhaustion with persisted (committed, retained) changes is unfinished work: recover/continue
            // from the retained shadow instead of reporting success or discarding the artifacts.
            if (result.TerminationReason == QueryTerminationReason.MaxRoundsReached)
            {
                return true;
            }

            if (!HasContinuationRecoverySignal(result.Flags))
            {
                return false;
            }

            return result.TerminationReason is QueryTerminationReason.EmptyResponseFallback
                or QueryTerminationReason.StallDetected;
        }

        private static bool IsForgeUnexecutedWriteRecoveryCandidate(QueryRuntimeResult result, int changedFileCount)
            => changedFileCount <= 0 &&
               result.TerminationReason == QueryTerminationReason.NoToolCalls &&
               result.TotalToolCalls > 0 &&
               result.WriteToolCalls == 0 &&
               !result.VerifiedWithoutChange; // a stated, verified "nothing to change" is a decision, not an unexecuted write

        private static string BuildForgeTaskScopePrompt(TaskFileScopeDescriptor taskFileScope)
        {
            return "\n\n# 文件范围约束\n" +
                   $"允许修改以下已有文件（或其直接对应测试文件）：\n- {string.Join("\n- ", taskFileScope.AllowedFiles)}\n" +
                   (taskFileScope.AllowedFileNames.Count > 0
                       ? $"也允许修改任务中明确点名的同名源文件：\n- {string.Join("\n- ", taskFileScope.AllowedFileNames)}\n"
                       : string.Empty) +
                   (taskFileScope.AllowedDirectories.Count > 0
                       ? $"允许在以下目录创建新文件：\n- {string.Join("\n- ", taskFileScope.AllowedDirectories)}\n"
                       : string.Empty) +
                   (taskFileScope.DisallowedDirectories.Count > 0
                       ? $"明确禁止修改以下目录：\n- {string.Join("\n- ", taskFileScope.DisallowedDirectories)}\n"
                       : string.Empty) +
                   (taskFileScope.AllowProjectFileEdits
                       ? "允许修改相关的 .csproj 项目文件。\n"
                       : "未列入允许清单的 .csproj、props、targets、appsettings、Program.cs 等项目/配置文件禁止修改。\n") +
                   "除非迁移文件或迁移目录被明确列入允许清单，否则禁止运行 `dotnet ef migrations`、`dotnet ef database`，也不要创建或重写 Migrations 目录。\n" +
                   "除非项目文件被明确列入允许清单，否则禁止运行 `dotnet add/remove package`、`dotnet add/remove reference`、`dotnet sln add/remove`、`dotnet new`、`dotnet format` 等会改项目结构或批量重写文件的命令。\n" +
                   "允许使用 `dotnet build`、`dotnet test`、`dotnet list package --vulnerable` 等只用于验证或审计的命令。\n" +
                   "验证、构建或审计发现范围外问题时，只报告为后续任务或剩余风险，不要为了修复它们扩散修改。\n" +
                   "若你发现需要改动范围外文件，请停止并在结果中说明，不要擅自扩散修改。";
        }

        private static bool ShouldMarkRecoveryNeeded(JObject? payload)
            => payload?["recoveryNeeded"]?.Value<bool>() == true;

        private static bool HasContinuationRecoverySignal(RuntimeState flags)
        {
            return flags.HasFlag(RuntimeState.EmptyResponseRecoveryUsed)
                || flags.HasFlag(RuntimeState.MalformedProtocolRecoveryUsed)
                || flags.HasFlag(RuntimeState.TransportFailureRecoveryUsed)
                || flags.HasFlag(RuntimeState.StallDetected);
        }

        private static string ResolveRecoveryReason(QueryRuntimeResult result)
        {
            if (result.Flags.HasFlag(RuntimeState.UnexecutedWriteIntentRecoveryUsed) ||
                (result.TerminationReason == QueryTerminationReason.NoToolCalls &&
                 result.TotalToolCalls > 0 &&
                 result.WriteToolCalls == 0))
            {
                return "unexecuted_write_intent";
            }

            if (result.Flags.HasFlag(RuntimeState.TransportFailureRecoveryUsed))
            {
                return "transport_failure";
            }

            if (result.Flags.HasFlag(RuntimeState.StallDetected))
            {
                return "stall_detected";
            }

            if (result.Flags.HasFlag(RuntimeState.EmptyResponseRecoveryUsed))
            {
                return "empty_response";
            }

            if (result.Flags.HasFlag(RuntimeState.MalformedProtocolRecoveryUsed))
            {
                return "malformed_protocol";
            }

            return result.TerminationReason == QueryTerminationReason.MaxRoundsReached
                ? "max_rounds_reached"
                : "recovery_exhausted";
        }

        private static string[] GetRuntimeFlags(RuntimeState flags)
        {
            return Enum.GetValues<RuntimeState>()
                .Where(flag => flag != RuntimeState.None && flags.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToArray();
        }

        private static string BuildRecoveryNeededSummary(WorkerType workerType, QueryRuntimeResult result)
        {
            var workerName = WorkerJobTypes.ToJobType(workerType);
            var recoveryReason = ResolveRecoveryReason(result);
            return result.TerminationReason switch
            {
                QueryTerminationReason.RecoveryExhausted =>
                    $"{workerName} requires recovery after {recoveryReason} exhausted runtime safeguards.",
                QueryTerminationReason.MaxRoundsReached =>
                    $"{workerName} requires recovery after {recoveryReason} consumed the remaining round budget.",
                _ =>
                    $"{workerName} requires recovery after {recoveryReason} interrupted execution with {result.TerminationReason}."
            };
        }

        private static string ResolveWorkerTypeName(string jobType)
        {
            return WorkerJobTypes.TryResolveWorkerType(jobType, out var workerType)
                ? workerType.ToString().ToLowerInvariant()
                : jobType.Trim().ToLowerInvariant();
        }

        private static string BuildRecoveryNeededPayload(
            string workerType,
            string summary,
            string? resultText,
            long? durationMs,
            int? totalRounds,
            int? totalToolCalls,
            string? terminationReason,
            string? detailCode,
            string recoveryReason,
            IReadOnlyCollection<string>? runtimeFlags,
            IReadOnlyDictionary<string, object?>? serviceDiagnostics,
            JObject? extraFields = null)
        {
            var payload = extraFields ?? new JObject();
            var playbook = WorkerRecoveryPlaybookFactory.Create(
                recoveryReason,
                workerType,
                runtimeFlags,
                serviceDiagnostics);

            payload["success"] = false;
            payload["recoveryNeeded"] = true;
            payload["canResume"] = true;
            payload["resumeStrategy"] = "continue_worker";
            payload["recoveryReason"] = recoveryReason;
            payload["resumeGuidance"] = WorkerRecoveryPlaybookFactory.BuildGuidanceSummary(playbook);
            payload["resumePlaybook"] = playbook;
            payload["message"] = summary;
            payload["summary"] = summary;
            payload["workerType"] = workerType;

            if (!string.IsNullOrWhiteSpace(resultText))
            {
                payload["result"] = resultText;
            }

            if (durationMs.HasValue)
            {
                payload["durationMs"] = durationMs.Value;
            }

            if (totalRounds.HasValue)
            {
                payload["totalRounds"] = totalRounds.Value;
            }

            if (totalToolCalls.HasValue)
            {
                payload["totalToolCalls"] = totalToolCalls.Value;
            }

            if (!string.IsNullOrWhiteSpace(terminationReason))
            {
                payload["terminationReason"] = terminationReason;
            }

            if (!string.IsNullOrWhiteSpace(detailCode))
            {
                payload["detailCode"] = detailCode;
            }

            if (runtimeFlags is { Count: > 0 })
            {
                payload["runtimeFlags"] = JArray.FromObject(runtimeFlags);
            }

            if (serviceDiagnostics != null)
            {
                payload["serviceDiagnostics"] = JObject.FromObject(serviceDiagnostics);
            }

            return payload.ToString(Formatting.None);
        }

        private async Task MarkJobRecoveryNeededAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            string recoveryPayloadJson,
            string reason,
            JobCheckpoint? latestCheckpoint,
            bool suppressNotifications,
            CancellationToken ct)
        {
            var trackedJob = await ResolveTrackedJobForTerminalWriteAsync(dbContext, job, ct).ConfigureAwait(false);
            if (trackedJob == null)
            {
                _logger.LogWarning(
                    "Skipping recovery-needed persistence because the parent job no longer exists. job={JobId}",
                    job.JobId);
                return;
            }

            job = trackedJob;

            dbContext.JobCheckpoints.Add(CreateCheckpoint(
                job,
                latestCheckpoint,
                "RecoveryNeeded",
                pendingActionType: "RecoveryResume",
                pendingPayloadJson: job.PayloadJson,
                checkpointData: JsonConvert.SerializeObject(new
                {
                    reason,
                    recoveryPayloadJson,
                    markedAtUtc = DateTime.UtcNow
                })));

            job.Status = BackgroundJobStatus.FailedRecoveryNeeded;
            job.ErrorMessage = reason;
            job.WaitingReason = null;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.UpdatedAtUtc = DateTime.UtcNow;
            job.PayloadJson = TryParsePayload(recoveryPayloadJson)?["updatedPayloadJson"]?.Value<string>() ?? job.PayloadJson;

            if (WorkerJobTypes.IsWorkerJobType(job.JobType))
            {
                job.NotificationStatus = suppressNotifications
                    ? NotificationStatus.Suppressed
                    : NotificationStatus.Pending;
            }

            var payloadWithNotification = await AttachJobNotificationXmlAsync(job, recoveryPayloadJson, ct);
            job.ResultPayloadJson = payloadWithNotification;

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                EventType = "JobRecoveryNeeded",
                JobId = job.JobId,
                SessionId = job.SessionId,
                TaskId = job.TaskId,
                Payload = payloadWithNotification
            });

            try
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping recovery-needed persistence because the job claim changed before save. job={JobId}",
                    job.JobId);
                dbContext.ChangeTracker.Clear();
                return;
            }
            catch (DbUpdateException ex) when (IsMissingBackgroundJobForeignKeyViolation(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Skipping recovery-needed checkpoint because the parent job disappeared before save. job={JobId}",
                    job.JobId);
                dbContext.ChangeTracker.Clear();
                return;
            }

            _outboxSignal.Pulse();
            if (!suppressNotifications)
            {
                await DispatchNotificationImmediatelyAsync(job.JobId, CancellationToken.None);
            }
        }

        private static async Task<BackgroundJob?> ResolveTrackedJobForTerminalWriteAsync(
            IJobDbContext dbContext,
            BackgroundJob job,
            CancellationToken ct)
        {
            var entry = dbContext.Entry(job);
            if (entry.State == EntityState.Detached)
            {
                return await dbContext.BackgroundJobs.FindAsync([job.JobId], ct).ConfigureAwait(false);
            }

            await entry.ReloadAsync(ct).ConfigureAwait(false);
            return entry.State == EntityState.Detached ? null : job;
        }

        private static bool IsMissingBackgroundJobForeignKeyViolation(DbUpdateException ex)
        {
            // SQLSTATE 23503 is a foreign key violation on every provider that reports SQLSTATE; the message names the constraint.
            return ex.GetBaseException() is System.Data.Common.DbException databaseException &&
                   string.Equals(databaseException.SqlState, "23503", StringComparison.Ordinal) &&
                   databaseException.Message.Contains("FK_JobCheckpoints_BackgroundJobs_JobId", StringComparison.Ordinal);
        }

        private static string GetDefaultWorkerPrompt(WorkerType workerType)
            => workerType switch
            {
                WorkerType.Explore => "请探索当前项目并给出结构化结论。",
                WorkerType.Plan => "请基于当前上下文给出结构化计划建议。",
                WorkerType.Verify => "请验证当前实现并给出可靠结论。",
                _ => "请完成当前任务。"
            };

        private static ParsedVerificationReport ParseVerificationReport(string? finalText, string taskId, string jobId)
        {
            if (string.IsNullOrWhiteSpace(finalText))
            {
                return ParsedVerificationReport.Fail("验证报告缺失，未收到 verification-report XML。");
            }

            var xml = ExtractVerificationReportXml(finalText);
            if (string.IsNullOrWhiteSpace(xml))
            {
                return ParsedVerificationReport.Fail("验证报告缺失，输出中未找到 verification-report XML。");
            }

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root is null || !string.Equals(root.Name.LocalName, "verification-report", StringComparison.OrdinalIgnoreCase))
                {
                    return ParsedVerificationReport.Fail("验证报告根节点不是 verification-report。");
                }

                var verdict = NormalizeVerdict(root.Element("verdict")?.Value);
                var summary = root.Element("summary")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = "验证报告缺少 summary，自动降级为 FAIL。";
                    verdict = "fail";
                }

                var evidence = root.Element("evidence-list")?
                    .Elements("evidence")
                    .Select(evidenceNode => new VerificationEvidence
                    {
                        Check = evidenceNode.Element("check")?.Value?.Trim() ?? "unspecified-check",
                        Passed = bool.TryParse(evidenceNode.Element("passed")?.Value, out var evidencePassed)
                            ? evidencePassed
                            : verdict == "pass",
                        Command = evidenceNode.Element("command")?.Value?.Trim(),
                        ExitCode = evidenceNode.Element("exit_code")?.Value?.Trim(),
                        Observation = evidenceNode.Element("observation")?.Value?.Trim()
                    })
                    .ToList()
                    ?? [];

                var issues = root.Element("issues")?
                    .Elements("issue")
                    .Select(issue => issue.Value?.Trim())
                    .Where(static issue => !string.IsNullOrWhiteSpace(issue))
                    .Select(static issue => issue!)
                    .ToList()
                    ?? [];

                return new ParsedVerificationReport(
                    Verdict: verdict,
                    Summary: summary,
                    Evidence: evidence,
                    Issues: issues,
                    HasCommandEvidence: evidence.Any(static item => !string.IsNullOrWhiteSpace(item.Command) && !string.IsNullOrWhiteSpace(item.Observation)),
                    HasHappyPathEvidence: evidence.Any(static item => item.Check.StartsWith("happy_path:", StringComparison.OrdinalIgnoreCase)),
                    HasAdversarialProbeEvidence: evidence.Any(static item => item.Check.StartsWith("adversarial_probe:", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                return ParsedVerificationReport.Fail($"验证报告解析失败：{ex.Message}");
            }
        }

        private static string? ExtractVerificationReportXml(string finalText)
        {
            var fullTagStart = finalText.IndexOf("<verification-report", StringComparison.OrdinalIgnoreCase);
            var fullTagEnd = finalText.LastIndexOf("</verification-report>", StringComparison.OrdinalIgnoreCase);
            if (fullTagStart >= 0 && fullTagEnd >= fullTagStart)
            {
                fullTagEnd += "</verification-report>".Length;
                return finalText[fullTagStart..fullTagEnd];
            }

            // Some models drop the opening root tag but still emit a complete inner body plus
            // the closing tag. Recover that fragment into a valid verification-report document.
            var verdictStart = finalText.IndexOf("<verdict>", StringComparison.OrdinalIgnoreCase);
            if (verdictStart < 0 || fullTagEnd < 0 || fullTagEnd < verdictStart)
            {
                return null;
            }

            fullTagEnd += "</verification-report>".Length;
            var innerXml = finalText[verdictStart..fullTagEnd];
            return "<verification-report>\n" + innerXml;
        }

        private static string NormalizeVerdict(string? verdict)
            => verdict?.Trim().ToLowerInvariant() switch
            {
                "pass" => "pass",
                "partial" => "partial",
                _ => "fail"
            };

        private static VerificationChecklistProjection BuildVerificationChecklist(ParsedVerificationReport report)
        {
            var completed = new List<VerificationChecklistItem>();
            var failed = new List<VerificationChecklistItem>();
            var pending = new List<VerificationChecklistItem>();

            foreach (var evidence in report.Evidence)
            {
                var evidenceLines = BuildVerificationEvidenceLines(evidence);
                var hasExecutableEvidence = !string.IsNullOrWhiteSpace(evidence.Command) &&
                                            !string.IsNullOrWhiteSpace(evidence.Observation);

                if (!evidence.Passed)
                {
                    failed.Add(new VerificationChecklistItem(evidence.Check, evidenceLines));
                    continue;
                }

                if (!hasExecutableEvidence)
                {
                    var pendingLines = evidenceLines.ToList();
                    pendingLines.Add("当前检查缺少可执行命令证据，需补充 command 与 observation。");
                    pending.Add(new VerificationChecklistItem(evidence.Check, pendingLines));
                    continue;
                }

                completed.Add(new VerificationChecklistItem(evidence.Check, evidenceLines));
            }

            if (!report.HasHappyPathEvidence)
            {
                pending.Add(new VerificationChecklistItem(
                    "补齐 happy_path:* 验证证据",
                    ["至少补充一条带 command 与 observation 的 happy_path:* 检查。"]));
            }

            if (!report.HasAdversarialProbeEvidence)
            {
                pending.Add(new VerificationChecklistItem(
                    "补齐 adversarial_probe:* 验证证据",
                    ["至少补充一条带 command 与 observation 的 adversarial_probe:* 检查。"]));
            }

            if (!report.HasCommandEvidence)
            {
                pending.Add(new VerificationChecklistItem(
                    "补齐命令执行证据",
                    ["至少一条 evidence 必须同时包含 command 与 observation。"]));
            }

            var focusItems = failed
                .Select(item => item.Check)
                .Concat(pending.Select(item => item.Check))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new VerificationChecklistProjection(
                CompletedItems: completed.ToArray(),
                FailedItems: failed.ToArray(),
                PendingItems: pending.ToArray(),
                FocusItems: focusItems);
        }

        private static IReadOnlyList<string> BuildVerificationEvidenceLines(VerificationEvidence evidence)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(evidence.Command))
            {
                lines.Add($"command: {evidence.Command.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(evidence.ExitCode))
            {
                lines.Add($"exit_code: {evidence.ExitCode.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(evidence.Observation))
            {
                lines.Add(evidence.Observation.Trim());
            }

            return lines;
        }

        /// <summary>
        /// A dependency task may write the manifests of the projects the host's own scan says the named packages reach: a plan's author can guess
        /// the wrong files (it declared "OtherProject1/*.csproj" without having read the repository), the scan cannot.
        /// </summary>
        private static void AddHostKnownDependencyManifests(IServiceProvider sp, string sessionId, CodexTask? task, TaskFileScopeDescriptor? scope)
        {
            if (task is null || scope is null)
            {
                return;
            }

            var upgrades = sp.GetService<CodexFlow.Services.ArtifactAudit.IDependencyUpgradeTargets>()?.FetchedUpgrades(sessionId);
            if (upgrades is { Count: > 0 })
            {
                TaskFileScopeGuard.AddHostKnownManifests(scope, task, upgrades.Select(static upgrade => (upgrade.Package, upgrade.Projects)));
            }
        }

        /// <summary>
        /// A quality retry queued for packages the host's contract found unmet was told to change those packages in the projects that declare them
        /// (<see cref="ForgeQualityRetry.Apply"/> keeps the projects in the payload). The task's own scope does not name them, so without this the
        /// scope guard refused the very edits the retry was queued for.
        /// </summary>
        private static void AddHostGrantedRetryManifests(JObject payload, TaskFileScopeDescriptor? scope)
        {
            if (scope is null || payload["retryUnmetProjects"] is not JArray projects || projects.Count == 0)
            {
                return;
            }

            TaskFileScopeGuard.AddHostGrantedManifests(scope, projects.Select(static p => p.Value<string>() ?? string.Empty));
        }

        private static T ResolveRequiredService<T>(IServiceProvider sp, IDictionary<string, object?> diagnostics)
            where T : notnull
        {
            try
            {
                var service = sp.GetRequiredService<T>();
                diagnostics[typeof(T).Name] = "resolved";
                return service;
            }
            catch (Exception ex)
            {
                diagnostics[typeof(T).Name] = $"failed: {ex.GetType().Name}";
                throw;
            }
        }

        private static T? TryResolveOptionalService<T>(IServiceProvider sp, IDictionary<string, object?> diagnostics)
        {
            try
            {
                var service = sp.GetService<T>();
                diagnostics[typeof(T).Name] = service == null ? "not_registered" : "resolved";
                return service;
            }
            catch (Exception ex)
            {
                diagnostics[typeof(T).Name] = $"failed: {ex.GetType().Name}";
                return default;
            }
        }

        private static void ActivateWorkerDeferredTools(IToolRegistry toolRegistry, WorkerDefinition workerDefinition)
        {
            foreach (var toolName in workerDefinition.AutoActivateToolNames)
            {
                toolRegistry.ActivateTool(toolName);
            }
        }

        /// <summary>
        /// Test-only job handler: simulates a 3-second task and returns success.
        /// Used by the submit_mock_task tool in integration tests to verify sequential execution.
        /// </summary>
        private static async Task<string> RunMockSimpleTaskAsync(BackgroundJob job, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = $"MockSimpleTask [{job.TaskId ?? job.JobId}] completed successfully.",
                taskId = job.TaskId
            });
        }

        private static async Task<string> SimulateJobAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return "{}";
        }

        private static IQueryRuntimeEventSink CreateRuntimeEventSink(
            IServiceProvider serviceProvider,
            string sessionId,
            string jobId,
            string? taskId,
            ILogger logger,
            LoggingQueryRuntimeEventSink loggingSink)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return loggingSink;
            }

            var gateway = serviceProvider.GetService<ICodexGateway>();
            var hubContext = serviceProvider.GetService<IRealtimeNotifier>();
            if (gateway == null && hubContext == null)
            {
                return loggingSink;
            }

            return new CompositeQueryRuntimeEventSink(
                loggingSink,
                new BackgroundGatewayRuntimeEventSink(gateway, hubContext, sessionId, jobId, taskId, logger));
        }

        private sealed class LoggingQueryRuntimeEventSink(ILogger logger) : IQueryRuntimeEventSink
        {
            private readonly List<string> _eventTypes = [];

            public int EventCount => _eventTypes.Count;

            public IReadOnlyList<string> EventTypes => _eventTypes;

            public bool IsEnabled(QueryRuntimeEventType eventType) => true;

            public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
            {
                _eventTypes.Add(runtimeEvent.GetType().Name);
                logger.LogDebug(
                    "ExploreWorker runtime event: {EventType} seq={Seq} session={SessionId}",
                    runtimeEvent.GetType().Name,
                    runtimeEvent.Seq,
                    runtimeEvent.SessionId);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class BackgroundGatewayRuntimeEventSink(
            ICodexGateway? gateway,
            IRealtimeNotifier? hubContext,
            string sessionId,
            string jobId,
            string? taskId,
            ILogger logger) : IQueryRuntimeEventSink
        {
            public bool IsEnabled(QueryRuntimeEventType eventType)
                => (gateway != null || hubContext != null)
                    && eventType is QueryRuntimeEventType.ToolCallRequested
                        or QueryRuntimeEventType.ToolExecutionCompleted
                        or QueryRuntimeEventType.SystemNotice
                        or QueryRuntimeEventType.Error
                        or QueryRuntimeEventType.RoundCompleted
                        or QueryRuntimeEventType.RecoveryTriggered;

            public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
            {
                var gatewayEvent = runtimeEvent switch
                {
                    ToolCallRequestedEvent e => new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.ToolCall,
                        Payload = new
                        {
                            type = GatewaySseEventType.ToolCall,
                            name = e.ToolName,
                            callId = e.CallId,
                            jobId,
                            taskId
                        }
                    },
                    ToolExecutionCompletedEvent e => new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.TaskStatus,
                        Payload = new
                        {
                            type = "tool_result",
                            name = e.ToolName,
                            success = e.Success,
                            result = e.Result,
                            jobId,
                            taskId
                        }
                    },
                    SystemNoticeEvent e => new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.SystemMessage,
                        Payload = new
                        {
                            type = GatewaySseEventType.SystemMessage,
                            noticeType = e.NoticeType,
                            content = e.Content,
                            jobId,
                            taskId
                        }
                    },
                    ErrorEvent e => new GatewaySseEvent
                    {
                        Type = GatewaySseEventType.Error,
                        Payload = new
                        {
                            error = e.ErrorType,
                            message = e.Message,
                            jobId,
                            taskId
                        }
                    },
                    _ => null
                };

                try
                {
                    if (gatewayEvent != null && gateway != null)
                    {
                        await gateway.PublishEventAsync(sessionId, gatewayEvent).ConfigureAwait(false);

                        if (runtimeEvent is ToolExecutionCompletedEvent completed &&
                            IsFileTreeMutatingTool(completed.ToolName))
                        {
                            await gateway.PublishEventAsync(
                                sessionId,
                                new GatewaySseEvent
                                {
                                    Type = GatewaySseEventType.FileTreeUpdated,
                                    Payload = new
                                    {
                                        tool = completed.ToolName,
                                        jobId,
                                        taskId
                                    }
                                }).ConfigureAwait(false);
                        }
                    }

                    var progressEvent = BuildProgressEvent(runtimeEvent);
                    if (progressEvent != null && hubContext != null)
                    {
                        await hubContext
                            .SendToGroupAsync($"conv:{sessionId}", "codexProgress", progressEvent)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Failed to publish background runtime event. session={SessionId} job={JobId} task={TaskId} event={EventType}",
                        sessionId,
                        jobId,
                        taskId ?? string.Empty,
                        runtimeEvent.GetType().Name);
                }
            }

            private object? BuildProgressEvent(QueryRuntimeEvent runtimeEvent)
            {
                var timestamp = runtimeEvent.Timestamp == default
                    ? DateTimeOffset.UtcNow
                    : runtimeEvent.Timestamp;

                return runtimeEvent switch
                {
                    ToolCallRequestedEvent e => new
                    {
                        sessionId,
                        type = "WorkerToolStarted",
                        message = $"Forge worker 调用工具 [{e.ToolName}]",
                        payload = new
                        {
                            jobId,
                            taskId,
                            e.Round,
                            toolName = e.ToolName,
                            e.CallId
                        },
                        timestamp
                    },
                    ToolExecutionCompletedEvent e => new
                    {
                        sessionId,
                        type = e.Success ? "WorkerToolCompleted" : "WorkerToolFailed",
                        message = e.Success
                            ? $"Forge worker 工具 [{e.ToolName}] 执行完成"
                            : $"Forge worker 工具 [{e.ToolName}] 执行失败",
                        payload = new
                        {
                            jobId,
                            taskId,
                            e.Round,
                            toolName = e.ToolName,
                            e.CallId,
                            e.Success,
                            e.ResultLength
                        },
                        timestamp
                    },
                    SystemNoticeEvent e => new
                    {
                        sessionId,
                        type = "WorkerNotice",
                        message = e.Content,
                        payload = new
                        {
                            jobId,
                            taskId,
                            noticeType = e.NoticeType
                        },
                        timestamp
                    },
                    RecoveryTriggeredEvent e => new
                    {
                        sessionId,
                        type = "WorkerRecovery",
                        message = $"Forge worker 触发恢复：{e.RecoveryType}（第 {e.Attempt} 次）",
                        payload = new
                        {
                            jobId,
                            taskId,
                            e.Round,
                            e.RecoveryType,
                            e.Attempt,
                            e.Reason
                        },
                        timestamp
                    },
                    RoundCompletedEvent e => new
                    {
                        sessionId,
                        type = "TaskProgress",
                        message = $"Forge worker 第 {e.Round} 轮完成：{e.ToolCallCount} 次工具调用，输出 {e.TextLength} 字",
                        payload = new
                        {
                            jobId,
                            taskId,
                            e.Round,
                            e.ToolCallCount,
                            e.HasText,
                            e.TextLength,
                            e.ThinkingLength,
                            e.ContinueReason
                        },
                        timestamp
                    },
                    ErrorEvent e => new
                    {
                        sessionId,
                        type = "TaskFailed",
                        message = $"Forge worker 错误：{e.Message}",
                        payload = new
                        {
                            jobId,
                            taskId,
                            e.ErrorType
                        },
                        timestamp
                    },
                    _ => null
                };
            }

            private static bool IsFileTreeMutatingTool(string? toolName)
                => toolName is "write_file"
                    or "edit_file"
                    or "delete_file"
                    or "create_directory"
                    or "apply_patch"
                    or "ivilson_smart_patch"
                    or "hs_write";
        }

        private sealed record ParsedVerificationReport(
            string Verdict,
            string Summary,
            IReadOnlyList<VerificationEvidence> Evidence,
            IReadOnlyList<string> Issues,
            bool HasCommandEvidence,
            bool HasHappyPathEvidence,
            bool HasAdversarialProbeEvidence)
        {
            public bool Passed => string.Equals(Verdict, "pass", StringComparison.OrdinalIgnoreCase);

            public ParsedVerificationReport WithIssue(string issue, string? forcedVerdict = null)
            {
                var issues = Issues.Concat([issue]).Distinct(StringComparer.Ordinal).ToArray();
                return this with
                {
                    Verdict = NormalizeVerdict(forcedVerdict ?? Verdict),
                    Issues = issues
                };
            }

            public static ParsedVerificationReport Fail(string summary)
                => new(
                    Verdict: "fail",
                    Summary: summary,
                    Evidence: [],
                    Issues: [summary],
                    HasCommandEvidence: false,
                    HasHappyPathEvidence: false,
                    HasAdversarialProbeEvidence: false);
        }

        private sealed record VerificationChecklistItem(
            string Check,
            IReadOnlyList<string> Evidence);

        private sealed record VerificationChecklistProjection(
            IReadOnlyList<VerificationChecklistItem> CompletedItems,
            IReadOnlyList<VerificationChecklistItem> FailedItems,
            IReadOnlyList<VerificationChecklistItem> PendingItems,
            IReadOnlyList<string> FocusItems);
    }
}
