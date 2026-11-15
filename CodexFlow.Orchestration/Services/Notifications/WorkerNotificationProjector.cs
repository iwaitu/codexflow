// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
using CodexFlow.Application.Notifications;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Domain.Jobs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Notifications;

/// <summary>
/// Projects background job outcomes into user-facing notification payloads.
/// </summary>
public sealed class WorkerNotificationProjector : IWorkerNotificationProjector
{
    private readonly IWorkerNotificationSerializer _serializer;

    /// <summary>
    /// Initializes the worker notification projector.
    /// </summary>
    /// <param name="serializer">Serializer used to convert notification envelopes into transport payloads.</param>
    public WorkerNotificationProjector(IWorkerNotificationSerializer serializer)
    {
        _serializer = serializer;
    }

    /// <summary>
    /// Builds the standard completion or failure notification payload for a worker job.
    /// </summary>
    /// <param name="job">Background job whose outcome should be projected.</param>
    /// <param name="resultPayloadJson">Optional explicit result payload that overrides <see cref="BackgroundJob.ResultPayloadJson"/>.</param>
    /// <returns>Serialized notification payload suitable for delivery channels.</returns>
    public string ProjectJobNotification(BackgroundJob job, string? resultPayloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = ParsePayload(resultPayloadJson ?? job.ResultPayloadJson);
        var jobPayload = ParsePayload(job.PayloadJson);
        var workerType = ResolveWorkerType(job, payload);
        if (workerType == WorkerType.Verify && TryBuildVerificationReportEnvelope(job, payload, out var verificationReport))
        {
            return _serializer.Serialize(verificationReport);
        }

        var envelope = new WorkerNotificationEnvelope
        {
            TaskId = job.TaskId ?? job.JobId,
            JobId = job.JobId,
            WorkerType = workerType,
            Status = ResolveWorkerStatus(job.Status),
            Summary = ResolveSummary(job, payload),
            Result = ResolveResult(payload, job.Status),
            ResumeToken = ResolveResumeToken(payload),
            Usage = ResolveUsage(payload),
            Worktree = ResolveWorktree(payload, jobPayload),
            Recovery = ResolveRecovery(payload),
            CompletedAtUtc = ResolveCompletedAtUtc(job)
        };

        return _serializer.Serialize(envelope);
    }

    /// <summary>
    /// Builds a waiting-user notification that carries resume instructions back to the client.
    /// </summary>
    /// <param name="job">Background job requesting user input.</param>
    /// <param name="resumeToken">Resume token required to continue the job.</param>
    /// <param name="reason">Human-readable explanation shown to the user.</param>
    /// <param name="context">Optional extra context that helps the user decide how to resume.</param>
    /// <returns>Serialized waiting-user notification payload.</returns>
    public string ProjectWaitingUserNotification(BackgroundJob job, string resumeToken, string reason, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var envelope = new WaitingUserEnvelope
        {
            TaskId = job.TaskId ?? job.JobId,
            JobId = job.JobId,
            ResumeToken = resumeToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? "User confirmation required" : reason.Trim(),
            Context = string.IsNullOrWhiteSpace(context) ? null : context
        };

        return _serializer.Serialize(envelope);
    }

    /// <summary>
    /// Builds a standalone verification report payload.
    /// </summary>
    /// <param name="taskId">Logical task identifier associated with the verification run.</param>
    /// <param name="jobId">Background job identifier.</param>
    /// <param name="validationResult">Structured validation result produced by the verify worker.</param>
    /// <param name="context">Optional narrative context to include as evidence.</param>
    /// <param name="usage">Optional token or resource usage metadata.</param>
    /// <returns>Serialized verification report payload.</returns>
    public string ProjectVerificationReport(
        string taskId,
        string jobId,
        ValidationResult validationResult,
        string? context = null,
        WorkerUsageInfo? usage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(validationResult);

        var issues = validationResult.Issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Select(issue => issue.Trim())
            .ToList();

        var evidence = BuildVerificationEvidence(validationResult, context);
        var envelope = new VerificationReportEnvelope
        {
            TaskId = taskId,
            JobId = jobId,
            Verdict = validationResult.Success ? "pass" : "fail",
            Passed = validationResult.Success,
            Summary = validationResult.Summary,
            Evidence = evidence,
            Issues = issues,
            Usage = usage
        };

        return _serializer.Serialize(envelope);
    }

