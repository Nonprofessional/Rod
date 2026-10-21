using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Collaboration;
using Rod.CoreState.Operators;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing operational-event-log read endpoint: the
/// per-engagement, append-only, attributed event stream (architecture.md
/// Sec 11), merged with the engagement's mutable process knowledge
/// (architecture.md Sec 15.3, ADR-005) -- comments and chat ride the
/// timeline as ordinary events, carried as explicitly mutable,
/// hash-less entries so the chain's semantics stay intact: the audit rows
/// remain the immutable evidence, the collaboration rows remain editable
/// knowledge, and neither pretends to be the other. Distinct from the
/// operators-layer <c>GET .../events</c> SSE route, which is the transient
/// live fan-out, not the durable reads.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: the event trail requires an authenticated operator session.
        var group = endpoints
            .MapGroup("/engagements/{engagementId}/audit")
            .RequireAuthorization().AddEndpointFilter(new EngagementAccessFilter(EngagementAccessRequirement.Read));
        group.MapGet("/", ListAsync).WithName(nameof(ListAsync));

        // The deployment bucket's trail (Sec 9): the account and fleet
        // changes that bind to no engagement, recorded under the empty
        // engagement id. The admin's alone to read -- it is the management
        // history of the whole deployment. Collaboration rows are
        // engagement-scoped by construction, so this view stays chain-only.
        endpoints.MapGet("/audit/deployment", DeploymentAsync)
            .RequireAuthorization(policy => policy.RequireClaim(
                OperatorClaims.DeploymentRoleClaimType, DeploymentRoles.AdminName))
            .WithName(nameof(DeploymentAsync));
        return endpoints;
    }

    private static async Task<IResult> DeploymentAsync(
        int? limit,
        string? cursor,
        IAuditStore audit,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!ListPaging.TryBind(limit, cursor, c => TimestampIdCursor.TryDecode(c, out _, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        // The bucket pages like an engagement trail: newest window first
        // across pages, oldest first within a page.
        var page = await audit.ListPageAsync(Guid.Empty, boundLimit, boundCursor, cancellationToken);

        var handles = new Dictionary<Guid, string>();
        foreach (var id in page.Items.Select(e => e.OperatorId).Where(id => id != Guid.Empty).Distinct())
        {
            var op = await operators.FindAsync(new OperatorId(id), cancellationToken);
            handles[id] = op?.Handle ?? id.ToString();
        }

        var body = new AuditListResponse(
            page.Items.Select(e => AuditEventEntry.Of(e, handles.GetValueOrDefault(e.OperatorId))).ToArray(),
            page.NextCursor);
        return Results.Ok(body);
    }

    private static async Task<IResult> ListAsync(
        string engagementId,
        int? limit,
        string? cursor,
        IAuditStore audit,
        ICommentStore comments,
        IChatStore chat,
        IOperatorRepository operators,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (!ListPaging.TryBind(limit, cursor, c => TimestampIdCursor.TryDecode(c, out _, out _),
                out var boundLimit, out var boundCursor, out var pagingError))
        {
            return Results.BadRequest(new Problem(pagingError));
        }

        // The trail is per-engagement by construction (architecture.md Sec 3/11);
        // cross-engagement access never reaches here with another engagement's id.
        // One page per request: newest window first across pages, oldest first
        // within a page so the page itself still reads in causal order. The
        // collaboration halves page on the same (timestamp, id) keyset -- the
        // cursor decodes once here and every source walks the same window.
        DateTimeOffset? before = null;
        Guid? beforeId = null;
        if (boundCursor is not null
            && TimestampIdCursor.TryDecode(boundCursor, out var afterTicks, out var afterId))
        {
            before = new DateTimeOffset(afterTicks, TimeSpan.Zero);
            beforeId = afterId;
        }

        var engagement = new EngagementId(engagementValue);
        var auditPage = await audit.ListPageAsync(engagementValue, boundLimit, boundCursor, cancellationToken);
        var commentPage = await comments.ListPageAsync(engagement, boundLimit, before, beforeId, cancellationToken);
        var chatPage = await chat.ListPageByEngagementAsync(engagement, boundLimit, before, beforeId, cancellationToken);

        // Merge the three sources' newest windows into one newest-first cut,
        // then read the page oldest-first like every listing. Every source
        // pages the same (timestamp, id) keyset, so one cursor walks them
        // together and a source running dry just stops contributing.
        var candidates = new List<(DateTimeOffset At, Guid Id, AuditEventEntry Entry)>();
        candidates.AddRange(auditPage.Items.Select(e => (e.At, e.EventId, AuditEventEntry.Of(e))));
        candidates.AddRange(commentPage.Items.Select(c => (c.CreatedAt, c.Id.Value, CommentEntry.Of(c))));
        candidates.AddRange(chatPage.Items.Select(m => (m.SentAt, m.Id.Value, ChatMessageEntry.Of(m))));

        // Resolve the page's operator ids to handles once per request: an
        // operator reading the timeline sees who acted, not a bare guid they
        // cannot map back to an account. The same tolerance the report
        // applies -- system for the unattributed Guid.Empty, the bare id when
        // the operator record no longer resolves (an entry outliving its
        // account).
        var handles = new Dictionary<Guid, string>();
        foreach (var id in candidates.Select(c => c.Entry.OperatorId).Where(id => id != Guid.Empty).Distinct())
        {
            var op = await operators.FindAsync(new OperatorId(id), cancellationToken);
            handles[id] = op?.Handle ?? id.ToString();
        }

        var cut = candidates
            .OrderByDescending(c => c.At)
            .ThenByDescending(c => c.Id)
            .Take(boundLimit)
            .Select(c => c.Entry with
            {
                OperatorHandle = handles.TryGetValue(c.Entry.OperatorId, out var handle)
                    ? handle
                    : (c.Entry.OperatorId == Guid.Empty ? "system" : c.Entry.OperatorId.ToString()),
            })
            .ToArray();
        Array.Reverse(cut); // Oldest first within the page.

        var hasMore = auditPage.NextCursor is not null || commentPage.HasMore || chatPage.HasMore;
        var body = new AuditListResponse(
            cut,
            hasMore && cut.Length > 0 ? TimestampIdCursor.Encode(cut[0].At, cut[0].EventId) : null);
        return Results.Ok(body);
    }

    // The collaboration projections of the timeline: one entry per comment
    // row (the body renders as written now -- an edit moves the row, it
    // does not fork history) and one per chat message. Both carry
    // Mutable = true so a consumer can tell process knowledge from
    // evidence without knowing the kind vocabulary.

    private sealed record CommentEntry
    {
        public static AuditEventEntry Of(Comment c)
            => new(
                c.Id.Value,
                Kind: "Comment",
                Verb: "comment",
                c.AuthorId.Value,
                OperatorHandle: string.Empty,
                c.SubjectKind == CommentSubjectKind.Implant ? c.SubjectId : Guid.Empty,
                c.SubjectKind == CommentSubjectKind.Task ? c.SubjectId : Guid.Empty,
                c.Body,
                Output: c.EditedAt is { } edited ? $"edited {edited:O}" : null,
                Outcome: $"{c.SubjectKind.ToString().ToLowerInvariant()}:{c.SubjectId}",
                c.CreatedAt,
                Mutable: true);
    }

    private sealed record ChatMessageEntry
    {
        public static AuditEventEntry Of(ChatMessage m)
            => new(
                m.Id.Value,
                Kind: "ChatMessage",
                Verb: "chat",
                m.AuthorId.Value,
                OperatorHandle: string.Empty,
                ImplantId: Guid.Empty,
                TaskId: Guid.Empty,
                m.Body,
                Output: null,
                Outcome: $"channel:{m.ChannelId}",
                m.SentAt,
                Mutable: true);
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// A single attributed event on the engagement timeline. Carries the full
    /// attribution surface (operator/implant/task ids) so the timeline shows who
    /// did what against which entity, plus the resolved <see cref="OperatorHandle"/>
    /// an operator actually reads ("system" for unattributed events), and the
    /// kind/verb/payload/outcome that describe the action. The hash-chain fields
    /// are not surfaced here: the trail is tamper-evident by construction and a
    /// report consumer reads the facts, not the chain internals.
    /// <see cref="Mutable"/> marks the collaboration rows (comments, chat)
    /// that ride the timeline as ordinary events without entering the chain.
    /// </summary>
    public sealed record AuditEventEntry(
        Guid EventId,
        string Kind,
        string Verb,
        Guid OperatorId,
        string OperatorHandle,
        Guid ImplantId,
        Guid TaskId,
        string Payload,
        string? Output,
        string Outcome,
        DateTimeOffset At,
        bool Mutable = false)
    {
        public static AuditEventEntry Of(AuditEvent e, string? operatorHandle = null)
            => new(
                e.EventId,
                e.Kind.ToString(),
                e.Verb,
                e.OperatorId,
                operatorHandle ?? (e.OperatorId == Guid.Empty ? "system" : e.OperatorId.ToString()),
                e.ImplantId,
                e.TaskId,
                e.Payload,
                e.Output,
                e.Outcome,
                e.At);
    }

    /// <summary>
    /// One page of the engagement's timeline: the page's events (oldest first
    /// within the page) plus the cursor that walks one page older, null when the
    /// beginning of the timeline is reached.
    /// </summary>
    public sealed record AuditListResponse(
        AuditEventEntry[] Items,
        string? NextCursor);
}
