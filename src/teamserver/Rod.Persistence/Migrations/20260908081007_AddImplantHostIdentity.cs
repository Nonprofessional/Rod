using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImplantHostIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "arch",
                table: "implants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hostname",
                table: "implants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "os",
                table: "implants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "implants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "arch",
                table: "implants");

            migrationBuilder.DropColumn(
                name: "hostname",
                table: "implants");

            migrationBuilder.DropColumn(
                name: "os",
                table: "implants");

            migrationBuilder.DropColumn(
                name: "username",
                table: "implants");
        }
    }
}
