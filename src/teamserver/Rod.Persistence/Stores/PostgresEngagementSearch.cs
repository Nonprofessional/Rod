using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Collaboration;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IEngagementSearch"/> (architecture.md
/// Sec 15.3, ADR-005): one full-text query over the engagement's five
/// corpora -- task output, comments, artifact metadata, credentials, and
/// chat -- answered by the generated <c>tsvector</c> columns and their GIN
/// indexes (the migration owns that DDL; the EF model deliberately does
/// not map it, so the model snapshot stays clean of raw-SQL columns). The
/// 'simple' text-search configuration indexes verbatim lexemes: operator
/// queries are hostnames, principals, and paths, exactly the tokens
/// stemming would mangle. The credential corpus indexes its metadata
/// columns only -- a secret is never search text.
/// </summary>
internal sealed class PostgresEngagementSearch : IEngagementSearch
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresEngagementSearch(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    private sealed class SearchRow
    {
        public string Corpus { get; set; } = string.Empty;
        public string RefId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }
        public float Rank { get; set; }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        EngagementId engagementId,
        string query,
        IReadOnlySet<SearchCorpus>? corpora = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        // The corpus filter is assembled from enum names only -- no
        // caller text reaches the SQL string.
        IEnumerable<SearchCorpus> selected = corpora is null
            ? Enum.GetValues<SearchCorpus>()
            : corpora;
        var wanted = selected.Select(KindName).ToArray();
        var corpusFilter = string.Join(", ", wanted.Select(k => $"'{k}'"));

        // $$-interpolated: single braces are the SqlQueryRaw placeholders,
        // the corpus list is the one C# interpolation ({{...}}).
        var sql = $$"""
            SELECT * FROM (
                SELECT 'task_output' AS "Corpus", t.task_id::text AS "RefId", t.verb AS "Title",
                       left(coalesce(t.output, '') || ' ' || t.arguments, 240) AS "Excerpt",
                       coalesce(t.completed_at, t.created_at) AS "At",
                       ts_rank(t.search_tsv, websearch_to_tsquery('simple', {0})) AS "Rank"
                FROM tasks t
                WHERE t.engagement_id = {1} AND t.search_tsv @@ websearch_to_tsquery('simple', {0})
                UNION ALL
                SELECT 'comment', c.comment_id::text, 'comment on ' || c.subject_kind::text || '/' || c.subject_id::text,
                       left(c.body, 240), c.created_at,
                       ts_rank(c.search_tsv, websearch_to_tsquery('simple', {0}))
                FROM comments c
                WHERE c.engagement_id = {1} AND c.search_tsv @@ websearch_to_tsquery('simple', {0})
                UNION ALL
                SELECT 'artifact', a.artifact_id::text, a.name, left(a.name || ' ' || a.content_type, 240), a.stored_at,
                       ts_rank(a.search_tsv, websearch_to_tsquery('simple', {0}))
                FROM artifacts a
                WHERE a.engagement_id = {1} AND a.search_tsv @@ websearch_to_tsquery('simple', {0})
                UNION ALL
                SELECT 'credential', k.credential_id::text, k.principal,
                       left(k.kind || ' ' || k.principal || ' ' || coalesce(k.host, '') || ' ' || coalesce(k.note, ''), 240),
                       k.captured_at,
                       ts_rank(k.search_tsv, websearch_to_tsquery('simple', {0}))
                FROM credentials k
                WHERE k.engagement_id = {1} AND k.search_tsv @@ websearch_to_tsquery('simple', {0})
                UNION ALL
                SELECT 'chat_message', m.chat_message_id::text, 'chat', left(m.body, 240), m.sent_at,
                       ts_rank(m.search_tsv, websearch_to_tsquery('simple', {0}))
                FROM chat_messages m
                WHERE m.engagement_id = {1} AND m.search_tsv @@ websearch_to_tsquery('simple', {0})
            ) hits
            WHERE "Corpus" IN ({{corpusFilter}})
            ORDER BY "Rank" DESC, "At" DESC
            LIMIT {2}
            """;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database
            .SqlQueryRaw<SearchRow>(sql, query, engagementId.Value, limit)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SearchHit(
                KindOf(r.Corpus),
                Guid.Parse(r.RefId),
                r.Title,
                r.Excerpt,
                r.At))
            .ToArray();
    }

    private static SearchCorpus KindOf(string wireName)
        => Enum.GetValues<SearchCorpus>().Single(c => KindName(c) == wireName);

    private static string KindName(SearchCorpus corpus)
        => corpus switch
        {
            SearchCorpus.TaskOutput => "task_output",
            SearchCorpus.Comment => "comment",
            SearchCorpus.Artifact => "artifact",
            SearchCorpus.Credential => "credential",
            SearchCorpus.ChatMessage => "chat_message",
            _ => throw new ArgumentOutOfRangeException(nameof(corpus)),
        };
}
