-- Generated from the historical EF Core 10.0.15 SQLite migration.
-- SqliteSchemaMigrator applies the transaction and migration history record.

CREATE TABLE "BusinessCredentialMetas" (
    "CredentialRef" text NOT NULL CONSTRAINT "PK_BusinessCredentialMetas" PRIMARY KEY,
    "AuthKind" text NOT NULL,
    "MaskedHint" text NOT NULL,
    "Revision" text NOT NULL,
    "CipherFileName" text NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "BusinessPlugins" (
    "Id" text NOT NULL CONSTRAINT "PK_BusinessPlugins" PRIMARY KEY,
    "Name" text NOT NULL,
    "SourceType" text NOT NULL,
    "ConnectionId" text NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "ActiveSnapshotId" text NULL,
    "ArchivedAtUtc" TEXT NULL,
    "LastDiscoveredAtUtc" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "CatalogCapabilities" (
    "SnapshotId" text NOT NULL,
    "Id" text NOT NULL,
    "NativeIdentity" text NOT NULL,
    "ModelAlias" text NOT NULL,
    "Title" text NOT NULL,
    "Description" text NOT NULL,
    "SourceDescriptionMissing" INTEGER NOT NULL,
    "GroupTag" text NOT NULL,
    "Risk" text NOT NULL,
    "SuggestedSourceRiskHint" text NULL,
    "Executability" text NOT NULL,
    "DiagnosticsJson" text NOT NULL,
    "InputSchemaJson" text NULL,
    "OutputSchemaJson" text NULL,
    "InvocationJson" text NOT NULL,
    CONSTRAINT "PK_CatalogCapabilities" PRIMARY KEY ("SnapshotId", "Id")
);

CREATE TABLE "CatalogSnapshots" (
    "Id" text NOT NULL CONSTRAINT "PK_CatalogSnapshots" PRIMARY KEY,
    "PluginId" text NOT NULL,
    "ConnectionRevision" INTEGER NOT NULL,
    "CredentialRevision" text NOT NULL,
    "Environment" text NOT NULL,
    "ProtocolVersion" text NOT NULL,
    "SourceHash" text NOT NULL,
    "SchemaHash" text NOT NULL,
    "Complete" INTEGER NOT NULL,
    "TotalCapabilities" INTEGER NOT NULL,
    "ExecutableCount" INTEGER NOT NULL,
    "NeedsConfigurationCount" INTEGER NOT NULL,
    "UnsupportedCount" INTEGER NOT NULL,
    "DiagnosticsJson" text NOT NULL,
    "FetchedAtUtc" TEXT NOT NULL
);

CREATE TABLE "PluginConnections" (
    "Id" text NOT NULL CONSTRAINT "PK_PluginConnections" PRIMARY KEY,
    "PluginId" text NOT NULL,
    "SourceUrl" text NOT NULL,
    "Environment" text NOT NULL,
    "EndpointPolicyJson" text NOT NULL,
    "DocumentCredentialRef" text NULL,
    "ExecutionCredentialRef" text NULL,
    "Revision" INTEGER NOT NULL,
    "CredentialRevision" text NOT NULL,
    "IsEncryptedTransport" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "PluginOperations" (
    "OperationId" text NOT NULL CONSTRAINT "PK_PluginOperations" PRIMARY KEY,
    "Kind" text NOT NULL,
    "TargetId" text NOT NULL,
    "ClientRequestId" text NOT NULL,
    "Status" text NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "Version" INTEGER NOT NULL,
    "SnapshotId" text NULL,
    "ResultMessage" text NULL,
    "ErrorJson" text NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "PublishedSkills" (
    "Id" text NOT NULL CONSTRAINT "PK_PublishedSkills" PRIMARY KEY,
    "Name" text NOT NULL,
    "Description" text NOT NULL,
    "PluginId" text NOT NULL,
    "Environment" text NOT NULL,
    "ActiveVersion" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "PublishedSkillVersions" (
    "SkillId" text NOT NULL,
    "Version" INTEGER NOT NULL,
    "ContentHash" text NOT NULL,
    "CatalogSnapshotId" text NOT NULL,
    "DefinitionJson" text NOT NULL,
    "NeedsRevalidation" INTEGER NOT NULL,
    "WriteStepVerified" INTEGER NOT NULL,
    "PublishedAtUtc" TEXT NOT NULL,
    CONSTRAINT "PK_PublishedSkillVersions" PRIMARY KEY ("SkillId", "Version")
);

CREATE TABLE "SkillDrafts" (
    "Id" text NOT NULL CONSTRAINT "PK_SkillDrafts" PRIMARY KEY,
    "PluginId" text NOT NULL,
    "CatalogSnapshotId" text NOT NULL,
    "Revision" INTEGER NOT NULL,
    "Name" text NOT NULL,
    "BusinessGoal" text NOT NULL,
    "SelectedCapabilityIdsJson" text NOT NULL,
    "CandidateDefinitionJson" text NULL,
    "ClarificationQuestionsJson" text NOT NULL,
    "ValidationErrorsJson" text NOT NULL,
    "NeedsRevalidation" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "SkillRuns" (
    "RunId" text NOT NULL CONSTRAINT "PK_SkillRuns" PRIMARY KEY,
    "SkillId" text NOT NULL,
    "SkillVersion" INTEGER NOT NULL,
    "ClientRequestId" text NOT NULL,
    "Status" text NOT NULL,
    "StopRequested" INTEGER NOT NULL,
    "ExternalRequestCount" INTEGER NOT NULL,
    "ReadOnlyRetryCount" INTEGER NOT NULL,
    "InputsJson" text NOT NULL,
    "OutputPayloadJson" text NULL,
    "PendingApprovalJson" text NULL,
    "ErrorJson" text NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);

CREATE TABLE "SkillRunSteps" (
    "RunId" text NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "StepId" text NOT NULL,
    "StepType" text NOT NULL,
    "State" text NOT NULL,
    "RequestSummary" text NULL,
    "ResponseSummary" text NULL,
    "Evidence" text NULL,
    "ErrorCode" text NULL,
    "StartedAtUtc" TEXT NOT NULL,
    "CompletedAtUtc" TEXT NULL,
    CONSTRAINT "PK_SkillRunSteps" PRIMARY KEY ("RunId", "Sequence")
);

CREATE INDEX "IX_BusinessPlugins_ConnectionId" ON "BusinessPlugins" ("ConnectionId");

CREATE INDEX "IX_BusinessPlugins_Enabled" ON "BusinessPlugins" ("Enabled");

CREATE INDEX "IX_CatalogCapabilities_SnapshotId_Executability" ON "CatalogCapabilities" ("SnapshotId", "Executability");

CREATE INDEX "IX_CatalogCapabilities_SnapshotId_GroupTag" ON "CatalogCapabilities" ("SnapshotId", "GroupTag");

CREATE INDEX "IX_CatalogSnapshots_PluginId_FetchedAtUtc" ON "CatalogSnapshots" ("PluginId", "FetchedAtUtc");

CREATE INDEX "IX_PluginConnections_PluginId" ON "PluginConnections" ("PluginId");

CREATE UNIQUE INDEX "IX_PluginOperations_Kind_TargetId_ClientRequestId" ON "PluginOperations" ("Kind", "TargetId", "ClientRequestId");

CREATE INDEX "IX_PublishedSkills_PluginId" ON "PublishedSkills" ("PluginId");

CREATE INDEX "IX_PublishedSkillVersions_ContentHash" ON "PublishedSkillVersions" ("ContentHash");

CREATE INDEX "IX_SkillDrafts_PluginId" ON "SkillDrafts" ("PluginId");

CREATE UNIQUE INDEX "IX_SkillRuns_SkillId_ClientRequestId" ON "SkillRuns" ("SkillId", "ClientRequestId");

CREATE INDEX "IX_SkillRuns_Status" ON "SkillRuns" ("Status");

CREATE INDEX "IX_SkillRunSteps_RunId_StepId" ON "SkillRunSteps" ("RunId", "StepId");
