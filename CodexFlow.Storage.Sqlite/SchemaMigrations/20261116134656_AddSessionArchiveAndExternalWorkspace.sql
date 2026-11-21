-- Generated from the historical EF Core 10.0.15 SQLite migration.
-- SqliteSchemaMigrator applies the transaction and migration history record.

ALTER TABLE "Sessions" ADD "IsArchived" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Sessions" ADD "IsExternalWorkspace" INTEGER NOT NULL DEFAULT 0;
