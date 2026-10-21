using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rod.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollaborationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_channels",
                columns: table => new
                {
                    chat_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_channels", x => x.chat_channel_id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    chat_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chat_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.chat_message_id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_kind = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comments", x => x.comment_id);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engagement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_implant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    principal = table.Column<string>(type: "text", nullable: false),
                    secret = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    host = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credentials", x => x.credential_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_channels_engagement_id",
                table: "chat_channels",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_channel_sent_at",
                table: "chat_messages",
                columns: new[] { "chat_channel_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_engagement_sent_at",
                table: "chat_messages",
                columns: new[] { "engagement_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_engagement_id",
                table: "comments",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_engagement_subject",
                table: "comments",
                columns: new[] { "engagement_id", "subject_kind", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_engagement_id",
                table: "credentials",
                column: "engagement_id");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_engagement_source_task",
                table: "credentials",
                columns: new[] { "engagement_id", "source_task_id" });

            // Full-text search over the engagement's five corpora
            // (architecture.md Sec 15.3, ADR-005): one generated tsvector
            // column per table, each under a GIN index, queried by the
            // Postgres search adapter. The columns live here in raw SQL --
            // the EF model deliberately does not map them, so the model
            // snapshot stays clean. 'simple' indexes verbatim lexemes:
            // operator queries are hostnames, principals, and paths, the
            // tokens English stemming would mangle. to_tsvector with a
            // regconfig argument is only stable, so the immutable wrapper
            // is what makes the GENERATED ... STORED form legal -- safe
            // here because the 'simple' configuration holds no dictionary
            // state to change under the index. The credentials column
            // covers the metadata columns only; a secret is never indexed.
            migrationBuilder.Sql("""
                CREATE FUNCTION rod_to_tsvector(body text) RETURNS tsvector
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS
                $fn$ SELECT to_tsvector('simple', body) $fn$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE comments
                    ADD COLUMN search_tsv tsvector
                    GENERATED ALWAYS AS (rod_to_tsvector(coalesce(body, ''))) STORED;
                CREATE INDEX ix_comments_search_tsv ON comments USING gin (search_tsv);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE chat_messages
                    ADD COLUMN search_tsv tsvector
                    GENERATED ALWAYS AS (rod_to_tsvector(coalesce(body, ''))) STORED;
                CREATE INDEX ix_chat_messages_search_tsv ON chat_messages USING gin (search_tsv);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE credentials
                    ADD COLUMN search_tsv tsvector
                    GENERATED ALWAYS AS (rod_to_tsvector(
                        coalesce(kind, '') || ' ' || coalesce(principal, '') || ' '
                        || coalesce(host, '') || ' ' || coalesce(note, ''))) STORED;
                CREATE INDEX ix_credentials_search_tsv ON credentials USING gin (search_tsv);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE tasks
                    ADD COLUMN search_tsv tsvector
                    GENERATED ALWAYS AS (rod_to_tsvector(
                        coalesce(verb, '') || ' ' || coalesce(arguments, '') || ' '
                        || coalesce(output, ''))) STORED;
                CREATE INDEX ix_tasks_search_tsv ON tasks USING gin (search_tsv);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE artifacts
                    ADD COLUMN search_tsv tsvector
                    GENERATED ALWAYS AS (rod_to_tsvector(
                        coalesce(name, '') || ' ' || coalesce(content_type, ''))) STORED;
                CREATE INDEX ix_artifacts_search_tsv ON artifacts USING gin (search_tsv);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The full-text columns first: they sit partly on tables this
            // migration drops and partly on tasks/artifacts, which stay.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_comments_search_tsv;
                DROP INDEX IF EXISTS ix_chat_messages_search_tsv;
                DROP INDEX IF EXISTS ix_credentials_search_tsv;
                DROP INDEX IF EXISTS ix_tasks_search_tsv;
                DROP INDEX IF EXISTS ix_artifacts_search_tsv;
                ALTER TABLE comments DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE chat_messages DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE credentials DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE tasks DROP COLUMN IF EXISTS search_tsv;
                ALTER TABLE artifacts DROP COLUMN IF EXISTS search_tsv;
                DROP FUNCTION IF EXISTS rod_to_tsvector(text);
                """);

            migrationBuilder.DropTable(
                name: "chat_channels");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "credentials");
        }
    }
}
