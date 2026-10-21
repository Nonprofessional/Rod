using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Engagements;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's credential rows (architecture.md Sec 15.2's output
/// grammar, landed by Sec 15.3's collaboration work): what a completed
/// credential-kind task harvested, persisted once at capture with the
/// stable ids task references address (<c>$cred-&lt;id&gt;</c>) and the
/// search index reads. The store is the single source -- the harvest path
/// and this read share it, so the panel and the placeholder resolver can
/// never disagree.
///
/// Read-only by construction and scoped by engagement (architecture.md
/// Sec 3). Secret material renders masked by default in the UI; the row
/// carries it because the operator's report needs what was actually
/// harvested, and it never enters the audit chain (Sec 11's
/// mutable-content rule cuts the other way: chain events carry
/// operational facts, not harvest payloads) and never enters a search
/// index.
/// </summary>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/credentials", ListHarvestAsync)
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read))
            .WithName(nameof(ListHarvestAsync));
        return endpoints;
    }

    private static async Task<IResult> ListHarvestAsync(
        string engagementId,
        IEngagementRepository engagements,
        ICredentialStore credentials,
        Rod.CoreState.Tasks.ITaskRepository tasks,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var engagement = await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken);
        if (engagement is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        var rows = await credentials.ListByEngagementAsync(new EngagementId(engagementValue), cancellationToken);

        // The verb column renders the capturing task's verb; the store keeps
        // only ids, so the source task resolves once per distinct task and a
        // vanished task (an engagement whose rows predate a store reset, the
        // degenerate case) still renders.
        var verbs = new Dictionary<Guid, string>();
        var items = new List<CredentialRowResponse>(rows.Count);
        foreach (var r in rows)
        {
            if (!verbs.TryGetValue(r.SourceTaskId.Value, out var verb))
            {
                var task = await tasks.FindAsync(r.SourceTaskId, cancellationToken);
                verb = task?.Verb ?? string.Empty;
                verbs[r.SourceTaskId.Value] = verb;
            }

            items.Add(new CredentialRowResponse(
                r.Kind,
                r.Principal,
                r.Secret,
                r.Source,
                r.Host,
                r.Note,
                r.Id.Value.ToString(),
                r.SourceTaskId.ToString(),
                r.SourceImplantId.ToString(),
                verb,
                r.CapturedAt));
        }

        return Results.Ok(new CredentialListResponse([.. items]));
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// One harvested credential, attributed to the task that captured it.
    /// <paramref name="Id"/> is the row's stable id -- the value a
    /// <c>$cred-&lt;id&gt;</c> task reference addresses. The id renders in
    /// the hyphenated form so the placeholder grammar reads it directly.
    /// <paramref name="Secret"/> is the material when the source carried it
    /// (the reference <c>collect.cred</c> lists stores without secret
    /// material; a harvesting module may carry it) -- the UI masks it by
    /// default.
    /// </summary>
    public sealed record CredentialRowResponse(
        string Kind,
        string Principal,
        string? Secret,
        string? Source,
        string? Host,
        string? Note,
        string Id,
        string TaskId,
        string ImplantId,
        string Verb,
        DateTimeOffset CapturedAt);

    public sealed record CredentialListResponse(IReadOnlyList<CredentialRowResponse> Items);
}
