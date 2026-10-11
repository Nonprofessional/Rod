using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Campaigns;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

// The lure serving edge (architecture.md Sec 11.5, Sec 8): the public route
// a campaign recipient's message points at. The unguessable route id is the
// credential -- the same capability-URL shape the hook serving edge keeps --
// so the route serves without a header or a token, the shape a link click
// can carry. The link route both records the click and delivers the
// recipient's built artifact (the click is the evidence and the delivery);
// the pixel route records the open and answers a 1x1 gif. Both run the same
// ingress scope refusal the enroll and fetch routes run, so a shared-tier
// socket (the operator front) carries no lure, and a revoked campaign's
// lures are indistinguishable from nonexistent ones.

/// <summary>
/// Maps the lure serving routes onto the implant family.
/// </summary>
public static class LureServingEndpoints
{
    /// <summary>The lure route, in the implant family with enroll and hooks.</summary>
    public const string Route = "/implants/lures/{lureId}";

    // The 1x1 transparent gif the pixel route answers -- the only body an
    // <img> tag needs, small enough that the open's cost is the request
    // itself. Inline rather than a file: it is a constant, not an asset.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    public static IEndpointRouteBuilder MapLureServingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, FetchLureAsync).WithName(nameof(FetchLureAsync));
        endpoints.MapGet(Route + "/open", FetchLurePixelAsync).WithName(nameof(FetchLurePixelAsync));
        return endpoints;
    }

    private static async Task<IResult> FetchLureAsync(
        string lureId,
        HttpRequest http,
        ICampaignStore campaigns,
        IListenerRegistry listeners,
        IPayloadStore payloads,
        IAuditStore audit,
        ILiveEventBus live,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(lureId, out var lureValue))
            return Results.BadRequest(new Problem("Lure id is not a valid identifier."));

        var lure = await campaigns.FindByLureAsync(lureValue, cancellationToken);
        if (lure is null)
            return Results.NotFound();

        var refusal = await ScopeRefusalAsync(http, listeners, lure.EngagementId, cancellationToken);
        if (refusal is not null)
            return refusal;

        var campaign = await campaigns.FindAsync(lure.CampaignId, cancellationToken);
        if (campaign is null || campaign.State == CampaignState.Revoked)
            return Results.NotFound();

        // The artifact must exist before the lure can deliver it: the link
        // exists from creation, the artifact only once the per-recipient
        // build lands. A click that arrives before then 404s -- evidence of
        // interest the trail keeps below, but nothing to serve.
        if (lure.PayloadId is not { } payloadId)
        {
            await RecordServeAsync(http, campaigns, audit, live, clock, lure, click: true, served: false, cancellationToken);
            return Results.NotFound();
        }

        var payload = await payloads.FindAsync(payloadId, lure.EngagementId.Value, cancellationToken);
        if (payload is null)
            return Results.NotFound();

        await RecordServeAsync(http, campaigns, audit, live, clock, lure, click: true, served: true, cancellationToken);

        http.HttpContext.Response.Headers.CacheControl = "no-store";
        // The download's shape follows the artifact's, the library download's
        // own rule: a zip bundle keeps its zip name, a Windows executable
        // carries .exe, everything else the extensionless .bin.
        var extension =
            payload.ContentType == "application/zip" ? ".dll.zip"
            : payload.Target?.StartsWith("windows", StringComparison.OrdinalIgnoreCase) == true ? ".exe"
            : ".bin";
        var fileName = $"rod-{payload.Class.ToLowerInvariant()}-{payload.PayloadId.ToString("N")[..8]}{extension}";
        return Results.File(payload.Content, payload.ContentType, fileName);
    }

    private static async Task<IResult> FetchLurePixelAsync(
        string lureId,
        HttpRequest http,
        ICampaignStore campaigns,
        IListenerRegistry listeners,
        IAuditStore audit,
        ILiveEventBus live,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(lureId, out var lureValue))
            return Results.BadRequest(new Problem("Lure id is not a valid identifier."));

        var lure = await campaigns.FindByLureAsync(lureValue, cancellationToken);
        if (lure is null)
            return Results.NotFound();

        var refusal = await ScopeRefusalAsync(http, listeners, lure.EngagementId, cancellationToken);
        if (refusal is not null)
            return refusal;

        var campaign = await campaigns.FindAsync(lure.CampaignId, cancellationToken);
        if (campaign is null || campaign.State == CampaignState.Revoked)
            return Results.NotFound();

        await RecordServeAsync(http, campaigns, audit, live, clock, lure, click: false, served: true, cancellationToken);

        http.HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.File(Pixel, "image/gif");
    }

    // The shared scope refusal: a socket the registry does not know (the
    // in-memory test harness) stays permissive; everything else must answer
    // for the campaign's engagement -- the same rule enroll and the payload
    // fetch run, so the operator front and every foreign engagement carry no
    // lure either.
    private static async Task<IResult?> ScopeRefusalAsync(
        HttpRequest http,
        IListenerRegistry listeners,
        EngagementId engagement,
        CancellationToken cancellationToken)
    {
        var listener = await listeners.FindByLocalPortAsync(
            http.HttpContext.Connection.LocalPort, cancellationToken);
        if (listener is not null && listener.EngagementId != engagement)
            return Results.NotFound();
        return null;
    }

    // Records one serve -- the evidence stamp on the recipient row, the
    // audit fact, and the live event -- whether the route had an artifact
    // to deliver or not. What the wire showed is captured before anything
    // else, the same first-touch shape a hook fetch records.
    private static async Task RecordServeAsync(
        HttpRequest http,
        ICampaignStore campaigns,
        IAuditStore audit,
        ILiveEventBus live,
        TimeProvider clock,
        CampaignLure lure,
        bool click,
        bool served,
        CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var remote = http.HttpContext.Connection.RemoteIpAddress is { } remoteIp
            ? $"{remoteIp}:{http.HttpContext.Connection.RemotePort}"
            : "unknown";
        var userAgent = http.Headers.UserAgent.ToString();

        if (click)
            await campaigns.NoteClickedAsync(lure.CampaignId, lure.RecipientId, at, cancellationToken);
        else
            await campaigns.NoteOpenedAsync(lure.CampaignId, lure.RecipientId, at, cancellationToken);

        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: lure.EngagementId.Value,
                operatorId: OperatorId.Empty.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: click ? "campaign.click" : "campaign.open",
                kind: AuditEventKind.CampaignLinkServed,
                payload: $"campaign='{lure.CampaignName}' ({lure.CampaignId}) "
                    + $"recipient={lure.RecipientEmail} "
                    + $"{(click ? "click" : "open")}={(served ? "served" : "no-artifact")} "
                    + $"remote={remote} listenerPort={http.HttpContext.Connection.LocalPort} "
                    + $"ua={(string.IsNullOrWhiteSpace(userAgent) ? "none" : userAgent)}",
                output: null,
                outcome: lure.RecipientId.ToString(),
                at),
            cancellationToken);

        await live.PublishAsync(
            LiveEvent.CampaignActivity(
                lure.EngagementId,
                OperatorId.Empty,
                $"{(click ? "clicked" : "opened")} recipient={lure.RecipientEmail} campaign={lure.CampaignId}",
                at),
            cancellationToken);
    }
}
