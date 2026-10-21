using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The engagement's global search (architecture.md Sec 15.3, ADR-005): one
/// query across task output, comments, artifact metadata, credentials, and
/// chat. Hosted by the operator layer; scoped by the route engagement, so
/// no result can cross the isolation boundary. The Postgres adapter
/// answers through tsvector + GIN; the in-memory twin sweeps the stores --
/// either way, no external search engine stands beside the teamserver.
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/search", SearchAsync)
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read))
            .WithName("EngagementSearch");
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        string engagementId,
        string? q,
        string? corpora,
        int? limit,
        IEngagementRepository engagements,
        Rod.CoreState.Collaboration.IEngagementSearch search,
        CancellationToken cancellationToken)
    {
        if (!EngagementId.TryParse(engagementId, out var engagement))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (await engagements.FindAsync(engagement, cancellationToken) is null)
            return Results.NotFound(new Problem("Unknown engagement."));
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new Problem("A search needs a query (q)."));

        var boundLimit = limit ?? 50;
        if (boundLimit is < 1 or > 200)
            return Results.BadRequest(new Problem("limit must be between 1 and 200."));

        // The corpus filter is comma-separated kind names -- the narrow
        // sweep behind a corpus chip in the console; unfiltered searches
        // all five.
        IReadOnlySet<SearchCorpus>? scope = null;
        if (!string.IsNullOrWhiteSpace(corpora))
        {
            var kinds = new HashSet<SearchCorpus>();
            foreach (var name in corpora.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // The filter reads the same snake_case wire names the hits
                // render (SearchHitResponse.WireName).
                var kind = Enum.GetValues<SearchCorpus>()
                    .FirstOrDefault(c => SearchHitResponse.WireName(c) == name);
                if (kind == default && SearchHitResponse.WireName(kind) != name)
                    return Results.BadRequest(new Problem($"Unknown search corpus '{name}'."));
                kinds.Add(kind);
            }

            scope = kinds;
        }

        var hits = await search.SearchAsync(engagement, q, scope, boundLimit, cancellationToken);
        return Results.Ok(new SearchResponse(hits.Select(SearchHitResponse.Of).ToArray()));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record SearchHitResponse(
        string Corpus,
        string RefId,
        string Title,
        string Excerpt,
        DateTimeOffset At)
    {
        public static SearchHitResponse Of(SearchHit hit)
            => new(WireName(hit.Corpus), hit.RefId.ToString(), hit.Title, hit.Excerpt, hit.At);

        // The wire names the corpus filter also reads, snake_case to match
        // the enum's compound names.
        public static string WireName(SearchCorpus corpus)
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

    public sealed record SearchResponse(IReadOnlyList<SearchHitResponse> Items);
}
