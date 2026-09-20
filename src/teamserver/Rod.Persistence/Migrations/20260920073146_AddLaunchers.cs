using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLaunchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "launchers",
                columns: table => new
                {
                    launcher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    listener_id = table.Column<Guid>(type: "uuid", nullable: false),
                    front_name = table.Column<string>(type: "text", nullable: false),
                    front_endpoint = table.Column<string>(type: "text", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_secret = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launchers", x => x.launcher_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_launchers_engagement_id",
                table: "launchers",
                column: "engagement_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "launchers");
        }
    }
}
