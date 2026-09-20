using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImplantCadence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "jitter_seconds",
                table: "implants",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "sleep_seconds",
                table: "implants",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "jitter_seconds",
                table: "implants");

            migrationBuilder.DropColumn(
                name: "sleep_seconds",
                table: "implants");
        }
    }
}
