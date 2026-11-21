// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Planning;
using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Infrastructure.Persistence;

public sealed class EfPlanArtifactStore(IJobDbContext dbContext)
    : PlanArtifactStore(new EfPlanArtifactRepository(dbContext));

public sealed class EfPlanArtifactRepository(IJobDbContext dbContext) : IPlanArtifactRepository
{
    public Task<PlanArtifactEntity?> GetAsync(string planArtifactId, CancellationToken ct = default)
        => dbContext.PlanArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(artifact => artifact.Id == planArtifactId, ct);

    public Task<PlanArtifactEntity?> GetCurrentAsync(string sessionId, CancellationToken ct = default)
        => dbContext.PlanArtifacts.AsNoTracking()
            .Where(artifact => artifact.SessionId == sessionId && artifact.IsCurrent)
            .OrderByDescending(artifact => artifact.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PlanArtifactEntity>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
        => await dbContext.PlanArtifacts.AsNoTracking()
            .Where(artifact => artifact.SessionId == sessionId)
            .OrderByDescending(artifact => artifact.UpdatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task SaveAsync(PlanArtifactEntity artifact, CancellationToken ct = default)
    {
        var existing = await dbContext.PlanArtifacts
            .FirstOrDefaultAsync(entity => entity.Id == artifact.Id, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            artifact.IsCurrent = false;
            dbContext.PlanArtifacts.Add(artifact);
        }
        else
        {
            existing.SessionId = artifact.SessionId;
            existing.Version = artifact.Version;
            existing.Status = artifact.Status;
            existing.Source = artifact.Source;
            existing.Goal = artifact.Goal;
            existing.Markdown = artifact.Markdown;
            existing.PreviewProjectedMarkdownHash = artifact.PreviewProjectedMarkdownHash;
            existing.PlanFilePath = artifact.PlanFilePath;
            existing.TasksJson = artifact.TasksJson;
            existing.ReviewsJson = artifact.ReviewsJson;
            existing.ApprovalJson = artifact.ApprovalJson;
            existing.ProjectionJson = artifact.ProjectionJson;
            existing.ParentPlanId = artifact.ParentPlanId;
            existing.SupersededByPlanId = artifact.SupersededByPlanId;
            existing.CreatedAtUtc = artifact.CreatedAtUtc;
            existing.UpdatedAtUtc = artifact.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default)
    {
        var plans = await dbContext.PlanArtifacts
            .Where(artifact => artifact.SessionId == sessionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var plan in plans)
        {
            plan.IsCurrent = string.Equals(plan.Id, planArtifactId, StringComparison.Ordinal);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
