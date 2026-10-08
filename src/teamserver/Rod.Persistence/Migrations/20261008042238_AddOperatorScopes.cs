using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "scopes",
                table: "operators",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            // The synthetic automation operator never authenticates; a row
            // seeded before scopes existed would otherwise count as a task
            // holder in the assignment guards (architecture.md Sec 4.5).
            migrationBuilder.Sql(
                "UPDATE operators SET scopes = 0 WHERE handle = 'automation'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scopes",
                table: "operators");
        }
    }
}
