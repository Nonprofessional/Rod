using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameWebShellAdapterIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The one-liner family's registry id was renamed; stored
            // profiles carry the wire id, so the rows follow it.
            migrationBuilder.Sql(
                "UPDATE webshell_profiles SET \"AdapterId\" = 'eval-php' WHERE \"AdapterId\" = 'antsword-php';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE webshell_profiles SET \"AdapterId\" = 'antsword-php' WHERE \"AdapterId\" = 'eval-php';");
        }
    }
}
