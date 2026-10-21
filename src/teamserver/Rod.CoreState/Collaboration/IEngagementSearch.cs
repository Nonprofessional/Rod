namespace Rod.CoreState.Collaboration;

/// <summary>
/// Which corpus a search hit came from (architecture.md Sec 15.3, ADR-005):
/// the engagement's five searchable bodies of text.
/// </summary>
public enum SearchCorpus
{
    TaskOutput,
    Comment,
    Artifact,
    Credential,
    ChatMessage,
}

/// <summary>
/// One global-search hit: the corpus, the hit's id (the entity to open),
/// a title line, an excerpt of the matched text, and when the hit was
/// written. The excerpt never carries a credential's secret -- the
/// credential corpus indexes its metadata columns only.
/// </summary>
public sealed record SearchHit(
    SearchCorpus Corpus,
    Guid RefId,
    string Title,
    string Excerpt,
    DateTimeOffset At);

/// <summary>
/// The engagement's global search (architecture.md Sec 15.3, ADR-005): one
/// query across task output, comments, artifact metadata, credentials, and
/// chat. Postgres answers through <c>tsvector</c> + GIN indexes; the
/// in-memory twin is a substring scan over the same corpora -- the
/// development and test shape, with the acceptance bar (a sub-100 ms answer
/// at ten-thousand events) held by the Postgres adapter. Engagement-scoped
/// by construction; no external search engine stands beside it.
/// </summary>
public interface IEngagementSearch
{
    /// <summary>
    /// Searches one engagement's corpora, newest hit first, at most
    /// <paramref name="limit"/> rows. An empty or whitespace query returns
    /// nothing. <paramref name="corpora"/> narrows the sweep; null searches
    /// all five.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        EngagementId engagementId,
        string query,
        IReadOnlySet<SearchCorpus>? corpora = null,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
