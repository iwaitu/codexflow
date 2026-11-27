// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Contracts;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Services;
using CodexFlow.Core.Execution;
using CodexFlow.Domain.Jobs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CodexFlow.Orchestration.Persistence;

namespace CodexFlow.Services.Background;

internal static class ForgeArtifactManifestFactory
{
    public static async Task<ForgeArtifactManifest?> CreateAsync(IServiceProvider services, BackgroundJob job,
        string userId, string sessionId, string mainWorkspace, string worktree, string baseline,
        string? head, TaskFileScopeDescriptor? scope, IGitService git, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(head) || head.Length is not (40 or 64) ||
            baseline.Length is not (40 or 64) || string.IsNullOrWhiteSpace(userId)) return null;
        var inspector = new GitArtifactInspector();
        var identity = await inspector.GetRepositoryIdentityAsync(mainWorkspace, ct).ConfigureAwait(false);
        var jobs = services.GetRequiredService<IJobReadStore>();
        var binding = string.IsNullOrWhiteSpace(job.PayloadJson) ? new JObject() : JObject.Parse(job.PayloadJson);
        var planId = binding["planArtifactId"]?.Value<string>() ?? string.Empty;
        var planVersion = binding["planVersion"]?.Value<string>() ?? string.Empty;
        var planHash = binding["planHash"]?.Value<string>() ?? string.Empty;
        var parents = await jobs.ReadCompletedResultPayloadsAsync(sessionId, job.JobId, ct).ConfigureAwait(false);
        var parentIds = parents.Select(ParseManifest).Where(x => x is not null && x.RepositoryIdentity == identity &&
                x.PlanArtifactId == planId && x.PlanVersion == planVersion && x.PlanHash == planHash)
            .Select(x => x!.ArtifactId).Distinct(StringComparer.Ordinal).ToArray();
        var changes = await git.GetChangedFilesAsync(worktree, baseline, head).ConfigureAwait(false);
        var manifest = new ForgeArtifactManifest
        {
            ArtifactId = $"{job.JobId}:{job.Attempt}:{head}", UserId = userId, SessionId = sessionId,
            JobId = job.JobId, TaskId = job.TaskId ?? job.JobId, Attempt = job.Attempt,
            RepositoryIdentity = identity, WorktreePath = Path.GetFullPath(worktree),
            BaseCommit = baseline, HeadCommit = head,
            ScopeHash = TrustedExecutionContextFactory.ComputeScopeHash(TrustedExecutionContextFactory.ToExportScope(scope)),
            PlanArtifactId = planId, PlanVersion = planVersion, PlanHash = planHash,
            RequiredTaskIds = ReadRequiredTaskIds(binding["requiredTaskIds"]),
            ParentArtifactIds = parentIds,
            ChangedPaths = changes.Select(x => x.FilePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        return manifest;
    }

    internal static bool HasUncommittedResult(JObject? result)
    {
        if (result?["changedFiles"] is not JArray { Count: > 0 } ||
            result["artifactManifest"] is not JObject manifest)
        {
            return false;
        }

        var head = manifest.GetValue("HeadCommit", StringComparison.OrdinalIgnoreCase)?.Value<string>();
        var baseline = manifest.GetValue("BaseCommit", StringComparison.OrdinalIgnoreCase)?.Value<string>();
        return !string.IsNullOrEmpty(head) && string.Equals(head, baseline, StringComparison.OrdinalIgnoreCase);
    }

    internal static ForgeArtifactManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var manifest = JObject.Parse(json)["artifactManifest"];
            return manifest is null ? null : System.Text.Json.JsonSerializer.Deserialize(
                manifest.ToString(Formatting.None), OrchestrationSnapshotJsonContext.LegacyManifest.ForgeArtifactManifest);
        }
        catch (Exception ex) when (ex is JsonException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string[] ReadRequiredTaskIds(JToken? token)
        => token switch
        {
            null or { Type: JTokenType.Null } => [],
            JArray items => items.Select(static item => item.Value<string>()!).ToArray(),
            _ => throw new JsonSerializationException("requiredTaskIds must be an array.")
        };
}
