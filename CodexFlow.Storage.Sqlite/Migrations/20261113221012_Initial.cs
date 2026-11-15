// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexFlow.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    JobType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RunnerId = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DequeuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEventSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    WaitingReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notified = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotificationStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NotificationAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NotificationLastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotificationLastError = table.Column<string>(type: "TEXT", nullable: true),
                    NotificationLeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    NotificationLeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotificationSuppressedReason = table.Column<string>(type: "TEXT", nullable: true),
                    NotificationChannels = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryMode = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryTokenUsageInput = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryTokenUsageOutput = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryGeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShortSummary = table.Column<string>(type: "TEXT", nullable: true),
                    NotificationRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotificationReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobs", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    AggregateId = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "PlanArtifacts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Goal = table.Column<string>(type: "text", nullable: false),
                    Markdown = table.Column<string>(type: "text", nullable: false),
                    PreviewProjectedMarkdownHash = table.Column<string>(type: "text", nullable: true),
                    PlanFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    TasksJson = table.Column<string>(type: "text", nullable: false),
                    ReviewsJson = table.Column<string>(type: "text", nullable: false),
                    ApprovalJson = table.Column<string>(type: "text", nullable: true),
                    ProjectionJson = table.Column<string>(type: "text", nullable: true),
                    ParentPlanId = table.Column<string>(type: "TEXT", nullable: true),
                    SupersededByPlanId = table.Column<string>(type: "TEXT", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentStage = table.Column<int>(type: "INTEGER", nullable: false),
                    FactsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GlobalPlanJson = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentPlanArtifactId = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveTaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HistorySummary = table.Column<string>(type: "TEXT", nullable: true),
                    LastCompressedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobCheckpoints",
                columns: table => new
                {
                    CheckpointId = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    SequenceNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStep = table.Column<string>(type: "TEXT", nullable: true),
                    PendingActionType = table.Column<string>(type: "TEXT", nullable: true),
                    PendingPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeToken = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveTaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEventSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckpointData = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobCheckpoints", x => x.CheckpointId);
                    table.ForeignKey(
                        name: "FK_JobCheckpoints_BackgroundJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "BackgroundJobs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadType = table.Column<int>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsIndexed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SystemPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    UserContent = table.Column<string>(type: "TEXT", nullable: true),
                    AiContent = table.Column<string>(type: "TEXT", nullable: true),
                    ThinkingContent = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyId = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TokenUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallResult = table.Column<string>(type: "TEXT", nullable: true),
                    MessageType = table.Column<string>(type: "TEXT", nullable: true),
                    NotificationMeta = table.Column<string>(type: "TEXT", nullable: true),
                    IvilsonConversationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Conversations_IvilsonConversationId",
                        column: x => x.IvilsonConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_NotificationLeaseExpiresAtUtc",
                table: "BackgroundJobs",
                column: "NotificationLeaseExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_SessionId",
                table: "BackgroundJobs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_SessionId_NotificationStatus",
                table: "BackgroundJobs",
                columns: new[] { "SessionId", "NotificationStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Status",
                table: "BackgroundJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Status_NotificationStatus_FinishedAtUtc",
                table: "BackgroundJobs",
                columns: new[] { "Status", "NotificationStatus", "FinishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_IvilsonConversationId",
                table: "ChatMessages",
                column: "IvilsonConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SessionId",
                table: "Conversations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_JobCheckpoints_JobId_SequenceNumber",
                table: "JobCheckpoints",
                columns: new[] { "JobId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_EventType",
                table: "OutboxEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_Pending_Seq",
                table: "OutboxEvents",
                column: "Seq",
                filter: "\"ProcessedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_ProcessedAt",
                table: "OutboxEvents",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlanArtifacts_SessionId",
                table: "PlanArtifacts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanArtifacts_SessionId_IsCurrent",
                table: "PlanArtifacts",
                columns: new[] { "SessionId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanArtifacts_Status",
                table: "PlanArtifacts",
                column: "Status");

            // The three tables below are not part of the EF model; the stores that own them use plain SQL.

            // Counter behind OutboxEvent.Seq (see OutboxSeqInterceptor).
            migrationBuilder.Sql("CREATE TABLE \"OutboxSeq\" (\"Id\" INTEGER NOT NULL PRIMARY KEY CHECK (\"Id\" = 1), \"Value\" INTEGER NOT NULL);");
            migrationBuilder.Sql("INSERT INTO \"OutboxSeq\" (\"Id\", \"Value\") VALUES (1, 0);");

            // Job event history (see SqliteJobEventLog).
            migrationBuilder.Sql(@"CREATE TABLE ""JobEventLog"" (
    ""EventId"" TEXT NOT NULL PRIMARY KEY,
    ""JobId"" TEXT NULL,
    ""SessionId"" TEXT NULL,
    ""TaskId"" TEXT NULL,
    ""Seq"" INTEGER NOT NULL,
    ""EventType"" TEXT NOT NULL,
    ""PayloadJson"" TEXT NOT NULL,
    ""OccurredAtUtc"" TEXT NOT NULL,
    ""LoggedAtUtc"" TEXT NOT NULL
);");
            migrationBuilder.Sql("CREATE INDEX \"IX_JobEventLog_JobId_Seq\" ON \"JobEventLog\" (\"JobId\", \"Seq\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_JobEventLog_SessionId\" ON \"JobEventLog\" (\"SessionId\");");

            // Expiring text store behind SqliteKeyValueTextStore (sessions, task-list snapshots).
            migrationBuilder.Sql("CREATE TABLE \"LocalKv\" (\"Key\" TEXT NOT NULL PRIMARY KEY, \"Value\" TEXT NOT NULL, \"ExpiresAtUtc\" TEXT NOT NULL);");
            migrationBuilder.Sql("CREATE INDEX \"IX_LocalKv_ExpiresAtUtc\" ON \"LocalKv\" (\"ExpiresAtUtc\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE \"LocalKv\";");
            migrationBuilder.Sql("DROP TABLE \"JobEventLog\";");
            migrationBuilder.Sql("DROP TABLE \"OutboxSeq\";");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "JobCheckpoints");

            migrationBuilder.DropTable(
                name: "OutboxEvents");

            migrationBuilder.DropTable(
                name: "PlanArtifacts");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "BackgroundJobs");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
