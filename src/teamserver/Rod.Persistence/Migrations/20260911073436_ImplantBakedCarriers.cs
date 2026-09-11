using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImplantBakedCarriers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "carriers",
                table: "implants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "carriers",
                table: "implants");
        }
    }
}
