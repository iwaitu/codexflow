// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using CodexFlow.Application.Jobs;
using CodexFlow.Application.Notifications;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Notifications;

/// <summary>
/// Gives a finished worker job back to the user's conversation: it enqueues a <c>TaskCompleted</c> / <c>TaskFailed</c> gateway message, and the
/// coordinator, who is the one that tells the user what happened and what to change, answers it. A failed job carries its structured failure
/// (code, kind, remedy, evidence), the coordinator's basis for saying what would change the outcome.
/// <para>
/// This is the part of the platform's notification subsystem that every host needs. The platform keeps its own dispatcher (leases, presence,
/// priorities, LLM summaries, SignalR); a host without one used a no-op, so after a task failed its conversation simply stopped (seen live,
/// dependency-upgrade-out-of-scope: the last line was "plan approved, task dispatched").
/// </para>
/// </summary>
public sealed class MainSessionJobOutcomeDispatcher(
    IJobStore jobs,
    ISessionDirectory sessions,
    IJobOutcomeStore outcomes,
    ICodexGateway gateway,
    IWorkerNotificationProjector workerNotificationProjector,
    ILogger<MainSessionJobOutcomeDispatcher> logger) : INotificationDispatcher
{
    // The runner dispatches a job from several places; two of them can run at the same moment. The status on the job covers every later call.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task DispatchAsync(string jobId, CancellationToken ct, string dispatchSource = "unknown")
    {
        if (!InFlight.TryAdd(jobId, 0))
        {
            return;
        }

        try
        {
            await DispatchCoreAsync(jobId, ct, dispatchSource).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telling the user must never break the job's own path; the job stays un-announced and a later dispatch tries again.
            logger.LogWarning(ex, "Announcing job {JobId} to its conversation failed (source {Source})", jobId, dispatchSource);
        }
        finally
        {
            InFlight.TryRemove(jobId, out _);
        }
    }

    private async Task DispatchCoreAsync(string jobId, CancellationToken ct, string dispatchSource)
    {
        var job = await jobs.FindAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || string.IsNullOrWhiteSpace(job.SessionId) || !WorkerJobTypes.IsWorkerJobType(job.JobType))
        {
            return;
        }

        var succeeded = job.Status == BackgroundJobStatus.Completed;
        var failed = job.Status is BackgroundJobStatus.Failed or BackgroundJobStatus.Cancelled or BackgroundJobStatus.FailedRecoveryNeeded;
        if (!succeeded && !failed)
        {
            return;
        }

        if (job.NotificationStatus is NotificationStatus.SummaryDelivered or NotificationStatus.SyncDelivered or NotificationStatus.Suppressed)
        {
            return;
        }

        var userId = (await sessions.FindAsync(job.SessionId, ct).ConfigureAwait(false))?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning("Job {JobId}: its session {SessionId} has no owner; nothing to announce", jobId, job.SessionId);
            return;
        }

        var conversationId = await outcomes.FindAnnouncementConversationAsync(job.SessionId, userId, job.TaskId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            logger.LogInformation("Job {JobId}: session {SessionId} has no conversation to announce it in", jobId, job.SessionId);
            return;
        }

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        JobFailureNotificationMeta.Apply(meta, job);
        var notificationXml = ResolveWorkerNotificationXml(job);
        var message = new GatewayMessage
        {
            Source = succeeded ? GatewayMessageSource.TaskCompleted : GatewayMessageSource.TaskFailed,
            SessionId = job.SessionId,
            UserId = userId,
            ConversationId = conversationId,
            TaskId = job.TaskId,
            Content = string.IsNullOrWhiteSpace(notificationXml) ? Fallback(job, succeeded) : notificationXml,
            Meta = new GatewayMessageMeta
            {
                JobId = job.JobId,
                TaskSuccess = succeeded,
                TaskResultMessage = Fallback(job, succeeded),
                Failure = succeeded ? null : JobFailureNotificationMeta.Read(meta),
                RetryCount = job.Attempt
            }
        };

        var messageId = await gateway.EnqueueAsync(message, ct).ConfigureAwait(false);

        await outcomes.RecordAnnouncementAsync(job, messageId, conversationId, DateTime.UtcNow, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Announced job {JobId} to conversation {ConversationId} as {Source} (message {MessageId}, dispatch {DispatchSource})",
            jobId, conversationId, message.Source, messageId, dispatchSource);
    }

    private string ResolveWorkerNotificationXml(BackgroundJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.ResultPayloadJson))
        {
            try
            {
                var xml = JObject.Parse(job.ResultPayloadJson)["workerNotificationXml"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    return xml;
                }
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException)
            {
                logger.LogWarning(ex, "Job {JobId}: its result is not readable as JSON; the notification is projected from the job instead", job.JobId);
            }
        }

        return workerNotificationProjector.ProjectJobNotification(job, job.ResultPayloadJson);
    }

    private static string Fallback(BackgroundJob job, bool succeeded)
    {
        var task = string.IsNullOrWhiteSpace(job.TaskId) ? $"作业 {job.JobId}" : $"任务 {job.TaskId}";
        return succeeded ? $"{task} 已完成。" : $"{task} 失败：{(string.IsNullOrWhiteSpace(job.ErrorMessage) ? job.Status.ToString() : job.ErrorMessage)}";
    }
}
