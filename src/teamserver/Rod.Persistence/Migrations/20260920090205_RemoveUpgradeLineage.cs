using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUpgradeLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "origin_shell_session",
                table: "stager_tokens");

            migrationBuilder.DropColumn(
                name: "upgraded_implant_id",
                table: "shell_sessions");

            migrationBuilder.DropColumn(
                name: "origin_shell_session",
                table: "implants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "origin_shell_session",
                table: "stager_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "upgraded_implant_id",
                table: "shell_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_shell_session",
                table: "implants",
                type: "uuid",
                nullable: true);
        }
    }
}
