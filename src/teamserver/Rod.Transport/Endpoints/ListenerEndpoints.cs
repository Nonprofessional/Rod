using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Listeners;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The engagement's listener endpoints: the C2 ingress this one engagement
/// owns. Every listener is engagement-scoped -- created here against the
/// engagement in the path, persisted so a restart rebinds it, and enforced at
/// enrollment (a token minted for any other engagement is refused whole on its
/// socket). The bind address is decoupled from the public endpoint implants
/// dial (typically a redirector, architecture.md Sec 8), and a repoint swaps a
/// burned front at runtime without touching the backend. The operator front
/// the UI and API ride is startup configuration, not a listener in this sense:
/// it carries no implant ingress and never appears here.
/// </summary>
public static class ListenerEndpoints
{
    public static IEndpointRouteBuilder MapListenerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Operator-facing: listener views and actions require an authenticated
        // operator session.
        var group = endpoints.MapGroup("/engagements/{engagementId}/listeners").RequireAuthorization();

        group.MapGet("/", ListListenersAsync).WithName(nameof(ListListenersAsync));
        group.MapGet("/{id}", GetListenerAsync).WithName(nameof(GetListenerAsync));
        group.MapPost("/", CreateListenerAsync).WithName(nameof(CreateListenerAsync));
        group.MapPost("/{id}:repoint", RepointAsync).WithName(nameof(RepointAsync));
        group.MapDelete("/{id}", DeleteListenerAsync).WithName(nameof(DeleteListenerAsync));

        return endpoints;
    }

    private static async Task<IResult> CreateListenerAsync(
        string engagementId,
        CreateListenerRequest body,
        ListenerManager manager,
        IEngagementRepository engagements,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new Problem("Listener name is required."));
        if (!TryParseTransport(body.Transport, out var transport))
            return Results.BadRequest(new Problem(
                "Transport is not recognized. Use one of: " +
                string.Join(", ", Enum.GetValues<ListenerTransport>().Select(t => t.WireName())) + "."));
        if (string.IsNullOrWhiteSpace(body.BindAddress))
            return Results.BadRequest(new Problem("Bind address is required."));
        if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
            return Results.BadRequest(new Problem("Public endpoint is required."));
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken) is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

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
                new ListenerConfig(
                    body.Name.Trim(), transport, body.BindAddress.Trim(), body.PublicEndpoint.Trim(),
                    new EngagementId(engagementValue)),
                cancellationToken);

            return Results.Created($"/engagements/{engagementId}/listeners/{listener.Id}", Response.Of(listener));
        }
        catch (ArgumentException ex)
        {
            // A malformed bind address or an unknown engagement: an operator
            // mistake.
            return Results.BadRequest(new Problem(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // The bind was refused (address in use, socket never opened).
            return Results.Conflict(new Problem(ex.Message));
        }
    }

    private static async Task<IResult> DeleteListenerAsync(
        string engagementId,
        string id,
        ListenerManager manager,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        var (error, engagementIdValue, listenerId) = Resolve(engagementId, id);
        if (error is not null)
            return error;

        var listener = await listeners.FindAsync(listenerId, cancellationToken);
        if (!Owns(listener, engagementIdValue))
            return Results.NotFound(new Problem("Listener does not exist in this engagement."));

        await manager.RemoveAsync(listenerId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListListenersAsync(
        string engagementId,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));

        var owned = (await listeners.ListAsync(cancellationToken))
            .Where(l => l.EngagementId == new EngagementId(engagementValue))
            .Select(Response.Of)
            .ToArray();
        return Results.Ok(owned);
    }

    private static async Task<IResult> GetListenerAsync(
        string engagementId,
        string id,
        IListenerRegistry listeners,
        CancellationToken cancellationToken)
    {
        var (error, engagementIdValue, listenerId) = Resolve(engagementId, id);
        if (error is not null)
            return error;

        var listener = await listeners.FindAsync(listenerId, cancellationToken);
        if (listener is null || listener.EngagementId != new EngagementId(engagementIdValue))
            return Results.NotFound(new Problem("Listener does not exist in this engagement."));

        return Results.Ok(Response.Of(listener));
    }

    private static async Task<IResult> RepointAsync(
        string engagementId,
        string id,
        RepointListenerRequest body,
        IListenerRegistry listeners,
        IListenerStore definitions,
        CancellationToken cancellationToken)
    {
        var (error, engagementIdValue, listenerId) = Resolve(engagementId, id);
        if (error is not null)
            return error;
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
        if (!Owns(listener, engagementIdValue))
            return Results.NotFound(new Problem("Listener does not exist in this engagement."));

        // The definition follows the repoint, so the restore pass after a
        // restart rebinds the current front, not the burned one.
        await definitions.SaveAsync(
            new ListenerDefinition(
                listener!.Id.Value,
                listener.EngagementId!.Value,
                listener.Name,
                listener.Transport.WireName(),
                listener.BindAddress,
                listener.PublicEndpoint,
                listener.CreatedAt,
                listener.RepointedAt),
            cancellationToken);

        return Results.Ok(Response.Of(listener));
    }

    // The transport parses from the wire's kebab name ("https-envelope") or
    // the enum name, case-insensitively -- the listing renders kebab, so the
    // create form speaks the same shape it reads back.
    private static bool TryParseTransport(string? text, out ListenerTransport transport)
    {
        if (text is not null
            && Enum.TryParse<ListenerTransport>(text.Replace("-", ""), ignoreCase: true, out transport))
        {
            return true;
        }
        transport = default;
        return false;
    }

    // Shared id resolution for the scoped routes: both ids parse or the
    // request is a 400 before anything is touched.
    private static (IResult? Error, Guid EngagementId, ListenerId ListenerIdValue) Resolve(
        string engagementId, string id)
    {
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return (Results.BadRequest(new Problem("Engagement id is not a valid identifier.")), Guid.Empty, default);
        if (!ListenerId.TryParse(id, out var listenerId))
            return (Results.BadRequest(new Problem("Listener id is not a valid identifier.")), Guid.Empty, default);
        return (null, engagementValue, listenerId);
    }

    // A listener answers for the engagement when it is scoped to it; anything
    // else (another engagement's, or the startup-configuration tier) reads as
    // not-existing from this engagement's routes.
    private static bool Owns(Listener? listener, Guid engagementId)
        => listener?.EngagementId == new EngagementId(engagementId);

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
    /// Request to create one of this engagement's listeners. The transport
    /// names the <see cref="ListenerTransport"/> (case-insensitive); the bind
    /// address is the socket this server opens (host:port for every network
    /// transport, a bare pipe name for SMB); the public endpoint is the
    /// address implants dial. The engagement comes from the route, never the
    /// body. The definition is persisted, so a restart rebinds it.
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
