using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing listener endpoints ( read view,
/// repoint): which listeners are bound and serving, their transports, bind
/// addresses, and -- crucially -- the public endpoints implants dial (typically
/// a redirector, decoupled from the bind address per architecture.md Sec 8).
/// Listeners are bound at startup; at runtime an operator can repoint a
/// listener's public endpoint to swap a burned redirector without touching the
/// backend (architecture.md Sec 7/8).
/// </summary>
public static class ListenerEndpoints
{
    public static IEndpointRouteBuilder MapListenerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: listener views and repoint require an authenticated
        // operator session.
        var group = endpoints.MapGroup("/listeners").RequireAuthorization();

        group.MapGet("/", ListListenersAsync).WithName(nameof(ListListenersAsync));
        group.MapGet("/{id}", GetListenerAsync).WithName(nameof(GetListenerAsync));
        group.MapPost("/{id}:repoint", RepointAsync).WithName(nameof(RepointAsync));

        return endpoints;
    }

    private static async Task<IResult> ListListenersAsync(
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        var all = await listeners.ListAsync(cancellationToken);
        var body = all.Select(Response.Of).ToArray();
        return Results.Ok(body);
    }

    private static async Task<IResult> GetListenerAsync(
        string id,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (!ListenerId.TryParse(id, out var listenerId))
            return Results.BadRequest(new Problem("Listener id is not a valid identifier."));

        var listener = await listeners.FindAsync(listenerId, cancellationToken);
        if (listener is null)
            return Results.NotFound(new Problem("Listener is not registered."));

        return Results.Ok(Response.Of(listener));
    }

    private static async Task<IResult> RepointAsync(
        string id,
        RepointListenerRequest body,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (!ListenerId.TryParse(id, out var listenerId))
            return Results.BadRequest(new Problem("Listener id is not a valid identifier."));
        if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
            return Results.BadRequest(new Problem("Public endpoint is required."));

        // The public endpoint is the address built implants dial and the
        // roster shows: an absolute http(s) URL or a bare host:port (the
        // redirector shape, e.g. 203.0.113.10:443). Anything else -- a number,
        // a path -- would be copied into payload builds and silently strand
        // them, so the repoint is refused naming the accepted shapes.
        if (!IsPublicEndpoint(body.PublicEndpoint))
            return Results.BadRequest(new Problem(
                "Public endpoint must be an absolute http(s) URL or a host:port pair " +
                $"(e.g. http://redirect.example.test or 203.0.113.10:443), got '{body.PublicEndpoint}'."));

        // Repoint swaps the public endpoint -- the redirector implants dial --
        // without touching the bound socket (architecture.md Sec 7/8). The
        // bind address stays put, so a live listener keeps serving; the registry's
        // public-endpoint lookup now resolves the new endpoint and no longer
        // resolves the old one (a burned redirector is severed).
        var listener = await listeners.RepointAsync(listenerId, body.PublicEndpoint, cancellationToken);
        if (listener is null)
            return Results.NotFound(new Problem("Listener is not registered."));

        return Results.Ok(Response.Of(listener));
    }

    // A public endpoint is either an absolute http(s) URL or a bare
    // host:port -- both shapes are documented deployments (the URL is what a
    // payload build bakes, host:port is the redirector front). A DNS name or
    // literal IP with a port is enough; no scheme-less bare host, because
    // nothing downstream can guess a port.
    private static bool IsPublicEndpoint(string text)
    {
        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return false;
        if (!int.TryParse(value[(colon + 1)..], out var port) || port is < 1 or > 65535)
            return false;
        var host = value[..colon];
        return host.Length > 0
            && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// Request to repoint a listener's public endpoint. The new endpoint is the
    /// redirector or host-header implants should dial after the swap.
    /// </summary>
    public sealed record RepointListenerRequest(string PublicEndpoint);

    public sealed record ListenerResponse(
        string Id,
        string Name,
        string Transport,
        string BindAddress,
        string PublicEndpoint,
        string State,
        DateTimeOffset CreatedAt,
        DateTimeOffset? RepointedAt);

    private static class Response
    {
        public static ListenerResponse Of(Listener l)
            => new(
                l.Id.ToString(),
                l.Name,
                // The stable kebab-case wire name (HttpsEnvelope -> "https-envelope"),
                // which the listing and the operator UI render verbatim.
                l.Transport.WireName(),
                l.BindAddress,
                l.PublicEndpoint,
                l.State.ToString().ToLowerInvariant(),
                l.CreatedAt,
                l.RepointedAt);
    }

    public sealed record Problem(string Error);
}
