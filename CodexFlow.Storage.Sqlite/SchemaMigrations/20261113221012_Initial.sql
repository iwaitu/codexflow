-- Generated from the historical EF Core 10.0.15 SQLite migration.
-- SqliteSchemaMigrator applies the transaction and migration history record.

CREATE TABLE "BackgroundJobs" (
    "JobId" TEXT NOT NULL CONSTRAINT "PK_BackgroundJobs" PRIMARY KEY,
    "SessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "JobType" TEXT NOT NULL,
    "Status" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "ResultPayloadJson" TEXT NULL,
    "ErrorMessage" TEXT NULL,
    "RunnerId" TEXT NULL,
    "ClaimToken" TEXT NULL,
    "LeaseExpiresAtUtc" TEXT NULL,
    "HeartbeatAtUtc" TEXT NULL,
    "DequeuedAtUtc" TEXT NULL,
    "Attempt" INTEGER NOT NULL,
    "LastEventSeq" INTEGER NOT NULL,
    "WaitingReason" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "StartedAtUtc" TEXT NULL,
    "FinishedAtUtc" TEXT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    "CancelledAtUtc" TEXT NULL,
    "Notified" INTEGER NOT NULL,
    "NotifiedAtUtc" TEXT NULL,
    "NotificationStatus" TEXT NULL,
    "NotificationAttemptCount" INTEGER NOT NULL,
    "NotificationLastAttemptAtUtc" TEXT NULL,
    "NotificationLastError" TEXT NULL,
    "NotificationLeaseOwner" TEXT NULL,
    "NotificationLeaseExpiresAtUtc" TEXT NULL,
    "NotificationSuppressedReason" TEXT NULL,
    "NotificationChannels" TEXT NULL,
    "SummaryMode" TEXT NULL,
    "SummaryMessageId" TEXT NULL,
    "SummaryConversationId" TEXT NULL,
    "SummaryTokenUsageInput" INTEGER NULL,
    "SummaryTokenUsageOutput" INTEGER NULL,
    "SummaryGeneratedAtUtc" TEXT NULL,
    "ShortSummary" TEXT NULL,
    "NotificationRead" INTEGER NOT NULL,
    "NotificationReadAtUtc" TEXT NULL
);

CREATE TABLE "OutboxEvents" (
    "EventId" TEXT NOT NULL CONSTRAINT "PK_OutboxEvents" PRIMARY KEY,
    "EventType" TEXT NOT NULL,
    "JobId" TEXT NULL,
    "SessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "Seq" INTEGER NOT NULL,
    "AggregateId" TEXT NULL,
    "Payload" TEXT NOT NULL,
    "CorrelationId" TEXT NULL,
    "DedupeKey" TEXT NULL,
    "OccurredAtUtc" TEXT NOT NULL,
    "ProcessedAt" TEXT NULL,
    "RetryCount" INTEGER NOT NULL,
    "LastError" TEXT NULL
);

CREATE TABLE "PlanArtifacts" (
    "Id" text NOT NULL CONSTRAINT "PK_PlanArtifacts" PRIMARY KEY,
    "SessionId" text NOT NULL,
    "Version" text NOT NULL,
    "Status" text NOT NULL,
    "Source" text NOT NULL,
    "Goal" text NOT NULL,
    "Markdown" text NOT NULL,
    "PreviewProjectedMarkdownHash" text NULL,
    "PlanFilePath" TEXT NULL,
    "TasksJson" text NOT NULL,
    "ReviewsJson" text NOT NULL,
    "ApprovalJson" text NULL,
    "ProjectionJson" text NULL,
    "ParentPlanId" TEXT NULL,
    "SupersededByPlanId" TEXT NULL,
    "IsCurrent" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "Sessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
    "UserId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "WorkspacePath" TEXT NOT NULL,
    "ProjectUrl" TEXT NULL,
    "CurrentStage" INTEGER NOT NULL,
    "FactsJson" TEXT NOT NULL,
    "GlobalPlanJson" TEXT NULL,
    "CurrentPlanArtifactId" TEXT NULL,
    "ActiveTaskId" TEXT NULL,
    "Created" TEXT NOT NULL,
    "HistorySummary" TEXT NULL,
    "LastCompressedAt" TEXT NULL
);

CREATE TABLE "JobCheckpoints" (
    "CheckpointId" TEXT NOT NULL CONSTRAINT "PK_JobCheckpoints" PRIMARY KEY,
    "JobId" TEXT NOT NULL,
    "SessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "SequenceNumber" INTEGER NOT NULL,
    "CurrentStep" TEXT NULL,
    "PendingActionType" TEXT NULL,
    "PendingPayloadJson" TEXT NULL,
    "ResumeToken" TEXT NULL,
    "WorkspacePath" TEXT NULL,
    "ActiveTaskId" TEXT NULL,
    "Attempt" INTEGER NOT NULL,
    "LastEventSeq" INTEGER NOT NULL,
    "CheckpointData" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_JobCheckpoints_BackgroundJobs_JobId" FOREIGN KEY ("JobId") REFERENCES "BackgroundJobs" ("JobId") ON DELETE CASCADE
);

