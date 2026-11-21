using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexFlow.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionArchiveAndExternalWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsExternalWorkspace",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IsExternalWorkspace",
                table: "Sessions");
        }
    }
}
