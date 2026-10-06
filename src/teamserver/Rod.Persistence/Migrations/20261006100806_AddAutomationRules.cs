using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_rules",
                columns: table => new
                {
                    automation_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trigger_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval_seconds = table.Column<long>(type: "bigint", nullable: true),
                    event_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    only_implant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_verb = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verb = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    arguments = table.Column<string>(type: "text", nullable: false),
                    cooldown_seconds = table.Column<long>(type: "bigint", nullable: false),
                    max_firings = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fire_count = table.Column<int>(type: "integer", nullable: false),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_refusals = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_rules", x => x.automation_rule_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_enabled",
                table: "automation_rules",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_engagement_id",
                table: "automation_rules",
                column: "engagement_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_rules");
        }
    }
}
