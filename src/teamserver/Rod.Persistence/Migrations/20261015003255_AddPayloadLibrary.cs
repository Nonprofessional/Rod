using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayloadLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payloads",
                columns: table => new
                {
                    payload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @class = table.Column<string>(name: "class", type: "character varying(64)", maxLength: 64, nullable: false),
                    language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    target = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    envelope_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    envelope_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    build_profile = table.Column<string>(type: "text", nullable: true),
                    delivers_payload_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payloads", x => x.payload_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payloads_engagement_id",
                table: "payloads",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_payloads_envelope_key_id",
                table: "payloads",
                column: "envelope_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_payloads_token_id",
                table: "payloads",
                column: "token_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payloads");
        }
    }
}
