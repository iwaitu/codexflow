// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using CodexFlow.Contracts;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Services;
using CodexFlow.Core.Execution;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        var db = services.GetRequiredService<IJobDbContext>();
        var binding = string.IsNullOrWhiteSpace(job.PayloadJson) ? new JObject() : JObject.Parse(job.PayloadJson);
        var planId = binding["planArtifactId"]?.Value<string>() ?? string.Empty;
        var planVersion = binding["planVersion"]?.Value<string>() ?? string.Empty;
        var planHash = binding["planHash"]?.Value<string>() ?? string.Empty;
        var parents = await db.BackgroundJobs.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.JobId != job.JobId && x.Status == BackgroundJobStatus.Completed)
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.ResultPayloadJson).ToListAsync(ct).ConfigureAwait(false);
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
            RequiredTaskIds = binding["requiredTaskIds"]?.ToObject<string[]>() ?? [],
            ParentArtifactIds = parentIds,
            ChangedPaths = changes.Select(x => x.FilePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        return manifest;
    }

    internal static ForgeArtifactManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JObject.Parse(json)["artifactManifest"]?.ToObject<ForgeArtifactManifest>(); }
        catch (JsonException) { return null; }
    }
}
