using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    relay_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    relay_port = table.Column<int>(type: "integer", nullable: false),
                    relay_tls = table.Column<int>(type: "integer", nullable: false),
                    relay_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    relay_password = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    body_is_html = table.Column<bool>(type: "boolean", nullable: false),
                    build_request_json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    listener_id = table.Column<Guid>(type: "uuid", nullable: false),
                    launched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.campaign_id);
                });

            migrationBuilder.CreateTable(
                name: "campaign_recipients",
                columns: table => new
                {
                    campaign_recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    name = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    lure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    failure = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enroll_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clicked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrolled_implant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_recipients", x => x.campaign_recipient_id);
                    table.ForeignKey(
                        name: "FK_campaign_recipients_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "campaign_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_campaign_id",
                table: "campaign_recipients",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ux_campaign_recipients_enroll_token_id",
                table: "campaign_recipients",
                column: "enroll_token_id",
                unique: true,
                filter: "enroll_token_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_campaign_recipients_lure_id",
                table: "campaign_recipients",
                column: "lure_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_engagement_id",
                table: "campaigns",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_state",
                table: "campaigns",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_recipients");

            migrationBuilder.DropTable(
                name: "campaigns");
        }
    }
}
