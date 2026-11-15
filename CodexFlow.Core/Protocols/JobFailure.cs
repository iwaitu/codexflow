// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Protocols;

/// <summary>What kind of thing went wrong, in terms of who can do something about it.</summary>
public enum JobFailureKind
{
    /// <summary>The model provider or the network failed; the work itself was not judged.</summary>
    Transient,

    /// <summary>The worker ran and did not deliver: no change, stalled, ran out of rounds, a declared package left at the wrong version.</summary>
    Quality,

    /// <summary>The scope guard refused the files the task needs; the same scope refuses them again.</summary>
    ScopeBoundary,

    /// <summary>The host decided on facts a retry does not change: a version it cannot restore offline, a plan nobody approved, a session that is not the caller's.</summary>
    HostDecision,

    /// <summary>The environment is broken: git, the provider's parameters, the verification services, an exception.</summary>
    Infrastructure,

    /// <summary>A code this version does not know.</summary>
    Unknown
}

/// <summary>What would change the outcome.</summary>
public enum JobFailureRemedy
{
    /// <summary>The same job, as it was.</summary>
    RetryAsIs,

    /// <summary>The same job with the failure in front of the worker: a different approach, not a repetition.</summary>
    RetryWithGuidance,

    /// <summary>Not a retry: the plan, the task's declared files or the requested versions have to change.</summary>
    ChangePlanOrScope,

    /// <summary>Not a retry and not the coordinator's call: the user approves, chooses, or takes the step.</summary>
    AskUser,

    /// <summary>Not a retry: something outside the job has to be repaired first.</summary>
    FixEnvironment
}

/// <summary>
/// A failed job as the coordinator needs to read it: a code, a kind, the evidence (the paths the scope guard refused, the packages that came
/// out wrong, the worker's own report) and the remedy that can change the outcome. Free-text messages stay for people; this is for deciding.
/// </summary>
public sealed record JobFailure(
    string Code,
    JobFailureKind Kind,
    JobFailureRemedy Remedy,
    string Message,
    IReadOnlyList<string> RefusedPaths,
    IReadOnlyList<string> Evidence)
{
    /// <summary>True when running the job again, as it was or with guidance, can give a different result.</summary>
    public bool Retryable => Remedy is JobFailureRemedy.RetryAsIs or JobFailureRemedy.RetryWithGuidance;

    /// <summary>The failure as a block of text for a model or a person: code, reason, evidence, and what would change the outcome.</summary>
    public string ToPromptText()
    {
        var builder = new StringBuilder();
        builder.Append("失败码：").Append(Code).Append('（').Append(KindText(Kind)).AppendLine("）");
        if (!string.IsNullOrWhiteSpace(Message))
        {
            builder.Append("原因：").AppendLine(Message);
        }

        if (RefusedPaths.Count > 0)
        {
            builder.Append("范围守卫拒绝的文件：").AppendLine(string.Join("、", RefusedPaths));
        }

        foreach (var item in Evidence)
        {
            builder.Append("- ").AppendLine(item);
        }

        builder.Append("怎么才能改变结果：").Append(RemedyText(Remedy, RefusedPaths.Count > 0));
        return builder.ToString();
    }

    private static string KindText(JobFailureKind kind) => kind switch
    {
        JobFailureKind.Transient => "临时性故障，与工作内容无关",
        JobFailureKind.Quality => "工作者没有交付",
        JobFailureKind.ScopeBoundary => "任务范围拒绝了它需要改的文件",
        JobFailureKind.HostDecision => "宿主按事实作出的判定",
        JobFailureKind.Infrastructure => "运行环境故障",
        _ => "未识别的失败"
    };

    private static string RemedyText(JobFailureRemedy remedy, bool hasRefusedPaths) => remedy switch
    {
        JobFailureRemedy.RetryAsIs => "原样重试即可。",
        JobFailureRemedy.RetryWithGuidance => "可以重试，但要带上这次的原因，换一种做法，不要原样重复。",
        JobFailureRemedy.ChangePlanOrScope => hasRefusedPaths
            ? "重试不会改变结果（同一个范围会再次拒绝这些文件）：需要改计划，把这些文件放进任务声明的输出，或者调整请求的范围，再由用户批准。"
            : "重试不会改变结果：需要改计划、任务的范围或请求的版本，再由用户批准。",
        JobFailureRemedy.AskUser => "重试不会改变结果，也不是协调者能决定的：需要用户批准、选择或亲自处理。",
        _ => "重试不会改变结果：先修复运行环境。"
    };
}

/// <summary>Reads a failed job's status text and result payload into a <see cref="JobFailure"/>.</summary>
public static class JobFailureClassifier
{
    /// <summary>The longest message kept.</summary>
    public const int MaxMessageChars = 600;

    private const int MaxListItems = 20;

    private static readonly Regex CodeInMessage = new(@"DetailCode=([^,\]\s]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Transient = new(StringComparer.OrdinalIgnoreCase)
    {
        "transient_provider_failure", "transport_failure", "retry_not_before_deferred", "host_shutdown"
    };

    private static readonly HashSet<string> Quality = new(StringComparer.OrdinalIgnoreCase)
    {
        "no_relevant_change", "no_persisted_changes", "forge_attempt_changed_nothing", "stall_detected", "max_rounds_reached", "empty_response", "malformed_protocol",
        "claimed_changes_mismatch", "unexecuted_write_intent", "dependency_contract_unmet", "continuation_budget_exhausted", "recovery_exhausted",
        "worker_failed", "task_mismatch", "commit_not_full_sha"
    };

    private static readonly HashSet<string> WorkerBlocked = new(StringComparer.OrdinalIgnoreCase) { "worker_reported_blocked" };

    private static readonly HashSet<string> HostDecidesAndNeedsTheUser = new(StringComparer.OrdinalIgnoreCase)
    {
        "plan_not_approved", "plan_binding_missing", "session_not_owned_by_user", "missing_workspace_metadata", "identity_unbound",
        "plan_changed", "PlanResetRequired", "PlanPatchPending"
    };

    private static readonly HashSet<string> HostDecidesAboutScope = new(StringComparer.OrdinalIgnoreCase)
    {
        "offline_restore_unavailable"
    };

    private static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider_parameter_incompatibility", "tool_choice_required_in_thinking_mode", "git_service_unavailable", "verification_services_unavailable",
        "baseline_unavailable", "manifest_unavailable", "worker_infrastructure_error", "retained_worktree_missing", "retained_artifact_check_failed",
        "worktree_not_bound_to_task", "ShadowWorktreeResidueNotDiscarded"
    };

