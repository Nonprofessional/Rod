using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImplantTaskNonces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "implant_task_nonces",
                columns: table => new
                {
                    implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nonce_floor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_implant_task_nonces", x => x.implant_id);
                    table.ForeignKey(
                        name: "FK_implant_task_nonces_implants_implant_id",
                        column: x => x.implant_id,
                        principalTable: "implants",
                        principalColumn: "implant_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "implant_task_nonces");
        }
    }
}
