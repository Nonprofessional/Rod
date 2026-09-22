using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameDeployTokensAndSoftRevoke : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_stager_tokens",
                table: "stager_tokens");

            migrationBuilder.RenameTable(
                name: "stager_tokens",
                newName: "deploy_tokens");

            migrationBuilder.RenameColumn(
                name: "stager_token_id",
                table: "deploy_tokens",
                newName: "deploy_token_id");

            migrationBuilder.RenameIndex(
                name: "ux_stager_tokens_secret_hash",
                table: "deploy_tokens",
                newName: "ux_deploy_tokens_secret_hash");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "deploy_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_deploy_tokens",
                table: "deploy_tokens",
                column: "deploy_token_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_deploy_tokens",
                table: "deploy_tokens");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "deploy_tokens");

            migrationBuilder.RenameTable(
                name: "deploy_tokens",
                newName: "stager_tokens");

            migrationBuilder.RenameColumn(
                name: "deploy_token_id",
                table: "stager_tokens",
                newName: "stager_token_id");

            migrationBuilder.RenameIndex(
                name: "ux_deploy_tokens_secret_hash",
                table: "stager_tokens",
                newName: "ux_stager_tokens_secret_hash");

            migrationBuilder.AddPrimaryKey(
                name: "PK_stager_tokens",
                table: "stager_tokens",
                column: "stager_token_id");
        }
    }
}