    /// <summary>Classifies a failed job from its error text and its result payload (either may be missing).</summary>
    public static JobFailure Classify(string? errorMessage, string? resultPayloadJson)
    {
        var payload = Parse(resultPayloadJson);
        var code = Text(payload, "detailCode") ?? Text(payload, "failureCode") ?? CodeFromMessage(errorMessage) ?? "unknown";
        var message = Bound(Text(payload, "message") ?? Text(payload, "detailMessage") ?? errorMessage ?? string.Empty);

        var refused = RefusedPathsOf(payload);
        var evidence = EvidenceOf(payload);
        var (kind, remedy) = Decide(code, refused.Count > 0);

        return new JobFailure(code, kind, remedy, message, refused, evidence);
    }

    private static (JobFailureKind Kind, JobFailureRemedy Remedy) Decide(string code, bool guardRefusedPaths)
    {
        if (Transient.Contains(code))
        {
            return (JobFailureKind.Transient, JobFailureRemedy.RetryAsIs);
        }

        if (string.Equals(code, "scope_violation", StringComparison.OrdinalIgnoreCase))
        {
            return (JobFailureKind.ScopeBoundary, JobFailureRemedy.ChangePlanOrScope);
        }

        if (HostDecidesAboutScope.Contains(code))
        {
            return (JobFailureKind.HostDecision, JobFailureRemedy.ChangePlanOrScope);
        }

        if (HostDecidesAndNeedsTheUser.Contains(code) || code.StartsWith("WorkerExecutionNotAuthorized", StringComparison.OrdinalIgnoreCase))
        {
            return (JobFailureKind.HostDecision, JobFailureRemedy.AskUser);
        }

        if (Infrastructure.Contains(code) || code.StartsWith("WorkspaceUnavailable", StringComparison.OrdinalIgnoreCase) || LooksLikeException(code))
        {
            return (JobFailureKind.Infrastructure, JobFailureRemedy.FixEnvironment);
        }

        // The guard refused files and the worker then stopped, stalled or reported itself blocked: it was not allowed to do what the task needs.
        if (guardRefusedPaths)
        {
            return (JobFailureKind.ScopeBoundary, JobFailureRemedy.ChangePlanOrScope);
        }

        if (Quality.Contains(code) || WorkerBlocked.Contains(code))
        {
            return (JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance);
        }

        return (JobFailureKind.Unknown, JobFailureRemedy.RetryWithGuidance);
    }

    private static bool LooksLikeException(string code)
    {
        var colon = code.IndexOf(':');
        var head = colon > 0 ? code[..colon] : code;
        return head.EndsWith("Exception", StringComparison.Ordinal);
    }

    private static List<string> RefusedPathsOf(JObject? payload)
    {
        var paths = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path.Trim(), StringComparer.OrdinalIgnoreCase) && paths.Count < MaxListItems)
            {
                paths.Add(path.Trim());
            }
        }

        if (payload?["scopeDenials"] is JArray denials)
        {
            foreach (var denial in denials.OfType<JObject>())
            {
                Add(denial["path"]?.Value<string>());
            }
        }

        if (payload?["outOfScopeFiles"] is JArray files)
        {
            foreach (var file in files)
            {
                Add(file.Type == JTokenType.String ? file.Value<string>() : null);
            }
        }

        return paths;
    }

    private static List<string> EvidenceOf(JObject? payload)
    {
        var evidence = new List<string>();
        if (payload?["unmetPackages"] is JArray unmet)
        {
            foreach (var item in unmet.OfType<JObject>().Take(MaxListItems))
            {
                var package = item["package"]?.Value<string>() ?? "?";
                var project = item["project"]?.Value<string>();
                var expected = item["expected"]?.Value<string>() ?? "?";
                var actual = item["actual"]?.Value<string>() ?? "?";
                evidence.Add($"依赖未到位：{package} 应为 {expected}，实际 {actual}" + (string.IsNullOrWhiteSpace(project) ? string.Empty : $"（{project}）"));
            }
        }

        if (payload?["workerReport"] is JObject report)
        {
            var reason = report["reason"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                evidence.Add("工作者自己的说明：" + Bound(reason));
            }

            if (report["unfinished"] is JArray unfinished)
            {
                foreach (var item in unfinished.Values<string?>().Where(static s => !string.IsNullOrWhiteSpace(s)).Take(MaxListItems))
                {
                    evidence.Add("工作者没做完：" + Bound(item!));
                }
            }
        }

        return evidence;
    }

    private static string? CodeFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = CodeInMessage.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? Text(JObject? payload, string name)
    {
        var value = payload?[name];
        if (value is null || value.Type != JTokenType.String)
        {
            return null;
        }

        var text = value.Value<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static JObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JObject.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static string Bound(string text) => text.Length <= MaxMessageChars ? text : text[..MaxMessageChars] + "…";
}
