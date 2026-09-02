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
        group.MapPost("/", CreateListenerAsync).WithName(nameof(CreateListenerAsync));
        group.MapPost("/{id}:repoint", RepointAsync).WithName(nameof(RepointAsync));
        group.MapDelete("/{id}", DeleteListenerAsync).WithName(nameof(DeleteListenerAsync));

        return endpoints;
    }

    private static async Task<IResult> CreateListenerAsync(
        CreateListenerRequest body,
        ListenerManager manager,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Listener name is required."));
        if (!Enum.TryParse<ListenerTransport>(body.Transport, ignoreCase: true, out var transport))
            return Results.BadRequest(new Problem(
                "Transport is not recognized. Use one of: " +
                string.Join(", ", Enum.GetNames<ListenerTransport>().Select(t => t.ToLowerInvariant())) + "."));
        if (string.IsNullOrWhiteSpace(body.BindAddress))
            return Results.BadRequest(new Problem("Bind address is required."));
        if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
            return Results.BadRequest(new Problem("Public endpoint is required."));

        // The public endpoint's accepted shape is transport-shaped: what a
        // payload dials on the HTTP transports (a URL or host:port), the zone
        // a DNS listener answers for (a bare domain), or the pipe path an SMB
        // listener serves. A malformed value would ride into payload builds
        // and strand them, so it is refused here, not at dial time.
        if (!IsAcceptablePublicEndpoint(transport, body.PublicEndpoint))
            return Results.BadRequest(new Problem(PublicEndpointRule(transport, body.PublicEndpoint)));

        try
        {
            var listener = await manager.CreateAsync(
                new ListenerConfig(body.Name.Trim(), transport, body.BindAddress.Trim(), body.PublicEndpoint.Trim()),
                cancellationToken);

            return Results.Created($"/listeners/{listener.Id}", Response.Of(listener));
        }
        catch (ArgumentException ex)
        {
            // A malformed bind address for the transport: an operator mistake.
            return Results.BadRequest(new Problem(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // The bind was refused (address in use, socket never opened).
            return Results.Conflict(new Problem(ex.Message));
        }
    }

    private static async Task<IResult> DeleteListenerAsync(
        string id,
        ListenerManager manager,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (!ListenerId.TryParse(id, out var listenerId))
            return Results.BadRequest(new Problem("Listener id is not a valid identifier."));

        var listener = await listeners.FindAsync(listenerId, cancellationToken);
        if (listener is null)
            return Results.NotFound(new Problem("Listener is not registered."));

        // Startup-configuration listeners are owned by that configuration: the
        // runtime manager can only remove what it created, so the operator
        // edits the configuration and restarts -- said plainly, not as a 500.
        if (!manager.IsRuntime(listenerId))
            return Results.Conflict(new Problem(
                $"Listener '{listener.Name}' was bound from startup configuration; remove its entry there and restart."));

        await manager.RemoveAsync(listenerId, cancellationToken);
        return Results.NoContent();
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

    // The create-time check is transport-shaped: the HTTP transports dial an
    // endpoint (IsPublicEndpoint), the DNS listener answers a zone (a bare
    // domain), and the SMB listener serves a pipe path. The pipe and raw-TCP
    // transports accept the same host-shaped forms as their deployment docs.
    private static bool IsAcceptablePublicEndpoint(ListenerTransport transport, string text)
    {
        var value = text.Trim().TrimEnd('.');
        if (value.Length == 0)
            return false;

        return transport switch
        {
            ListenerTransport.Http or ListenerTransport.Mtls or ListenerTransport.HttpsEnvelope
                => IsPublicEndpoint(text),
            // A zone or pipe path: letters, digits, dots, hyphens, and the
            // Windows pipe prefix's backslashes.
            ListenerTransport.Dns
                => value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-'),
            ListenerTransport.Smb
                => value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '\\' or '_'),
            ListenerTransport.Tcp
                => value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '_'),
            _ => false,
        };
    }

    // One sentence naming the accepted shapes, so the refusal teaches.
    private static string PublicEndpointRule(ListenerTransport transport, string got) => transport switch
    {
        ListenerTransport.Dns => $"Public endpoint must be the DNS zone this listener answers for (e.g. c2.example.test), got '{got}'.",
        ListenerTransport.Smb => $"Public endpoint must be the pipe path implants dial (e.g. \\\\host\\pipe\\name), got '{got}'.",
        _ => "Public endpoint must be an absolute http(s) URL or a host:port pair " +
            $"(e.g. http://redirect.example.test or 203.0.113.10:443), got '{got}'.",
    };

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// Request to create a listener at runtime. The transport names the
    /// <see cref="ListenerTransport"/> (case-insensitive); the bind address is
    /// the socket this server opens (host:port for every network transport, a
    /// bare pipe name for SMB); the public endpoint is the address implants
    /// dial. Runtime listeners are not persisted -- a restart rebinds the
    /// startup configuration.
    /// </summary>
    public sealed record CreateListenerRequest(
        string Name,
        string Transport,
        string BindAddress,
        string PublicEndpoint);

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
