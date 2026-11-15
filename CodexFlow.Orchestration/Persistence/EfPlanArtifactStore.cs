// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Domain.Planning;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CodexFlow.Infrastructure.Persistence;

public sealed class EfPlanArtifactStore(CodexFlow.Orchestration.Jobs.IJobDbContext dbContext) : IPlanArtifactStore
{
    public async Task<PlanArtifact?> GetAsync(string planArtifactId, CancellationToken ct = default)
    {
        var entity = await dbContext.PlanArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(artifact => artifact.Id == planArtifactId, ct)
            .ConfigureAwait(false);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<PlanArtifact?> GetCurrentAsync(string sessionId, CancellationToken ct = default)
    {
        var entity = await dbContext.PlanArtifacts.AsNoTracking()
            .Where(artifact => artifact.SessionId == sessionId && artifact.IsCurrent)
            .OrderByDescending(artifact => artifact.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<IReadOnlyList<PlanArtifact>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        var entities = await dbContext.PlanArtifacts.AsNoTracking()
            .Where(artifact => artifact.SessionId == sessionId)
            .OrderByDescending(artifact => artifact.UpdatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(ToModel).ToList();
    }

    public async Task SaveAsync(PlanArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var existing = await dbContext.PlanArtifacts
            .FirstOrDefaultAsync(entity => entity.Id == artifact.Id, ct)
            .ConfigureAwait(false);
        if (existing == null)
        {
            dbContext.PlanArtifacts.Add(ToEntity(artifact));
        }
        else
        {
            CopyToEntity(artifact, existing);
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

    private static PlanArtifact ToModel(PlanArtifactEntity entity)
    {
        var artifact = new PlanArtifact
        {
            Id = entity.Id,
            SessionId = entity.SessionId,
            Version = entity.Version,
            Status = Enum.TryParse<PlanArtifactStatus>(entity.Status, out var status) ? status : PlanArtifactStatus.Draft,
            Source = Enum.TryParse<PlanArtifactSource>(entity.Source, out var source) ? source : PlanArtifactSource.DefaultPlanner,
            Goal = entity.Goal,
            Markdown = entity.Markdown,
            PreviewProjectedMarkdownHash = entity.PreviewProjectedMarkdownHash,
            PlanFilePath = entity.PlanFilePath,
            Approval = DeserializeOrNull<PlanApprovalRecord>(entity.ApprovalJson),
            Projection = DeserializeOrNull<PlanProjectionRecord>(entity.ProjectionJson),
            ParentPlanId = entity.ParentPlanId,
            SupersededByPlanId = entity.SupersededByPlanId,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };
        artifact.ReplaceTasks(DeserializeList<CodexTask>(entity.TasksJson));
        artifact.ReplaceReviews(DeserializeList<PlanReviewRecord>(entity.ReviewsJson));
        return artifact;
    }

    private static PlanArtifactEntity ToEntity(PlanArtifact artifact)
    {
        var entity = new PlanArtifactEntity();
        CopyToEntity(artifact, entity);
        return entity;
    }

    private static void CopyToEntity(PlanArtifact artifact, PlanArtifactEntity entity)
    {
        entity.Id = artifact.Id;
        entity.SessionId = artifact.SessionId;
        entity.Version = artifact.Version;
        entity.Status = artifact.Status.ToString();
        entity.Source = artifact.Source.ToString();
        entity.Goal = artifact.Goal;
        entity.Markdown = artifact.Markdown;
        entity.PreviewProjectedMarkdownHash = artifact.PreviewProjectedMarkdownHash;
        entity.PlanFilePath = artifact.PlanFilePath;
        entity.TasksJson = JsonConvert.SerializeObject(artifact.Tasks);
        entity.ReviewsJson = JsonConvert.SerializeObject(artifact.Reviews);
        entity.ApprovalJson = artifact.Approval == null ? null : JsonConvert.SerializeObject(artifact.Approval);
        entity.ProjectionJson = artifact.Projection == null ? null : JsonConvert.SerializeObject(artifact.Projection);
        entity.ParentPlanId = artifact.ParentPlanId;
        entity.SupersededByPlanId = artifact.SupersededByPlanId;
        entity.CreatedAtUtc = artifact.CreatedAtUtc;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonConvert.DeserializeObject<List<T>>(json) ?? [];
    }

    private static T? DeserializeOrNull<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonConvert.DeserializeObject<T>(json);
    }
}
