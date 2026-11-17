using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexFlow.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessPluginBuilderTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessCredentialMetas",
                columns: table => new
                {
                    CredentialRef = table.Column<string>(type: "text", nullable: false),
                    AuthKind = table.Column<string>(type: "text", nullable: false),
                    MaskedHint = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<string>(type: "text", nullable: false),
                    CipherFileName = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCredentialMetas", x => x.CredentialRef);
                });

            migrationBuilder.CreateTable(
                name: "BusinessPlugins",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SourceType = table.Column<string>(type: "text", nullable: false),
                    ConnectionId = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActiveSnapshotId = table.Column<string>(type: "text", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessPlugins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogCapabilities",
                columns: table => new
                {
                    SnapshotId = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    NativeIdentity = table.Column<string>(type: "text", nullable: false),
                    ModelAlias = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    SourceDescriptionMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    GroupTag = table.Column<string>(type: "text", nullable: false),
                    Risk = table.Column<string>(type: "text", nullable: false),
                    SuggestedSourceRiskHint = table.Column<string>(type: "text", nullable: true),
                    Executability = table.Column<string>(type: "text", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: false),
                    InputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    OutputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    InvocationJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogCapabilities", x => new { x.SnapshotId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "CatalogSnapshots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    ConnectionRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialRevision = table.Column<string>(type: "text", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    ProtocolVersion = table.Column<string>(type: "text", nullable: false),
                    SourceHash = table.Column<string>(type: "text", nullable: false),
                    SchemaHash = table.Column<string>(type: "text", nullable: false),
                    Complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalCapabilities = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsConfigurationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnsupportedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    EndpointPolicyJson = table.Column<string>(type: "text", nullable: false),
                    DocumentCredentialRef = table.Column<string>(type: "text", nullable: true),
                    ExecutionCredentialRef = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialRevision = table.Column<string>(type: "text", nullable: false),
                    IsEncryptedTransport = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginOperations",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: false),
                    ClientRequestId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotId = table.Column<string>(type: "text", nullable: true),
                    ResultMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginOperations", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "PublishedSkills",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    ActiveVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedSkills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishedSkillVersions",
                columns: table => new
                {
                    SkillId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    CatalogSnapshotId = table.Column<string>(type: "text", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    NeedsRevalidation = table.Column<bool>(type: "INTEGER", nullable: false),
                    WriteStepVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedSkillVersions", x => new { x.SkillId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "SkillDrafts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PluginId = table.Column<string>(type: "text", nullable: false),
                    CatalogSnapshotId = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BusinessGoal = table.Column<string>(type: "text", nullable: false),
                    SelectedCapabilityIdsJson = table.Column<string>(type: "text", nullable: false),
                    CandidateDefinitionJson = table.Column<string>(type: "text", nullable: true),
                    ClarificationQuestionsJson = table.Column<string>(type: "text", nullable: false),
                    ValidationErrorsJson = table.Column<string>(type: "text", nullable: false),
                    NeedsRevalidation = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SkillRuns",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "text", nullable: false),
                    SkillId = table.Column<string>(type: "text", nullable: false),
                    SkillVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientRequestId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StopRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalRequestCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadOnlyRetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    OutputPayloadJson = table.Column<string>(type: "text", nullable: true),
                    PendingApprovalJson = table.Column<string>(type: "text", nullable: true),
                    ErrorJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "SkillRunSteps",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StepId = table.Column<string>(type: "text", nullable: false),
                    StepType = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    RequestSummary = table.Column<string>(type: "text", nullable: true),
                    ResponseSummary = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillRunSteps", x => new { x.RunId, x.Sequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPlugins_ConnectionId",
                table: "BusinessPlugins",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPlugins_Enabled",
                table: "BusinessPlugins",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCapabilities_SnapshotId_Executability",
                table: "CatalogCapabilities",
                columns: new[] { "SnapshotId", "Executability" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCapabilities_SnapshotId_GroupTag",
                table: "CatalogCapabilities",
                columns: new[] { "SnapshotId", "GroupTag" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogSnapshots_PluginId_FetchedAtUtc",
                table: "CatalogSnapshots",
                columns: new[] { "PluginId", "FetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginConnections_PluginId",
                table: "PluginConnections",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginOperations_Kind_TargetId_ClientRequestId",
                table: "PluginOperations",
                columns: new[] { "Kind", "TargetId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishedSkills_PluginId",
                table: "PublishedSkills",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedSkillVersions_ContentHash",
                table: "PublishedSkillVersions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_SkillDrafts_PluginId",
                table: "SkillDrafts",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillRuns_SkillId_ClientRequestId",
                table: "SkillRuns",
                columns: new[] { "SkillId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkillRuns_Status",
                table: "SkillRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SkillRunSteps_RunId_StepId",
                table: "SkillRunSteps",
                columns: new[] { "RunId", "StepId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessCredentialMetas");

            migrationBuilder.DropTable(
                name: "BusinessPlugins");

            migrationBuilder.DropTable(
                name: "CatalogCapabilities");

            migrationBuilder.DropTable(
                name: "CatalogSnapshots");

            migrationBuilder.DropTable(
                name: "PluginConnections");

            migrationBuilder.DropTable(
                name: "PluginOperations");

            migrationBuilder.DropTable(
                name: "PublishedSkills");

            migrationBuilder.DropTable(
                name: "PublishedSkillVersions");

            migrationBuilder.DropTable(
                name: "SkillDrafts");

            migrationBuilder.DropTable(
                name: "SkillRuns");

            migrationBuilder.DropTable(
                name: "SkillRunSteps");
        }
    }
}
