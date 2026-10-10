using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEngagementMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "engagement_members",
                columns: table => new
                {
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_members", x => new { x.engagement_id, x.operator_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_engagement_members_operator_id",
                table: "engagement_members",
                column: "operator_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engagement_members");
        }
    }
}
