using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EngagementListenerDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "listener_definitions",
                columns: table => new
                {
                    listener_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    transport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bind_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    public_endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    repointed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listener_definitions", x => x.listener_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_listener_definitions_engagement_id",
                table: "listener_definitions",
                column: "engagement_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listener_definitions");
        }
    }
}
