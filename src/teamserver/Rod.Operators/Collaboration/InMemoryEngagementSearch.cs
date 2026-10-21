using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Tasks;

namespace Rod.Operators.Collaboration;

/// <summary>
/// The in-memory <see cref="IEngagementSearch"/> twin: a case-insensitive
/// substring sweep over the same five corpora the Postgres adapter indexes,
/// with the same exclusion (a credential's secret never matches -- only its
/// metadata columns). This is the no-Postgres development and test shape;
/// the acceptance bar (a sub-100 ms answer at ten-thousand events) is the
/// Postgres adapter's to hold. The sweep reads the stores directly, so an
/// edit or a delete is visible to the next search with no index to keep
/// true.
/// </summary>
public sealed class InMemoryEngagementSearch : IEngagementSearch
{
    private readonly ITaskRepository _tasks;
    private readonly ICommentStore _comments;
    private readonly IChatStore _chat;
    private readonly ICredentialStore _credentials;
    private readonly IArtifactStore _artifacts;

    public InMemoryEngagementSearch(
        ITaskRepository tasks,
        ICommentStore comments,
        IChatStore chat,
        ICredentialStore credentials,
        IArtifactStore artifacts)
    {
        _tasks = tasks;
        _comments = comments;
        _chat = chat;
        _credentials = credentials;
        _artifacts = artifacts;
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

        var needle = query.Trim();
        var hits = new List<SearchHit>();

        if (Wants(corpora, SearchCorpus.TaskOutput))
        {
            foreach (var task in await _tasks.ListByEngagementAsync(engagementId, cancellationToken))
            {
                var haystack = $"{task.Verb}\n{task.Arguments}\n{task.Output}";
                var (matched, excerpt) = Match(haystack, needle);
                if (matched)
                    hits.Add(new SearchHit(
                        SearchCorpus.TaskOutput, task.Id.Value, task.Verb, excerpt, task.CompletedAt ?? task.CreatedAt));
            }
        }

        if (Wants(corpora, SearchCorpus.Comment))
        {
            var page = await _comments.ListPageAsync(engagementId, int.MaxValue, null, null, cancellationToken);
            foreach (var comment in page.Items)
            {
                var (matched, excerpt) = Match(comment.Body, needle);
                if (matched)
                    hits.Add(new SearchHit(
                        SearchCorpus.Comment, comment.Id.Value,
                        $"comment on {comment.SubjectKind.ToString().ToLowerInvariant()}/{comment.SubjectId}",
                        excerpt, comment.CreatedAt));
            }
        }

        if (Wants(corpora, SearchCorpus.ChatMessage))
        {
            var page = await _chat.ListPageByEngagementAsync(engagementId, int.MaxValue, null, null, cancellationToken);
            foreach (var message in page.Items)
            {
                var (matched, excerpt) = Match(message.Body, needle);
                if (matched)
                    hits.Add(new SearchHit(SearchCorpus.ChatMessage, message.Id.Value, "chat", excerpt, message.SentAt));
            }
        }

        if (Wants(corpora, SearchCorpus.Credential))
        {
            foreach (var credential in await _credentials.ListByEngagementAsync(engagementId, cancellationToken))
            {
                // Metadata only -- the secret is not searchable text.
                var haystack = $"{credential.Kind}\n{credential.Principal}\n{credential.Source}\n{credential.Host}\n{credential.Note}";
                var (matched, excerpt) = Match(haystack, needle);
                if (matched)
                    hits.Add(new SearchHit(
                        SearchCorpus.Credential, credential.Id.Value, credential.Principal, excerpt, credential.CapturedAt));
            }
        }

        if (Wants(corpora, SearchCorpus.Artifact))
        {
            foreach (var artifact in await _artifacts.ListAsync(engagementId.Value, cancellationToken))
            {
                var haystack = $"{artifact.Name}\n{artifact.ContentType}";
                var (matched, excerpt) = Match(haystack, needle);
                if (matched)
                    hits.Add(new SearchHit(
                        SearchCorpus.Artifact, artifact.ArtifactId, artifact.Name, excerpt, artifact.StoredAt));
            }
        }

        return hits
            .OrderByDescending(h => h.At)
            .Take(limit)
            .ToArray();
    }

    private static bool Wants(IReadOnlySet<SearchCorpus>? corpora, SearchCorpus corpus)
        => corpora is null || corpora.Contains(corpus);

    // One pass, case-insensitive: the matched window around the first hit is
    // the excerpt, bounded so a transcript match stays a glimpse.
    private static (bool Matched, string Excerpt) Match(string haystack, string needle)
    {
        var index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return (false, string.Empty);

        const int window = 120;
        var start = Math.Max(0, index - 20);
        var length = Math.Min(window, haystack.Length - start);
        var excerpt = haystack.Substring(start, length).Replace("\n", " ").TrimEnd();
        return (true, excerpt.Length < haystack.Length ? excerpt + "…" : excerpt);
    }
}
