using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexFlow.Storage.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionWorkModeAndPlanMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ModeVersion",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "PlanModeActive",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanModeChangedAtUtc",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanModeChangedBy",
                table: "Sessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanModeVersion",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "WorkMode",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModeVersion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PlanModeActive",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PlanModeChangedAtUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PlanModeChangedBy",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PlanModeVersion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "WorkMode",
                table: "Sessions");
        }
    }
}