    private static List<VerificationEvidence> BuildVerificationEvidence(ValidationResult validationResult, string? context)
    {
        var evidence = new List<VerificationEvidence>();

        if (!string.IsNullOrWhiteSpace(context))
        {
            evidence.Add(new VerificationEvidence
            {
                Check = "validation-context",
                Passed = validationResult.Success,
                Observation = context.Trim()
            });
        }

        if (validationResult.WarningDetails?.Count > 0)
        {
            evidence.Add(new VerificationEvidence
            {
                Check = "quality-warnings",
                Passed = !validationResult.HasQualityWarnings,
                Observation = string.Join(Environment.NewLine, validationResult.WarningDetails)
            });
        }

        if (validationResult.ChecklistEvaluation is { } checklist)
        {
            foreach (var item in checklist.CompletedItems.Concat(checklist.FailedItems).Concat(checklist.PendingItems))
            {
                checklist.EvidenceByItem.TryGetValue(item.ItemId, out var itemEvidence);
                evidence.Add(new VerificationEvidence
                {
                    Check = $"{item.Text} ({item.ItemId})",
                    Passed = item.Status == TaskChecklistItemStatus.Completed,
                    Observation = itemEvidence == null || itemEvidence.Count == 0
                        ? null
                        : string.Join(Environment.NewLine, itemEvidence)
                });
            }
        }

        return evidence;
    }

