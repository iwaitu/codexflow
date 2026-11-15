// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;
using CodexFlow.Domain.Jobs;
using CodexFlow.Domain.Planning;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

/// <summary>
/// The tables the job runner lives on: jobs, their checkpoints, the outbox and the plan artifacts. It is all the runner, the gateway and the
/// projector know about a database, so a host decides where the tables are: the local host points this context at a SQLite file,
/// the platform host at the PostgreSQL database its own context (users, sessions, ...) also maps. The mapping is
/// <see cref="JobModel"/>, shared with that platform context, so both describe the same tables.
/// </summary>
public class JobDbContext : DbContext, IJobDbContext
{
    public JobDbContext(DbContextOptions<JobDbContext> options) : base(options)
    {
    }

    public DbSet<BackgroundJob> BackgroundJobs { get; set; }
    public DbSet<JobCheckpoint> JobCheckpoints { get; set; }
    public DbSet<OutboxEvent> OutboxEvents { get; set; }
    public DbSet<PlanArtifactEntity> PlanArtifacts { get; set; }
    public DbSet<IvilsonSession> Sessions { get; set; }
    public DbSet<IvilsonConversation> Conversations { get; set; }
    public DbSet<IvilsonChatMessage> ChatMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        JobModel.Apply(modelBuilder, Database.ProviderName);
    }
}

/// <summary>The mapping of the job tables, applied by every context that maps them.</summary>
public static class JobModel
{
    /// <summary>Provider name of Microsoft.EntityFrameworkCore.Sqlite (this project does not reference that package).</summary>
    public const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    public static void Apply(ModelBuilder builder, string? providerName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var isSqlite = providerName == SqliteProviderName;

        builder.Entity<BackgroundJob>(entity =>
        {
            entity.HasKey(e => e.JobId);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Status);

            // 通知状态机索引
            entity.HasIndex(e => new { e.Status, e.NotificationStatus, e.FinishedAtUtc })
                  .HasDatabaseName("IX_BackgroundJobs_Status_NotificationStatus_FinishedAtUtc");
            entity.HasIndex(e => e.NotificationLeaseExpiresAtUtc)
                  .HasDatabaseName("IX_BackgroundJobs_NotificationLeaseExpiresAtUtc");
            entity.HasIndex(e => new { e.SessionId, e.NotificationStatus })
                  .HasDatabaseName("IX_BackgroundJobs_SessionId_NotificationStatus");

            entity.Property(e => e.ClaimToken)
                  .IsConcurrencyToken();

            // NotificationStatus 存储为字符串
            entity.Property(e => e.NotificationStatus)
                  .HasConversion<string?>()
                  .HasMaxLength(32);
        });

        builder.Entity<JobCheckpoint>(entity =>
        {
            entity.HasKey(e => e.CheckpointId);
            entity.HasIndex(e => new { e.JobId, e.SequenceNumber }).IsUnique();
        });

        builder.Entity<OutboxEvent>(entity =>
        {
            entity.HasKey(e => e.EventId);
            // PostgreSQL numbers events itself. SQLite cannot auto-number a non-key column, so the SQLite
            // provider assigns Seq (monotonic across contexts) just before saving.
            if (isSqlite)
            {
                entity.Property(e => e.Seq).ValueGeneratedNever();
            }
            else
            {
                entity.Property(e => e.Seq).ValueGeneratedOnAdd();
            }
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.ProcessedAt);
            entity.HasIndex(e => e.Seq);
            // Partial index for pending events
            entity.HasIndex(e => e.Seq)
                  .HasFilter("\"ProcessedAt\" IS NULL")
                  .HasDatabaseName("IX_OutboxEvents_Pending_Seq");
        });

        var jsonColumnType = isSqlite ? "text" : "jsonb";
        builder.Entity<PlanArtifactEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => new { e.SessionId, e.IsCurrent });
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.SessionId).IsRequired().HasColumnType("text");
            entity.Property(e => e.Version).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).IsRequired().HasColumnType("text");
            entity.Property(e => e.Source).IsRequired().HasColumnType("text");
            entity.Property(e => e.Goal).IsRequired().HasColumnType("text");
            entity.Property(e => e.Markdown).IsRequired().HasColumnType("text");
            entity.Property(e => e.PreviewProjectedMarkdownHash).HasColumnType("text");
            entity.Property(e => e.TasksJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.ReviewsJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.ApprovalJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.ProjectionJson).HasColumnType(jsonColumnType);
        });
    }
}
