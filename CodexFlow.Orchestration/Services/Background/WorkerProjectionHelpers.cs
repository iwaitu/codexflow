// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Workers;
using CodexFlow.Application.Jobs.Dtos;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

internal static class WorkerProjectionHelpers
{
    public static string? ResolveWorkerType(string? jobType)
    {
        return WorkerJobTypes.TryResolveWorkerType(jobType, out var workerType)
            ? workerType.ToString().ToLowerInvariant()
            : null;
    }

    public static string? BuildSummary(
        string? status,
        string? payloadJson,
        string? resultPayloadJson,
        string? shortSummary,
        string? waitingReason,
        string? errorMessage)
    {
        if (!string.IsNullOrWhiteSpace(shortSummary))
        {
            return NormalizeSummary(shortSummary);
        }

        if (string.Equals(status, "WaitingUser", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(waitingReason))
        {
            return NormalizeSummary(waitingReason);
        }

        var resultPayload = ParseJson(resultPayloadJson);
        var resultSummary = resultPayload?["summary"]?.Value<string>()
            ?? resultPayload?["message"]?.Value<string>()
            ?? resultPayload?["result"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(resultSummary))
        {
            return NormalizeSummary(resultSummary);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return NormalizeSummary(errorMessage);
        }

        var payload = ParseJson(payloadJson);
        var promptSummary = payload?["summary"]?.Value<string>()
            ?? payload?["prompt"]?.Value<string>()
            ?? payload?["message"]?.Value<string>()
            ?? payload?["description"]?.Value<string>();

        return NormalizeSummary(promptSummary);
    }

    public static bool ResolveRecoveryNeeded(string? status, string? resultPayloadJson)
    {
        if (string.Equals(status, "FailedRecoveryNeeded", StringComparison.Ordinal))
        {
            return true;
        }

        return ParseJson(resultPayloadJson)?["recoveryNeeded"]?.Value<bool>() == true;
    }

    public static string ResolveStateKind(
        string? status,
        string? resultPayloadJson,
        int? attempt = null,
        string? latestCheckpointStep = null)
    {
        if (string.Equals(status, "WaitingUser", StringComparison.Ordinal))
        {
            return "waiting_user";
        }

        if (ResolveRecoveryNeeded(status, resultPayloadJson))
        {
            return "recovery_needed";
        }

        return status switch
        {
            "Queued" when IsResumeCheckpointStep(latestCheckpointStep) => "resume_queued",
            "Queued" when attempt.GetValueOrDefault() > 0 => "retry_queued",
            "Queued" => "queued",
            "Running" when IsResumeCheckpointStep(latestCheckpointStep) => "resume_running",
            "Running" when attempt.GetValueOrDefault() > 0 => "retry_running",
            "Running" => "running",
            "Completed" => "completed",
            "Failed" => "failed",
            "Cancelled" => "cancelled",
            _ => "unknown"
        };
    }

    public static string ResolveStateKindFromEvent(string? eventType)
    {
        return eventType switch
        {
            "JobCreated" => "queued",
            "JobRetried" => "retry_queued",
            "JobResumed" => "resume_queued",
            "JobStarted" => "running",
            "JobWaitingUser" => "waiting_user",
            "JobRecoveryNeeded" => "recovery_needed",
            "JobCompleted" => "completed",
            "JobFailed" => "failed",
            "JobCancelled" => "cancelled",
            _ => "unknown"
        };
    }

    public static string? ResolveRecoveryReason(string? resultPayloadJson)
        => NormalizeSummary(ParseJson(resultPayloadJson)?["recoveryReason"]?.Value<string>());

    public static string? ResolveResumeStrategy(string? resultPayloadJson)
        => NormalizeSummary(ParseJson(resultPayloadJson)?["resumeStrategy"]?.Value<string>());

    public static string? ResolveResumeGuidance(string? resultPayloadJson)
    {
        var payload = ParseJson(resultPayloadJson);
        return NormalizeSummary(
            payload?["resumeGuidance"]?.Value<string>()
            ?? payload?["resumePlaybook"]?["guidance"]?.Value<string>()
            ?? payload?["resumePlaybook"]?["nextAction"]?.Value<string>());
    }

    /// <summary>The structured failure of a failed job; null for any other status (a cancelled job did not fail).</summary>
    public static BackgroundJobFailureDto? ResolveFailure(string? status, string? errorMessage, string? resultPayloadJson)
    {
        if (status == "Completed" && BackgroundJobService.HasUncommittedCompletion(resultPayloadJson))
        {
            return new BackgroundJobFailureDto
            {
                Code = "artifact_commit_failed",
                Kind = "infrastructure",
                Remedy = "retry_with_guidance",
                Retryable = true,
                Message = "作业报告了改动，验收产物仍停在基线。请继续执行原作业。"
            };
        }

        if (status is not ("Failed" or "FailedRecoveryNeeded"))
        {
            return null;
        }

        var failure = CodexFlow.Core.Protocols.JobFailureClassifier.Classify(errorMessage, resultPayloadJson);
        return new BackgroundJobFailureDto
        {
            Code = failure.Code,
            Kind = ToWireName(failure.Kind.ToString()),
            Remedy = ToWireName(failure.Remedy.ToString()),
            Retryable = failure.Retryable || failure.CanRecheckRepositoryOwnership || failure.CanRecheckWorkspace,
            Message = failure.Message,
            RefusedPaths = failure.RefusedPaths.ToArray(),
            Evidence = failure.Evidence.ToArray()
        };
    }

    private static string ToWireName(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 4);
        foreach (var ch in pascal)
        {
            if (char.IsUpper(ch) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public static BackgroundJobVerificationDto? ResolveVerification(string? resultPayloadJson)
    {
        var payload = ParseJson(resultPayloadJson);
        if (payload == null)
        {
            return null;
        }

        var verdict = NormalizeVerificationValue(payload["verificationVerdict"]?.Value<string>());
        var passed = payload["verificationPassed"]?.Value<bool?>();
        var summary = NormalizeVerificationValue(payload["verificationSummary"]?.Value<string>());
        var evidence = ParseVerificationEvidence(payload["verificationEvidence"] as JArray);
        var issues = ParseVerificationIssues(payload["verificationIssues"] as JArray);
        var checklist = ParseVerificationChecklist(payload["verificationChecklist"] as JObject);

        if (passed is null
            && string.IsNullOrWhiteSpace(verdict)
            && string.IsNullOrWhiteSpace(summary)
            && evidence.Length == 0
            && issues.Length == 0
            && checklist == null)
        {
            return null;
        }

        return new BackgroundJobVerificationDto
        {
            Verdict = verdict ?? string.Empty,
            Passed = passed ?? string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase),
            Summary = summary ?? NormalizeVerificationValue(payload["summary"]?.Value<string>()),
            Issues = issues,
            Evidence = evidence,
            Checklist = checklist
        };
    }

    internal static JObject? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JObject.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= 160
            ? normalized
            : normalized[..157].TrimEnd() + "...";
    }

    private static bool IsResumeCheckpointStep(string? step)
        => string.Equals(step, "ResumedFromWaitingUser", StringComparison.Ordinal)
            || string.Equals(step, "RecoveryResumed", StringComparison.Ordinal);

    private static BackgroundJobVerificationEvidenceDto[] ParseVerificationEvidence(JArray? array)
    {
        if (array == null || array.Count == 0)
        {
            return [];
        }

        return array
            .OfType<JObject>()
            .Select(item => new BackgroundJobVerificationEvidenceDto
            {
                Check = NormalizeVerificationValue(item["check"]?.Value<string>()) ?? "unspecified-check",
                Passed = item["passed"]?.Value<bool?>() ?? false,
                Command = NormalizeVerificationValue(item["command"]?.Value<string>()),
                ExitCode = NormalizeVerificationValue(item["exitCode"]?.Value<string>())
                    ?? NormalizeVerificationValue(item["exit_code"]?.Value<string>()),
                Observation = NormalizeVerificationValue(item["observation"]?.Value<string>())
            })
            .ToArray();
    }

    private static string[] ParseVerificationIssues(JArray? array)
    {
        if (array == null || array.Count == 0)
        {
            return [];
        }

        return array
            .Values<string?>()
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static BackgroundJobVerificationChecklistDto? ParseVerificationChecklist(JObject? checklist)
    {
        if (checklist == null)
        {
            return null;
        }

        var completedItems = ParseVerificationChecklistItems(checklist["completedItems"] as JArray);
        var failedItems = ParseVerificationChecklistItems(checklist["failedItems"] as JArray);
        var pendingItems = ParseVerificationChecklistItems(checklist["pendingItems"] as JArray);
        var focusItems = checklist["focusItems"]?
            .Values<string?>()
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];

        if (completedItems.Length == 0 && failedItems.Length == 0 && pendingItems.Length == 0 && focusItems.Length == 0)
        {
            return null;
        }

        return new BackgroundJobVerificationChecklistDto
        {
            CompletedItems = completedItems,
            FailedItems = failedItems,
            PendingItems = pendingItems,
            FocusItems = focusItems
        };
    }

    private static BackgroundJobVerificationChecklistItemDto[] ParseVerificationChecklistItems(JArray? array)
    {
        if (array == null || array.Count == 0)
        {
            return [];
        }

        return array
            .OfType<JObject>()
            .Select(item => new BackgroundJobVerificationChecklistItemDto
            {
                Check = NormalizeVerificationValue(item["check"]?.Value<string>()) ?? "unspecified-check",
                Evidence = item["evidence"]?
                    .Values<string?>()
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .Select(static line => line!.Trim())
                    .ToArray()
                    ?? []
            })
            .ToArray();
    }

    private static string? NormalizeVerificationValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