    private static JObject? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JObject.Parse(payloadJson);
        }
        catch
        {
            return null;
        }
    }

    private static WorkerType ResolveWorkerType(BackgroundJob job, JObject? payload)
    {
        var workerType = payload?["workerType"]?.Value<string>() ?? job.JobType;
        return workerType.Trim().ToLowerInvariant() switch
        {
            "explore" or "exploreworker" => WorkerType.Explore,
            "plan" or "planworker" => WorkerType.Plan,
            "verify" or "verifyworker" => WorkerType.Verify,
            "forge" or "executecodetask" => WorkerType.Forge,
            _ => WorkerType.Forge
        };
    }

    private static WorkerStatus ResolveWorkerStatus(BackgroundJobStatus status) =>
        status switch
        {
            BackgroundJobStatus.Completed => WorkerStatus.Completed,
            BackgroundJobStatus.WaitingUser => WorkerStatus.WaitingUser,
            _ => WorkerStatus.Failed
        };

    private static string ResolveSummary(BackgroundJob job, JObject? payload)
    {
        var summary = payload?["summary"]?.Value<string>()
            ?? payload?["message"]?.Value<string>()
            ?? job.ShortSummary
            ?? job.WaitingReason
            ?? job.ErrorMessage
            ?? BuildFallbackSummary(job);

        return summary.Trim();
    }

    private static string? ResolveResult(JObject? payload, BackgroundJobStatus status)
    {
        var explicitResult = payload?["result"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(explicitResult))
        {
            return explicitResult;
        }

        if (status == BackgroundJobStatus.Failed)
        {
            var diagnostics = payload?["serviceDiagnostics"];
            if (diagnostics != null)
            {
                return diagnostics.ToString(Formatting.Indented);
            }

            var message = payload?["message"]?.Value<string>() ?? payload?["error"]?.Value<string>();
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }

        return null;
    }

    private static string? ResolveResumeToken(JObject? payload)
    {
        var resumeToken = payload?["resumeToken"]?.Value<string>();
        return string.IsNullOrWhiteSpace(resumeToken) ? null : resumeToken;
    }

    private static WorkerUsageInfo? ResolveUsage(JObject? payload)
    {
        if (payload == null)
        {
            return null;
        }

        var durationMs = payload["durationMs"]?.Value<int?>() ?? 0;
        var toolCalls = payload["totalToolCalls"]?.Value<int?>()
            ?? payload["toolCalls"]?.Value<int?>()
            ?? 0;
        var writeToolCalls = payload["writeToolCalls"]?.Value<int?>() ?? 0;
        var inputTokens = payload["inputTokens"]?.Value<int?>() ?? 0;
        var outputTokens = payload["outputTokens"]?.Value<int?>() ?? 0;

        if (durationMs == 0 && toolCalls == 0 && writeToolCalls == 0 && inputTokens == 0 && outputTokens == 0)
        {
            return null;
        }

        return new WorkerUsageInfo
        {
            DurationMs = durationMs,
            ToolCalls = toolCalls,
            WriteToolCalls = writeToolCalls,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };
    }

    private static WorkerWorktreeInfo? ResolveWorktree(JObject? resultPayload, JObject? jobPayload)
    {
        var worktreePath = resultPayload?["worktreePath"]?.Value<string>()
            ?? jobPayload?["shadowWorktreePath"]?.Value<string>()
            ?? jobPayload?["lastShadowWorktreePath"]?.Value<string>();
        var commitHash = resultPayload?["commitHash"]?.Value<string>()
            ?? jobPayload?["lastCommitHash"]?.Value<string>();
        var retained = resultPayload?["worktreeRetained"]?.Value<bool?>()
            ?? jobPayload?["worktreeRetained"]?.Value<bool?>()
            ?? !string.IsNullOrWhiteSpace(worktreePath);
        var changedFiles = resultPayload?["changedFiles"]?.Values<string?>()
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .ToArray()
            ?? jobPayload?["lastChangedFiles"]?.Values<string?>()
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!)
                .ToArray()
            ?? [];

        if (string.IsNullOrWhiteSpace(worktreePath) && string.IsNullOrWhiteSpace(commitHash) && changedFiles.Length == 0 && !retained)
        {
            return null;
        }

        return new WorkerWorktreeInfo
        {
            Path = worktreePath,
            Retained = retained,
            CommitHash = commitHash,
            ChangedFiles = changedFiles
        };
    }

    private static WorkerRecoveryInfo? ResolveRecovery(JObject? payload)
    {
        if (payload == null || payload["recoveryNeeded"]?.Value<bool>() != true)
        {
            return null;
        }

        var playbook = payload["resumePlaybook"] as JObject;
        var runtimeFlags = playbook?["runtimeFlags"]?
            .Values<string?>()
            .Where(static flag => !string.IsNullOrWhiteSpace(flag))
            .Select(static flag => flag!.Trim())
            .ToArray()
            ?? payload["runtimeFlags"]?
                .Values<string?>()
                .Where(static flag => !string.IsNullOrWhiteSpace(flag))
                .Select(static flag => flag!.Trim())
                .ToArray()
            ?? [];
        var steps = playbook?["steps"]?
            .Values<string?>()
            .Where(static step => !string.IsNullOrWhiteSpace(step))
            .Select(static step => step!.Trim())
            .ToArray()
            ?? [];
        var checks = playbook?["checks"]?
            .Values<string?>()
            .Where(static check => !string.IsNullOrWhiteSpace(check))
            .Select(static check => check!.Trim())
            .ToArray()
            ?? [];

        return new WorkerRecoveryInfo
        {
            Reason = payload["recoveryReason"]?.Value<string>(),
            ResumeStrategy = payload["resumeStrategy"]?.Value<string>(),
            Guidance = payload["resumeGuidance"]?.Value<string>()
                ?? playbook?["guidance"]?.Value<string>()
                ?? playbook?["nextAction"]?.Value<string>(),
            RuntimeFlags = runtimeFlags,
            Steps = steps,
            Checks = checks
        };
    }

    private static bool TryBuildVerificationReportEnvelope(
        BackgroundJob job,
        JObject? payload,
        out VerificationReportEnvelope envelope)
    {
        var summary = payload?["verificationSummary"]?.Value<string>()
            ?? payload?["summary"]?.Value<string>();
        var verdict = payload?["verificationVerdict"]?.Value<string>();
        var evidenceArray = payload?["verificationEvidence"] as JArray;
        var issueArray = payload?["verificationIssues"] as JArray;

        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(verdict))
        {
            envelope = default!;
            return false;
        }

        var evidence = evidenceArray?
            .OfType<JObject>()
            .Select(item => new VerificationEvidence
            {
                Check = item["check"]?.Value<string>() ?? "unspecified-check",
                Passed = item["passed"]?.Value<bool?>() ?? false,
                Command = item["command"]?.Value<string>(),
                ExitCode = item["exitCode"]?.Value<string>() ?? item["exit_code"]?.Value<string>(),
                Observation = item["observation"]?.Value<string>()
            })
            .ToList()
            ?? [];

        var issues = issueArray?
            .Values<string?>()
            .Where(static issue => !string.IsNullOrWhiteSpace(issue))
            .Select(static issue => issue!.Trim())
            .ToList()
            ?? [];

        envelope = new VerificationReportEnvelope
        {
            TaskId = job.TaskId ?? job.JobId,
            JobId = job.JobId,
            Verdict = verdict.Trim().ToLowerInvariant(),
            Passed = payload?["verificationPassed"]?.Value<bool?>() ?? string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase),
            Summary = summary.Trim(),
            Evidence = evidence,
            Issues = issues,
            Usage = ResolveUsage(payload)
        };

        return true;
    }

    private static DateTime ResolveCompletedAtUtc(BackgroundJob job)
    {
        if (job.FinishedAtUtc.HasValue)
        {
            return job.FinishedAtUtc.Value;
        }

        if (job.UpdatedAtUtc != default)
        {
            return job.UpdatedAtUtc;
        }

        return DateTime.UtcNow;
    }

    private static string BuildFallbackSummary(BackgroundJob job) =>
        job.Status switch
        {
            BackgroundJobStatus.Completed => $"Worker {job.JobType} completed.",
            BackgroundJobStatus.WaitingUser => $"Worker {job.JobType} is waiting for user input.",
            _ => $"Worker {job.JobType} failed."
        };
}
