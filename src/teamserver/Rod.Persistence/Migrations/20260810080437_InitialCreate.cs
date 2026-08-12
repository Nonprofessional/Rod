using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.artifact_id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verb = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    output = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    append_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "engagements",
                columns: table => new
                {
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagements", x => x.engagement_id);
                });

            migrationBuilder.CreateTable(
                name: "implants",
                columns: table => new
                {
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    kill_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @class = table.Column<int>(name: "class", type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deployed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_implant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_implants", x => x.implant_id);
                });

            migrationBuilder.CreateTable(
                name: "operators",
                columns: table => new
                {
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operators", x => x.operator_id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "stager_tokens",
                columns: table => new
                {
                    stager_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    remaining_uses = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stager_tokens", x => x.stager_token_id);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    verb = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    arguments = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    output = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.task_id);
                });

            migrationBuilder.CreateTable(
                name: "engagement_members",
                columns: table => new
                {
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_members", x => new { x.engagement_id, x.operator_id });
                    table.ForeignKey(
                        name: "FK_engagement_members_engagements_engagement_id",
                        column: x => x.engagement_id,
                        principalTable: "engagements",
                        principalColumn: "engagement_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_engagement_id",
                table: "artifacts",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_task_id",
                table: "artifacts",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_engagement_id",
                table: "audit_events",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_implants_engagement_id",
                table: "implants",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_engagement_id",
                table: "sessions",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_implant_id",
                table: "sessions",
                column: "implant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_engagement_id",
                table: "tasks",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_implant_id",
                table: "tasks",
                column: "implant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "engagement_members");

            migrationBuilder.DropTable(
                name: "implants");

            migrationBuilder.DropTable(
                name: "operators");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "stager_tokens");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "engagements");
        }
    }
}
