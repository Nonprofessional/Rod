using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShellSessionsWebShellProfilesTokenOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "origin_shell_session",
                table: "stager_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_shell_session",
                table: "implants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shell_sessions",
                columns: table => new
                {
                    shell_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    listener_id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Os = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_input_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_output_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upgraded_implant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shell_sessions", x => x.shell_session_id);
                });

            migrationBuilder.CreateTable(
                name: "webshell_profiles",
                columns: table => new
                {
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AdapterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Password = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Encoder = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Decoder = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    registered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_probe_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_probe_ok = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webshell_profiles", x => x.implant_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shell_sessions");

            migrationBuilder.DropTable(
                name: "webshell_profiles");

            migrationBuilder.DropColumn(
                name: "origin_shell_session",
                table: "stager_tokens");

            migrationBuilder.DropColumn(
                name: "origin_shell_session",
                table: "implants");
        }
    }
}
