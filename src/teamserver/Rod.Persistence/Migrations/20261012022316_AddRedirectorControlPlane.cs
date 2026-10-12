using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRedirectorControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fronts_json",
                table: "listener_definitions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "redirector_credentials",
                columns: table => new
                {
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    secret_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    enrolled_node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redirector_credentials", x => x.credential_id);
                });

            migrationBuilder.CreateTable(
                name: "redirector_nodes",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enrolled_by = table.Column<Guid>(type: "uuid", nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reported_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reported_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    secret_digest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redirector_nodes", x => x.node_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_redirector_credentials_enrolled_node_id",
                table: "redirector_credentials",
                column: "enrolled_node_id");

            migrationBuilder.CreateIndex(
                name: "ix_redirector_credentials_issued_at",
                table: "redirector_credentials",
                column: "issued_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "redirector_credentials");

            migrationBuilder.DropTable(
                name: "redirector_nodes");

            migrationBuilder.DropColumn(
                name: "fronts_json",
                table: "listener_definitions");
        }
    }
}
