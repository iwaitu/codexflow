// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

/// <summary>
/// A forge attempt that ended without changing the project (notes only, an edit that was only previewed, exploration that never wrote) gets one more
/// attempt, told what the first one did. Industry practice for output validation is a bounded retry that feeds the failure back (CrewAI guardrails
/// re-run the task with the guardrail's error, default 3). Here the bound is 1 and the facts come from the job's own result, not from wording.
/// </summary>
internal static class ForgeQualityRetry
{
    public const int MaxRetries = 1;
    private const int ReplyExcerptChars = 400;

    // Terminal detail codes of an attempt that ended with the project unchanged and whose cause a different attempt can plausibly change.
    private static readonly HashSet<string> RetryableDetailCodes = new(StringComparer.Ordinal)
    {
        "no_relevant_change",
        ForgeDetailCode.NoPersistedChanges,
        "dependency_contract_unmet",
        "required_tool_contract_violation",
        "recovery_exhausted_unexecuted_write_intent",
        "recovery_exhausted",
        "max_rounds_reached"
    };

    public static bool ShouldRetry(JObject? result, JObject? payload)
    {
        if (result == null || result["success"]?.Value<bool?>() == true ||
            ForgeFailureContext.IsVerificationFailure(result) ||
            result["scopeDenials"] is JArray { Count: > 0 } ||
            (result["changedFiles"] is JArray { Count: > 0 } &&
             result["detailCode"]?.Value<string>() is "recovery_exhausted" or "recovery_exhausted_unexecuted_write_intent" or "max_rounds_reached"))
        {
            return false;
        }

        var detailCode = result["detailCode"]?.Value<string>();
        return detailCode != null &&
               RetryableDetailCodes.Contains(detailCode) &&
               (payload?["qualityRetryCount"]?.Value<int?>() ?? 0) < MaxRetries;
    }

    public static string Reason(JObject result) => result["detailCode"]?.Value<string>() == "dependency_contract_unmet"
        ? "dependency_contract_unmet"
        : "forge_attempt_changed_nothing";

    /// <summary>Appends the facts of the failed attempt to the task prompt (the original prompt is kept and never nested) and counts the retry.</summary>
    public static void Apply(JObject payload, JObject result)
    {
        var original = payload["originalPrompt"]?.Value<string>()
            ?? payload["prompt"]?.Value<string>()
            ?? payload["message"]?.Value<string>()
            ?? string.Empty;
        payload["originalPrompt"] = original;
        payload["prompt"] = original + "\n\n" + BuildFeedback(result);
        payload["qualityRetryCount"] = (payload["qualityRetryCount"]?.Value<int?>() ?? 0) + 1;

        // The first attempt's commit is what this retry exists to repair, not a finished result to carry over. The runner finalizes a retry from
        // "lastCommitHash + lastChangedFiles" without starting the worker (that is for an infrastructure failure after a good commit), so left in
        // place they made this retry accept the very result the host had just judged unmet, with the feedback never delivered. The worktree the
        // attempt left behind is still reused (shadowWorktreePath); the attempt that follows records its own commit.
        payload.Remove("lastCommitHash");
        payload.Remove("lastChangedFiles");

        // The feedback tells the worker to change these projects; the attempt's file scope must be able to allow them (see the runner).
        payload.Remove("retryUnmetProjects");
        if (result["unmetPackages"] is JArray unmet && unmet.Count > 0)
        {
            var projects = unmet.OfType<JObject>()
                .Select(static u => u["project"]?.Value<string>())
                .OfType<string>()
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToArray();
            if (projects.Length > 0)
            {
                payload["retryUnmetProjects"] = new JArray(projects);
            }
        }
    }

    private static string BuildFeedback(JObject result)
    {
        var changed = result["changedFiles"] is JArray files
            ? string.Join(", ", files.Select(static f => f.ToString()).Take(8))
            : string.Empty;
        var reply = result["result"]?.Value<string>() ?? string.Empty;
        if (reply.Length > ReplyExcerptChars)
        {
            reply = reply[..ReplyExcerptChars] + "…";
        }

        if (result["unmetPackages"] is JArray unmet && unmet.Count > 0)
        {
            var lines = string.Concat(unmet.OfType<JObject>().Take(20).Select(static u =>
                $"- {u["package"]}（{u["project"]}）现在是 {u["actual"]}，必须升到 {u["expected"]}\n"));
            return "[上一次尝试没有完成依赖升级]\n" +
                   "下列包在项目文件里仍低于宿主选定并已取包的版本，必须逐个改到该版本（保留你已经做完的部分，只补这些），然后还原、构建、测试：\n" +
                   lines;
        }

        return "[上一次尝试没有改动项目]\n" +
               $"- 上一次尝试共调用工具 {result["totalToolCalls"]?.Value<int?>() ?? 0} 次，其中写入类 {result["writeToolCalls"]?.Value<int?>() ?? 0} 次；结束原因：{result["detailCode"]?.Value<string>()}。\n" +
               (changed.Length > 0 ? $"- 留下的文件：{changed}（说明类文件不算对项目的改动）。\n" : string.Empty) +
               (reply.Length > 0 ? $"- 它最后的回复：{reply}\n" : string.Empty) +
               "这一次：少量阅读后立即用写入工具修改项目文件（不要带 dryRun），再还原、构建、测试；不要只写说明文件。" +
               "任务里提到的宿主已取包、预取完成等前提由宿主保证并在事后检查，不要等待任何人确认，也不要因此停手；缺包时照样修改并如实报告还原结果。";
    }
}
