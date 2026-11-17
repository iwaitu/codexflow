// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.BusinessSkills;
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
    public DbSet<BusinessPluginEntity> BusinessPlugins { get; set; }
    public DbSet<PluginConnectionEntity> PluginConnections { get; set; }
    public DbSet<BusinessCredentialMetaEntity> BusinessCredentialMetas { get; set; }
    public DbSet<CatalogSnapshotEntity> CatalogSnapshots { get; set; }
    public DbSet<CatalogCapabilityEntity> CatalogCapabilities { get; set; }
    public DbSet<PluginOperationEntity> PluginOperations { get; set; }
    public DbSet<SkillDraftEntity> SkillDrafts { get; set; }
    public DbSet<PublishedSkillEntity> PublishedSkills { get; set; }
    public DbSet<PublishedSkillVersionEntity> PublishedSkillVersions { get; set; }
    public DbSet<SkillRunEntity> SkillRuns { get; set; }
    public DbSet<SkillRunStepEntity> SkillRunSteps { get; set; }

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

        builder.Entity<BusinessPluginEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConnectionId);
            entity.HasIndex(e => e.Enabled);
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.Name).IsRequired().HasColumnType("text");
            entity.Property(e => e.SourceType).IsRequired().HasColumnType("text");
            entity.Property(e => e.ConnectionId).IsRequired().HasColumnType("text");
            entity.Property(e => e.ActiveSnapshotId).HasColumnType("text");
        });

        builder.Entity<PluginConnectionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PluginId);
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.PluginId).IsRequired().HasColumnType("text");
            entity.Property(e => e.SourceUrl).IsRequired().HasColumnType("text");
            entity.Property(e => e.Environment).IsRequired().HasColumnType("text");
            entity.Property(e => e.EndpointPolicyJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.DocumentCredentialRef).HasColumnType("text");
            entity.Property(e => e.ExecutionCredentialRef).HasColumnType("text");
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.Property(e => e.CredentialRevision).IsRequired().HasColumnType("text");
        });

        builder.Entity<BusinessCredentialMetaEntity>(entity =>
        {
            entity.HasKey(e => e.CredentialRef);
            entity.Property(e => e.CredentialRef).HasColumnType("text");
            entity.Property(e => e.AuthKind).IsRequired().HasColumnType("text");
            entity.Property(e => e.MaskedHint).IsRequired().HasColumnType("text");
            entity.Property(e => e.Revision).IsRequired().HasColumnType("text");
            entity.Property(e => e.CipherFileName).IsRequired().HasColumnType("text");
        });

        builder.Entity<CatalogSnapshotEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PluginId, e.FetchedAtUtc });
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.PluginId).IsRequired().HasColumnType("text");
            entity.Property(e => e.CredentialRevision).IsRequired().HasColumnType("text");
            entity.Property(e => e.Environment).IsRequired().HasColumnType("text");
            entity.Property(e => e.ProtocolVersion).IsRequired().HasColumnType("text");
            entity.Property(e => e.SourceHash).IsRequired().HasColumnType("text");
            entity.Property(e => e.SchemaHash).IsRequired().HasColumnType("text");
            entity.Property(e => e.DiagnosticsJson).IsRequired().HasColumnType(jsonColumnType);
        });

        builder.Entity<CatalogCapabilityEntity>(entity =>
        {
            entity.HasKey(e => new { e.SnapshotId, e.Id });
            entity.HasIndex(e => new { e.SnapshotId, e.GroupTag });
            entity.HasIndex(e => new { e.SnapshotId, e.Executability });
            entity.Property(e => e.SnapshotId).HasColumnType("text");
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.NativeIdentity).IsRequired().HasColumnType("text");
            entity.Property(e => e.ModelAlias).IsRequired().HasColumnType("text");
            entity.Property(e => e.Title).IsRequired().HasColumnType("text");
            entity.Property(e => e.Description).IsRequired().HasColumnType("text");
            entity.Property(e => e.GroupTag).IsRequired().HasColumnType("text");
            entity.Property(e => e.Risk).IsRequired().HasColumnType("text");
            entity.Property(e => e.SuggestedSourceRiskHint).HasColumnType("text");
            entity.Property(e => e.Executability).IsRequired().HasColumnType("text");
            entity.Property(e => e.DiagnosticsJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.InputSchemaJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.OutputSchemaJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.InvocationJson).IsRequired().HasColumnType(jsonColumnType);
        });

        builder.Entity<PluginOperationEntity>(entity =>
        {
            entity.HasKey(e => e.OperationId);
            entity.HasIndex(e => new { e.Kind, e.TargetId, e.ClientRequestId }).IsUnique();
            entity.Property(e => e.OperationId).HasColumnType("text");
            entity.Property(e => e.Kind).IsRequired().HasColumnType("text");
            entity.Property(e => e.TargetId).IsRequired().HasColumnType("text");
            entity.Property(e => e.ClientRequestId).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).IsRequired().HasColumnType("text");
            entity.Property(e => e.SnapshotId).HasColumnType("text");
            entity.Property(e => e.ResultMessage).HasColumnType("text");
            entity.Property(e => e.ErrorJson).HasColumnType(jsonColumnType);
        });

        builder.Entity<SkillDraftEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PluginId);
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.PluginId).IsRequired().HasColumnType("text");
            entity.Property(e => e.CatalogSnapshotId).IsRequired().HasColumnType("text");
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.Property(e => e.Name).IsRequired().HasColumnType("text");
            entity.Property(e => e.BusinessGoal).IsRequired().HasColumnType("text");
            entity.Property(e => e.SelectedCapabilityIdsJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.CandidateDefinitionJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.ClarificationQuestionsJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.ValidationErrorsJson).IsRequired().HasColumnType(jsonColumnType);
        });

        builder.Entity<PublishedSkillEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PluginId);
            entity.Property(e => e.Id).HasColumnType("text");
            entity.Property(e => e.Name).IsRequired().HasColumnType("text");
            entity.Property(e => e.Description).IsRequired().HasColumnType("text");
            entity.Property(e => e.PluginId).IsRequired().HasColumnType("text");
            entity.Property(e => e.Environment).IsRequired().HasColumnType("text");
        });

        builder.Entity<PublishedSkillVersionEntity>(entity =>
        {
            entity.HasKey(e => new { e.SkillId, e.Version });
            entity.HasIndex(e => e.ContentHash);
            entity.Property(e => e.SkillId).HasColumnType("text");
            entity.Property(e => e.ContentHash).IsRequired().HasColumnType("text");
            entity.Property(e => e.CatalogSnapshotId).IsRequired().HasColumnType("text");
            entity.Property(e => e.DefinitionJson).IsRequired().HasColumnType(jsonColumnType);
        });

        builder.Entity<SkillRunEntity>(entity =>
        {
            entity.HasKey(e => e.RunId);
            entity.HasIndex(e => new { e.SkillId, e.ClientRequestId }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.RunId).HasColumnType("text");
            entity.Property(e => e.SkillId).IsRequired().HasColumnType("text");
            entity.Property(e => e.ClientRequestId).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).IsRequired().HasColumnType("text");
            entity.Property(e => e.InputsJson).IsRequired().HasColumnType(jsonColumnType);
            entity.Property(e => e.OutputPayloadJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.PendingApprovalJson).HasColumnType(jsonColumnType);
            entity.Property(e => e.ErrorJson).HasColumnType(jsonColumnType);
        });

        builder.Entity<SkillRunStepEntity>(entity =>
        {
            entity.HasKey(e => new { e.RunId, e.Sequence });
            entity.HasIndex(e => new { e.RunId, e.StepId });
            entity.Property(e => e.RunId).HasColumnType("text");
            entity.Property(e => e.StepId).IsRequired().HasColumnType("text");
            entity.Property(e => e.StepType).IsRequired().HasColumnType("text");
            entity.Property(e => e.State).IsRequired().HasColumnType("text");
            entity.Property(e => e.RequestSummary).HasColumnType("text");
            entity.Property(e => e.ResponseSummary).HasColumnType("text");
            entity.Property(e => e.Evidence).HasColumnType("text");
            entity.Property(e => e.ErrorCode).HasColumnType("text");
        });
    }
}