CREATE TABLE "Conversations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Conversations" PRIMARY KEY,
    "SessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "ThreadType" INTEGER NOT NULL,
    "IsArchived" INTEGER NOT NULL,
    "Model" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Created" TEXT NOT NULL,
    "UpdateAt" TEXT NOT NULL,
    "IsIndexed" INTEGER NOT NULL,
    CONSTRAINT "FK_Conversations_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id")
);

CREATE TABLE "ChatMessages" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ChatMessages" PRIMARY KEY,
    "SystemPrompt" TEXT NULL,
    "ConversationId" TEXT NOT NULL,
    "UserContent" TEXT NULL,
    "AiContent" TEXT NULL,
    "ThinkingContent" TEXT NULL,
    "ReplyId" TEXT NULL,
    "Created" TEXT NOT NULL,
    "TokenUsed" INTEGER NOT NULL,
    "ToolCallResult" TEXT NULL,
    "MessageType" TEXT NULL,
    "NotificationMeta" TEXT NULL,
    "IvilsonConversationId" TEXT NULL,
    CONSTRAINT "FK_ChatMessages_Conversations_IvilsonConversationId" FOREIGN KEY ("IvilsonConversationId") REFERENCES "Conversations" ("Id")
);

CREATE INDEX "IX_BackgroundJobs_NotificationLeaseExpiresAtUtc" ON "BackgroundJobs" ("NotificationLeaseExpiresAtUtc");

CREATE INDEX "IX_BackgroundJobs_SessionId" ON "BackgroundJobs" ("SessionId");

CREATE INDEX "IX_BackgroundJobs_SessionId_NotificationStatus" ON "BackgroundJobs" ("SessionId", "NotificationStatus");

CREATE INDEX "IX_BackgroundJobs_Status" ON "BackgroundJobs" ("Status");

CREATE INDEX "IX_BackgroundJobs_Status_NotificationStatus_FinishedAtUtc" ON "BackgroundJobs" ("Status", "NotificationStatus", "FinishedAtUtc");

CREATE INDEX "IX_ChatMessages_IvilsonConversationId" ON "ChatMessages" ("IvilsonConversationId");

CREATE INDEX "IX_Conversations_SessionId" ON "Conversations" ("SessionId");

CREATE UNIQUE INDEX "IX_JobCheckpoints_JobId_SequenceNumber" ON "JobCheckpoints" ("JobId", "SequenceNumber");

CREATE INDEX "IX_OutboxEvents_EventType" ON "OutboxEvents" ("EventType");

CREATE INDEX "IX_OutboxEvents_Pending_Seq" ON "OutboxEvents" ("Seq") WHERE "ProcessedAt" IS NULL;

CREATE INDEX "IX_OutboxEvents_ProcessedAt" ON "OutboxEvents" ("ProcessedAt");

CREATE INDEX "IX_PlanArtifacts_SessionId" ON "PlanArtifacts" ("SessionId");

CREATE INDEX "IX_PlanArtifacts_SessionId_IsCurrent" ON "PlanArtifacts" ("SessionId", "IsCurrent");

CREATE INDEX "IX_PlanArtifacts_Status" ON "PlanArtifacts" ("Status");

CREATE TABLE "OutboxSeq" ("Id" INTEGER NOT NULL PRIMARY KEY CHECK ("Id" = 1), "Value" INTEGER NOT NULL);

INSERT INTO "OutboxSeq" ("Id", "Value") VALUES (1, 0);

CREATE TABLE "JobEventLog" (
    "EventId" TEXT NOT NULL PRIMARY KEY,
    "JobId" TEXT NULL,
    "SessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "Seq" INTEGER NOT NULL,
    "EventType" TEXT NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "OccurredAtUtc" TEXT NOT NULL,
    "LoggedAtUtc" TEXT NOT NULL
);

CREATE INDEX "IX_JobEventLog_JobId_Seq" ON "JobEventLog" ("JobId", "Seq");

CREATE INDEX "IX_JobEventLog_SessionId" ON "JobEventLog" ("SessionId");

CREATE TABLE "LocalKv" ("Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL, "ExpiresAtUtc" TEXT NOT NULL);

CREATE INDEX "IX_LocalKv_ExpiresAtUtc" ON "LocalKv" ("ExpiresAtUtc");
