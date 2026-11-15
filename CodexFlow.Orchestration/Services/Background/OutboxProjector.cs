// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using CodexFlow.Core.Models.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// Projects pending outbox events into Mongo history, Redis hot views, task-list derivations, and SignalR broadcasts.
    /// </summary>
    public class OutboxProjector : IOutboxProjector
    {
        private readonly IJobDbContext _context;
        private readonly IRealtimeNotifier _hubContext;
        private readonly ILogger<OutboxProjector> _logger;
        private readonly IJobEventLog _eventLog;
        private readonly IJobHotView _hotView;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes the outbox projector.
        /// </summary>
        /// <param name="context">Database context used to read and acknowledge outbox events.</param>
        /// <param name="hubContext">SignalR hub used to broadcast projected job updates.</param>
        /// <param name="logger">Logger used for projection diagnostics.</param>
        /// <param name="eventLog">Store for the immutable job event history.</param>
        /// <param name="redis">Redis connection used for hot job views.</param>
        /// <param name="serviceProvider">Service provider used to resolve scoped dependencies for derived projections.</param>
        public OutboxProjector(
            IJobDbContext context, 
            IRealtimeNotifier hubContext, 
            ILogger<OutboxProjector> logger,
            IJobEventLog eventLog,
            IJobHotView hotView,
            IServiceProvider serviceProvider)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
            _eventLog = eventLog;
            _hotView = hotView;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Projects a bounded batch of pending outbox events across all read-model sinks.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The number of events fetched for projection in this batch.</returns>
        public async Task<int> ProjectPendingAsync(CancellationToken ct)
        {
            // Fetch pending events with a small buffer for late-committing transactions
            var cutoff = DateTime.UtcNow.AddMilliseconds(-200);
            var events = await _context.OutboxEvents
                .Where(e => e.ProcessedAt == null && e.OccurredAtUtc < cutoff)
                .OrderBy(e => e.Seq)
                .Take(50)
                .ToListAsync(ct);

            if (events.Count == 0) return 0;

            foreach (var ev in events)
            {
                try
                {
                    await DispatchEventAsync(ev, ct);
                    ev.ProcessedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to project outbox event {EventId}", ev.EventId);
                    ev.RetryCount++;
                    ev.LastError = ex.Message;
                }
            }

            await _context.SaveChangesAsync(ct);
            return events.Count;
        }

        private async Task DispatchEventAsync(OutboxEvent ev, CancellationToken ct)
        {
            _logger.LogInformation("Projecting event {EventType} for Job {JobId} (Seq: {Seq})", ev.EventType, ev.JobId, ev.Seq);

            if (string.Equals(ev.EventType, "PlanningSummary", StringComparison.Ordinal))
            {
                await ProjectToEventLogAsync(ev, ct);
                return;
            }

            var jobSnapshot = await LoadJobProjectionSnapshotAsync(ev.JobId, ct);

            // 1. Sink to the job event log (History)
            await ProjectToEventLogAsync(ev, ct);

            // 2. Sink to Redis (Hot View)
            await ProjectToRedisAsync(ev, jobSnapshot, ct);

            // 3. Update TaskList derivation
            await UpdateTaskListProjectionAsync(ev, ct);

            // 4. Broadcast to SignalR
            await BroadcastToSignalRAsync(ev, jobSnapshot);

            // 5. Maintain LastEventSeq on the BackgroundJob entity
            if (!string.IsNullOrEmpty(ev.JobId))
            {
                await AdvanceLastEventSeqAsync(_context, ev.JobId, ev.Seq, ct);
            }
        }

        /// <summary>
        /// Moves <c>LastEventSeq</c> of a job forward to <paramref name="seq"/>; an older or equal sequence number
        /// leaves the job untouched. Provider-neutral: it works on any relational provider.
        /// </summary>
        /// <returns>The number of jobs updated (0 or 1).</returns>
        public static Task<int> AdvanceLastEventSeqAsync(IJobDbContext context, string jobId, long seq, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return context.BackgroundJobs
                .Where(job => job.JobId == jobId && job.LastEventSeq < seq)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.LastEventSeq, seq)
                        .SetProperty(job => job.UpdatedAtUtc, now),
                    ct);
        }

        private async Task UpdateTaskListProjectionAsync(OutboxEvent ev, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ev.SessionId) || string.IsNullOrEmpty(ev.TaskId)) return;

            // Mapping EventType to CodexTaskStatus
            CodexTaskStatus? status = ev.EventType switch
            {
                "JobCreated" => CodexTaskStatus.Pending,
                "JobStarted" => CodexTaskStatus.Executing,
                "JobWaitingUser" => CodexTaskStatus.Executing,
                "JobCompleted" => CodexTaskStatus.Success,
                "JobFailed" => CodexTaskStatus.Failed,
                "JobCancelled" => CodexTaskStatus.Skipped,
                "JobRetried" => CodexTaskStatus.Pending,
                "JobResumed" => CodexTaskStatus.Pending,
                "JobRecoveryNeeded" => CodexTaskStatus.Failed,
                _ => null
            };

            if (status == null) return;

            try
            {
                // We need CodexSessionManager and ITaskListService (Scoped)
                using var scope = _serviceProvider.CreateScope();
                var sessionManager = scope.ServiceProvider.GetRequiredService<CodexSessionManager>();
                var taskListService = scope.ServiceProvider.GetRequiredService<ITaskListService>();

                var session = await sessionManager.GetExistingSessionAsync(ev.SessionId, "", "", (Uri?)null)
                    .ConfigureAwait(false)
                    ?? new CodexSession { Id = ev.SessionId };

                var snapshotJson = await taskListService.UpdateTaskStatusAsync(session, ev.TaskId, status.Value, null, ct).ConfigureAwait(false);
                _logger.LogDebug("Derived TaskList update for session {SessionId}, task {TaskId} status {Status}", ev.SessionId, ev.TaskId, status);

                // Push taskListUpdated SignalR event so the frontend TaskListPanel updates icons in real time.
                // This mirrors CodexOrchestrator.PublishTaskListAsync() but runs from the outbox path.
                if (!string.IsNullOrEmpty(snapshotJson))
                {
                    await _hubContext.SendToGroupAsync($"conv:{ev.SessionId}", "taskListUpdated", snapshotJson, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to derive TaskList update for session {SessionId}", ev.SessionId);
            }
        }

        private async Task ProjectToEventLogAsync(OutboxEvent ev, CancellationToken ct)
        {
            var entry = new JobEventLogEntry
            {
                EventId = ev.EventId,
                JobId = ev.JobId,
                SessionId = ev.SessionId,
                TaskId = ev.TaskId,
                Seq = ev.Seq,
                EventType = ev.EventType,
                PayloadJson = ev.Payload,
                OccurredAtUtc = ev.OccurredAtUtc
            };

            // Appending is idempotent per event id.
            await _eventLog.AppendAsync(entry, ct);
        }

        private async Task ProjectToRedisAsync(OutboxEvent ev, JobProjectionSnapshot? jobSnapshot, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ev.JobId)) return;

            // Only update if this seq is newer than what the hot view already holds (idempotency)
            var lastSeq = await _hotView.GetLatestSeqAsync(ev.JobId);
            if (lastSeq.HasValue && lastSeq.Value >= ev.Seq) return;

            // In a real implementation, we might fetch the latest job state from DB 
            // if the event itself doesn't contain enough info to build the full view.
            // For MVP, we at least update the status and sequence.
            
            var hashEntries = new List<HotViewField>
            {
                new HotViewField("LatestSeq", ev.Seq),
                new HotViewField("UpdatedAtUtc", DateTime.UtcNow.ToString("O"))
            };
            var fieldsToDelete = new List<string>();

            var statusValue = ev.EventType switch
            {
                "JobCreated" => "Queued",
                "JobStarted" => "Running",
                "JobCompleted" => "Completed",
                "JobFailed" => "Failed",
                "JobCancelled" => "Cancelled",
                "JobRetried" => "Queued",
                "JobResumed" => "Queued",
                "JobRecoveryNeeded" => "FailedRecoveryNeeded",
                "JobWaitingUser" => "WaitingUser",
                _ => null
            };

            if (statusValue != null)
            {
                hashEntries.Add(new HotViewField("Status", statusValue));
            }

            if (jobSnapshot != null)
            {
                if (!string.IsNullOrWhiteSpace(jobSnapshot.SessionId))
                {
                    hashEntries.Add(new HotViewField("SessionId", jobSnapshot.SessionId));
                }
                if (!string.IsNullOrWhiteSpace(jobSnapshot.TaskId))
                {
                    hashEntries.Add(new HotViewField("TaskId", jobSnapshot.TaskId));
                }
                if (!string.IsNullOrWhiteSpace(jobSnapshot.JobType))
                {
                    hashEntries.Add(new HotViewField("JobType", jobSnapshot.JobType));
                }
                if (!string.IsNullOrWhiteSpace(jobSnapshot.RunnerId))
                {
                    hashEntries.Add(new HotViewField("RunnerId", jobSnapshot.RunnerId));
                }
                if (!string.IsNullOrWhiteSpace(jobSnapshot.WorkerType))
                {
                    hashEntries.Add(new HotViewField("WorkerType", jobSnapshot.WorkerType));
                }
                else
                {
                    fieldsToDelete.Add("WorkerType");
                }

                var stateKind = ResolveProjectedStateKind(ev.EventType, jobSnapshot.StateKind);
                if (!string.IsNullOrWhiteSpace(stateKind))
                {
                    hashEntries.Add(new HotViewField("StateKind", stateKind));
                }
                else
                {
                    fieldsToDelete.Add("StateKind");
                }

                if (!string.IsNullOrWhiteSpace(jobSnapshot.Summary))
                {
                    hashEntries.Add(new HotViewField("Summary", jobSnapshot.Summary));
                    hashEntries.Add(new HotViewField("LatestMessage", jobSnapshot.Summary));
                }
                else
                {
                    fieldsToDelete.Add("Summary");
                    fieldsToDelete.Add("LatestMessage");
                }

                var projectedRecoveryNeeded = ResolveProjectedRecoveryNeeded(ev.EventType, jobSnapshot.RecoveryNeeded);
                hashEntries.Add(new HotViewField("RecoveryNeeded", projectedRecoveryNeeded));

                if (projectedRecoveryNeeded && !string.IsNullOrWhiteSpace(jobSnapshot.RecoveryReason))
                {
                    hashEntries.Add(new HotViewField("RecoveryReason", jobSnapshot.RecoveryReason));
                }
                else
                {
                    fieldsToDelete.Add("RecoveryReason");
                }

                if (projectedRecoveryNeeded && !string.IsNullOrWhiteSpace(jobSnapshot.ResumeStrategy))
                {
                    hashEntries.Add(new HotViewField("ResumeStrategy", jobSnapshot.ResumeStrategy));
                }
                else
                {
                    fieldsToDelete.Add("ResumeStrategy");
                }

                if (projectedRecoveryNeeded && !string.IsNullOrWhiteSpace(jobSnapshot.ResumeGuidance))
                {
                    hashEntries.Add(new HotViewField("ResumeGuidance", jobSnapshot.ResumeGuidance));
                }
                else
                {
                    fieldsToDelete.Add("ResumeGuidance");
                }
            }

            hashEntries.Add(new HotViewField("WaitingUser", ResolveProjectedWaitingUser(ev.EventType, statusValue, jobSnapshot)));

            if (ev.EventType == "JobWaitingUser")
            {
                var payload = TryParsePayload(ev.Payload);
                var waitingReason = payload?["waitingReason"]?.Value<string>();
                var resumeToken = payload?["resumeToken"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(waitingReason))
                {
                    hashEntries.Add(new HotViewField("WaitingReason", waitingReason));
                }
                if (!string.IsNullOrWhiteSpace(resumeToken))
                {
                    hashEntries.Add(new HotViewField("ResumeToken", resumeToken));
                }
            }
            else if (ev.EventType is "JobResumed" or "JobCompleted" or "JobFailed" or "JobCancelled" or "JobRecoveryNeeded")
            {
                // Cannot set RedisValue.Null in HashSetAsync — StackExchange.Redis rejects null values.
                // Use HashDeleteAsync to remove stale WaitingUser fields instead.
                fieldsToDelete.Add("WaitingReason");
                fieldsToDelete.Add("ResumeToken");
            }

            if (ev.EventType == "JobCreated")
            {
                hashEntries.Add(new HotViewField("JobId", ev.JobId));
                if (ev.SessionId != null) hashEntries.Add(new HotViewField("SessionId", ev.SessionId));
                if (ev.TaskId != null) hashEntries.Add(new HotViewField("TaskId", ev.TaskId));
                hashEntries.Add(new HotViewField("StateKind", WorkerProjectionHelpers.ResolveStateKindFromEvent(ev.EventType)));
            }

            await _hotView.ApplyAsync(
                ev.JobId,
                ToHotViewFields(hashEntries),
                fieldsToDelete.Distinct().ToArray(),
                TimeSpan.FromHours(24));
        }

        /// <summary>
        /// Turns the collected hash entries into hot-view fields. The same field can be collected twice (for example
        /// SessionId from the event and from the job snapshot); the later value wins, as it did when Redis applied them in order.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> ToHotViewFields(IEnumerable<HotViewField> entries)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                fields[entry.Name] = entry.Value;
            }

            return fields;
        }

        private async Task BroadcastToSignalRAsync(OutboxEvent ev, JobProjectionSnapshot? jobSnapshot)
        {
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var sessionId = ev.SessionId ?? jobSnapshot?.SessionId;
            var taskId = ev.TaskId ?? jobSnapshot?.TaskId;

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                groupIds.Add(sessionId);
            }

            if (!string.IsNullOrWhiteSpace(taskId))
            {
                groupIds.Add(taskId);
            }

            foreach (var groupId in groupIds)
            {
                var recoveryNeeded = ResolveProjectedRecoveryNeeded(ev.EventType, jobSnapshot?.RecoveryNeeded ?? false);
                await _hubContext.SendToGroupAsync($"conv:{groupId}", "OnJobUpdate", new 
                    { 
                        ev.JobId, 
                        SessionId = sessionId,
                        TaskId = taskId,
                        ev.EventType, 
                        ev.Payload,
                        WorkerType = jobSnapshot?.WorkerType,
                        StateKind = ResolveProjectedStateKind(ev.EventType, jobSnapshot?.StateKind),
                        Summary = jobSnapshot?.Summary,
                        WaitingUser = ResolveProjectedWaitingUser(ev.EventType, null, jobSnapshot),
                        RecoveryNeeded = recoveryNeeded,
                        RecoveryReason = recoveryNeeded ? jobSnapshot?.RecoveryReason : null,
                        ResumeStrategy = recoveryNeeded ? jobSnapshot?.ResumeStrategy : null,
                        ResumeGuidance = recoveryNeeded ? jobSnapshot?.ResumeGuidance : null,
                        ev.Seq,
                        ev.OccurredAtUtc 
                    });
            }
        }

        private async Task<JobProjectionSnapshot?> LoadJobProjectionSnapshotAsync(string? jobId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            var snapshot = await _context.BackgroundJobs
                .Where(j => j.JobId == jobId)
                .Select(j => new
                {
                    j.JobId,
                    j.SessionId,
                    j.TaskId,
                    j.JobType,
                    j.RunnerId,
                    j.Attempt,
                    Status = j.Status.ToString(),
                    j.PayloadJson,
                    j.ResultPayloadJson,
                    j.ShortSummary,
                    j.WaitingReason,
                    j.ErrorMessage,
                    LatestCheckpointStep = j.Checkpoints
                        .OrderByDescending(c => c.SequenceNumber)
                        .Select(c => c.CurrentStep)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            if (snapshot == null)
            {
                return null;
            }

            return new JobProjectionSnapshot(
                snapshot.JobId,
                snapshot.SessionId,
                snapshot.TaskId,
                snapshot.JobType,
                snapshot.RunnerId,
                snapshot.Status,
                WorkerProjectionHelpers.ResolveWorkerType(snapshot.JobType),
                WorkerProjectionHelpers.ResolveStateKind(
                    snapshot.Status,
                    snapshot.ResultPayloadJson,
                    snapshot.Attempt,
                    snapshot.LatestCheckpointStep),
                WorkerProjectionHelpers.ResolveRecoveryNeeded(snapshot.Status, snapshot.ResultPayloadJson),
                WorkerProjectionHelpers.ResolveRecoveryReason(snapshot.ResultPayloadJson),
                WorkerProjectionHelpers.ResolveResumeStrategy(snapshot.ResultPayloadJson),
                WorkerProjectionHelpers.ResolveResumeGuidance(snapshot.ResultPayloadJson),
                WorkerProjectionHelpers.BuildSummary(
                    snapshot.Status,
                    snapshot.PayloadJson,
                    snapshot.ResultPayloadJson,
                    snapshot.ShortSummary,
                    snapshot.WaitingReason,
                    snapshot.ErrorMessage));
        }

        private static JObject? TryParsePayload(string payload)
        {
            try
            {
                return string.IsNullOrWhiteSpace(payload) ? null : JObject.Parse(payload);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveProjectedStateKind(string? eventType, string? snapshotStateKind)
        {
            var eventStateKind = WorkerProjectionHelpers.ResolveStateKindFromEvent(eventType);
            return string.Equals(eventStateKind, "unknown", StringComparison.Ordinal)
                ? snapshotStateKind ?? "unknown"
                : eventStateKind;
        }

        private static bool ResolveProjectedWaitingUser(
            string? eventType,
            string? projectedStatus,
            JobProjectionSnapshot? jobSnapshot)
        {
            if (string.Equals(eventType, "JobWaitingUser", StringComparison.Ordinal)
                || string.Equals(projectedStatus, nameof(BackgroundJobStatus.WaitingUser), StringComparison.Ordinal))
            {
                return true;
            }

            if (ClearsWaitingUser(eventType))
            {
                return false;
            }

            return string.Equals(jobSnapshot?.Status, nameof(BackgroundJobStatus.WaitingUser), StringComparison.Ordinal);
        }

        private static bool ResolveProjectedRecoveryNeeded(string? eventType, bool snapshotRecoveryNeeded)
        {
            if (string.Equals(eventType, "JobRecoveryNeeded", StringComparison.Ordinal))
            {
                return true;
            }

            if (ClearsRecoveryNeeded(eventType))
            {
                return false;
            }

            return snapshotRecoveryNeeded;
        }

        private static bool ClearsWaitingUser(string? eventType)
            => eventType is "JobCompleted"
                or "JobFailed"
                or "JobCancelled"
                or "JobRetried"
                or "JobResumed"
                or "JobStarted"
                or "JobRecoveryNeeded";

        private static bool ClearsRecoveryNeeded(string? eventType)
            => eventType is "JobCompleted"
                or "JobFailed"
                or "JobCancelled"
                or "JobRetried"
                or "JobResumed"
                or "JobStarted";

        private sealed record JobProjectionSnapshot(
            string JobId,
            string? SessionId,
            string? TaskId,
            string JobType,
            string? RunnerId,
            string Status,
            string? WorkerType,
            string StateKind,
            bool RecoveryNeeded,
            string? RecoveryReason,
            string? ResumeStrategy,
            string? ResumeGuidance,
            string? Summary);
    }
}
