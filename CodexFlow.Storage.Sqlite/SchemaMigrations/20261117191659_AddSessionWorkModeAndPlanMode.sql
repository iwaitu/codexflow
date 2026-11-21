-- Generated from the historical EF Core 10.0.15 SQLite migration.
-- SqliteSchemaMigrator applies the transaction and migration history record.

ALTER TABLE "Sessions" ADD "ModeVersion" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "Sessions" ADD "PlanModeActive" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Sessions" ADD "PlanModeChangedAtUtc" TEXT NULL;

ALTER TABLE "Sessions" ADD "PlanModeChangedBy" TEXT NULL;

ALTER TABLE "Sessions" ADD "PlanModeVersion" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "Sessions" ADD "WorkMode" INTEGER NOT NULL DEFAULT 0;
