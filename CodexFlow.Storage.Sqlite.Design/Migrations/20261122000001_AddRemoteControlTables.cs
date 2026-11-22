using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexFlow.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteControlTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "RemoteAttachmentUploads" (
                    "PairingId" TEXT NOT NULL,
                    "SessionId" TEXT NOT NULL,
                    "UploadId" TEXT NOT NULL,
                    "FileSha256" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "RelativePath" TEXT NOT NULL,
                    "PairingIdHash8" TEXT NOT NULL,
                    "UploadedAttachmentDtoJson" TEXT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "UpdatedAtUtc" TEXT NOT NULL,
                    CONSTRAINT "PK_RemoteAttachmentUploads" PRIMARY KEY ("PairingId", "SessionId", "UploadId")
                );

                CREATE INDEX IF NOT EXISTS "IX_RemoteAttachmentUploads_Session_Path"
                    ON "RemoteAttachmentUploads" ("SessionId", "RelativePath", "Status");

                CREATE TABLE IF NOT EXISTS "RemoteChatOps" (
                    "PairingId" TEXT NOT NULL,
                    "SessionId" TEXT NOT NULL,
                    "ClientOpId" TEXT NOT NULL,
                    "RequestDigest" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "ConversationId" TEXT NULL,
                    "MessageId" TEXT NULL,
                    "BindingVersion" INTEGER NOT NULL,
                    "AccountRevocationSeq" INTEGER NOT NULL,
                    "DeviceRevocationSeq" INTEGER NOT NULL,
                    "ResponseDtoJson" TEXT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "UpdatedAtUtc" TEXT NOT NULL,
                    CONSTRAINT "PK_RemoteChatOps" PRIMARY KEY ("PairingId", "SessionId", "ClientOpId")
                );

                CREATE TABLE IF NOT EXISTS "RemoteWorkspaceAuthorizations" (
                    "WorkspaceRealPath" TEXT NOT NULL CONSTRAINT "PK_RemoteWorkspaceAuthorizations" PRIMARY KEY,
                    "SessionId" TEXT NULL,
                    "AuthorizedAtUtc" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "RemoteRevocationWatermarks" (
                    "ScopeKey" TEXT NOT NULL CONSTRAINT "PK_RemoteRevocationWatermarks" PRIMARY KEY,
                    "RevocationSeq" INTEGER NOT NULL,
                    "UpdatedAtUtc" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "RemoteRevokedPairings" (
                    "PairingId" TEXT NOT NULL CONSTRAINT "PK_RemoteRevokedPairings" PRIMARY KEY,
                    "RevokedAtUtc" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "RemoteJobOrigins" (
                    "JobId" TEXT NOT NULL CONSTRAINT "PK_RemoteJobOrigins" PRIMARY KEY,
                    "ControlChannel" TEXT NOT NULL,
                    "PairingId" TEXT NULL,
                    "BindingVersion" INTEGER NULL,
                    "AccountRevocationSeq" INTEGER NULL,
                    "DeviceRevocationSeq" INTEGER NULL,
                    "CreatedAtUtc" TEXT NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "RemoteJobOrigins";
                DROP TABLE IF EXISTS "RemoteRevokedPairings";
                DROP TABLE IF EXISTS "RemoteRevocationWatermarks";
                DROP TABLE IF EXISTS "RemoteWorkspaceAuthorizations";
                DROP TABLE IF EXISTS "RemoteChatOps";
                DROP TABLE IF EXISTS "RemoteAttachmentUploads";
                """);
        }
    }
}
