using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskEnqueueSequenceAndTokenHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "enqueue_seq",
                table: "tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_enqueue_seq",
                table: "tasks",
                column: "enqueue_seq");

            migrationBuilder.CreateIndex(
                name: "ux_stager_tokens_secret_hash",
                table: "stager_tokens",
                column: "secret_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tasks_enqueue_seq",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "ux_stager_tokens_secret_hash",
                table: "stager_tokens");

            migrationBuilder.DropColumn(
                name: "enqueue_seq",
                table: "tasks");
        }
    }
}
