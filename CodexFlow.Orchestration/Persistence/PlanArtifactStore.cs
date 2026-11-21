// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Domain.Planning;

namespace CodexFlow.Infrastructure.Persistence;

/// <summary>Stores plan rows using the historical planning JSON representation.</summary>
public class PlanArtifactStore(IPlanArtifactRepository repository) : IPlanArtifactStore
{
    public async Task<PlanArtifact?> GetAsync(string planArtifactId, CancellationToken ct = default)
    {
        var entity = await repository.GetAsync(planArtifactId, ct).ConfigureAwait(false);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<PlanArtifact?> GetCurrentAsync(string sessionId, CancellationToken ct = default)
    {
        var entity = await repository.GetCurrentAsync(sessionId, ct).ConfigureAwait(false);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<IReadOnlyList<PlanArtifact>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        var entities = await repository.ListBySessionAsync(sessionId, ct).ConfigureAwait(false);
        return entities.Select(ToModel).ToList();
    }

    public Task SaveAsync(PlanArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return repository.SaveAsync(ToEntity(artifact), ct);
    }

    public Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default)
        => repository.SetCurrentAsync(sessionId, planArtifactId, ct);

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

    private static PlanArtifactEntity ToEntity(PlanArtifact artifact) => new()
    {
        Id = artifact.Id,
        SessionId = artifact.SessionId,
        Version = artifact.Version,
        Status = artifact.Status.ToString(),
        Source = artifact.Source.ToString(),
        Goal = artifact.Goal,
        Markdown = artifact.Markdown,
        PreviewProjectedMarkdownHash = artifact.PreviewProjectedMarkdownHash,
        PlanFilePath = artifact.PlanFilePath,
        TasksJson = PlanToolJson.Write(artifact.Tasks),
        ReviewsJson = PlanToolJson.Write(artifact.Reviews),
        ApprovalJson = artifact.Approval == null ? null : PlanToolJson.Write(artifact.Approval),
        ProjectionJson = artifact.Projection == null ? null : PlanToolJson.Write(artifact.Projection),
        ParentPlanId = artifact.ParentPlanId,
        SupersededByPlanId = artifact.SupersededByPlanId,
        CreatedAtUtc = artifact.CreatedAtUtc,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return PlanToolJson.Read<List<T>>(json) ?? [];
    }

    private static T? DeserializeOrNull<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return PlanToolJson.Read<T>(json);
    }
}
