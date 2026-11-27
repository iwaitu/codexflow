// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Planning.Artifacts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

internal static class PlanTaskRetryBinding
{
    public static bool CanRetry(JObject payload, PlanArtifact plan, string taskId)
        => plan.Status is PlanArtifactStatus.ReadyToExecute or PlanArtifactStatus.Executing &&
           plan.Approval is { } approval &&
           approval.ApprovedPlanVersion == plan.Version &&
           approval.ApprovedMarkdownHash == PlanArtifactSupport.ComputeMarkdownHash(plan.Markdown) &&
           approval.ApprovedByUserId == payload.Value<string>("userId") &&
           plan.Tasks.Any(task => task.Id == taskId) &&
           Matches(payload, plan, approval.ApprovedMarkdownHash);

    public static async Task<JobViewSnapshot?> FindLatestAsync(
        IJobReadStore jobs, string sessionId, string taskId, string jobType, PlanArtifact plan, CancellationToken ct)
    {
        var hash = PlanArtifactSupport.ComputeMarkdownHash(plan.Markdown);
        var rows = await jobs.ReadSessionViewsAsync(sessionId, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.TaskId != taskId || row.JobType != jobType) continue;
            try
            {
                if (Matches(JObject.Parse(row.PayloadJson), plan, hash)) return row;
            }
            catch (JsonException)
            {
                // A legacy job with no readable binding does not describe this approved plan.
            }
        }

        return null;
    }

    private static bool Matches(JObject payload, PlanArtifact plan, string hash)
        => payload.Value<string>("planArtifactId") == plan.Id &&
           payload.Value<string>("planVersion") == plan.Version &&
           payload.Value<string>("planHash") == hash;
}
