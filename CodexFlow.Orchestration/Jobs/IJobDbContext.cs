// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;
using CodexFlow.Domain.Jobs;
using CodexFlow.Domain.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodexFlow.Orchestration.Jobs;

/// <summary>
/// What the job runner, the lease service and the projector need from a database: the three job tables and a unit of work over
/// them. <see cref="JobDbContext"/> is the implementation a host uses when the job tables are all it stores (the local host, on
/// SQLite). A host whose own context already maps these tables (the platform, on PostgreSQL) hands out that context instead, so
/// a request keeps working on one context and one transaction.
/// </summary>
public interface IJobDbContext
{
    DbSet<BackgroundJob> BackgroundJobs { get; }
    DbSet<JobCheckpoint> JobCheckpoints { get; }
    DbSet<OutboxEvent> OutboxEvents { get; }
    DbSet<PlanArtifactEntity> PlanArtifacts { get; }
    DbSet<IvilsonSession> Sessions { get; }
    DbSet<IvilsonConversation> Conversations { get; }
    DbSet<IvilsonChatMessage> ChatMessages { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    EntityEntry<TEntity> Update<TEntity>(TEntity entity) where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The session facts a job is checked against before it runs.</summary>
public sealed record SessionRecord(string Id, string? UserId, string? WorkspacePath);

/// <summary>
/// Looks sessions up for the job runner. Sessions belong to the host (the platform keeps them next to its users; the local host
/// has a single user), so the runner asks through this interface and never reads a session table itself.
/// </summary>
public interface ISessionDirectory
{
    /// <summary>The session with this id, or null when there is none.</summary>
    Task<SessionRecord?> FindAsync(string sessionId, CancellationToken ct);
}
