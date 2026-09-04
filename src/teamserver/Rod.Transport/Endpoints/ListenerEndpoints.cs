using System.Net;
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
        if (!Guid.TryParse(engagementId, out var engagementValue))
            return Results.BadRequest(new Problem("Engagement id is not a valid identifier."));
        if (await engagements.FindAsync(new EngagementId(engagementValue), cancellationToken) is null)
            return Results.NotFound(new Problem("Engagement does not exist."));

        // The public endpoint is what a payload dials, so an incomplete value
        // must not ride into builds -- but incompleteness is cheap to fix
        // here. On the HTTP-shaped transports a blank endpoint means "implants
        // dial this bind itself" (the no-redirector shape) and derives from
        // the bind; a bare hostname takes the transport's scheme and the
        // listener's own bind port. Complete input (an absolute URL or a
        // host:port pair) is stored verbatim. The stream transports cannot
        // derive -- a DNS zone or pipe path is not a function of the bind --
        // so they require it spelled out.
        string publicEndpoint;
        if (transport is ListenerTransport.Http or ListenerTransport.Https
            or ListenerTransport.Mtls or ListenerTransport.HttpsEnvelope)
        {
            var derived = DeriveHttpPublicEndpoint(transport, body.BindAddress.Trim(), body.PublicEndpoint);
            if (derived is not null)
            {
                publicEndpoint = derived;
            }
            else if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
            {
                // The one blank the derivation refuses: a wildcard bind is
                // every interface, not an address anything can dial. Name
                // that instead of restating the endpoint rule the operator
                // just followed. (A malformed bind also lands here and falls
                // through, so the manager's bind validation names it.)
                if (IsWildcardBind(body.BindAddress.Trim()))
                {
                    return Results.BadRequest(new Problem(
                        "A wildcard bind (0.0.0.0 or ::) is every interface, not an address implants can "
                        + "dial, so the public endpoint cannot be left empty for it -- type the hostname "
                        + "implants should reach (e.g. c2.example.test)."));
                }
                publicEndpoint = "";
            }
            else if (IsPublicEndpoint(body.PublicEndpoint.Trim()))
            {
                // A complete endpoint with an underivable bind: the manager's
                // bind validation names the real problem (a 400 with its
                // message), so fall through instead of blaming the endpoint.
                publicEndpoint = body.PublicEndpoint.Trim();
            }
            else
            {
                return Results.BadRequest(new Problem(PublicEndpointRule(transport, body.PublicEndpoint ?? "")));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
                return Results.BadRequest(new Problem(
                    $"Public endpoint is required for the {transport.WireName()} transport: the zone or path implants dial cannot be derived from the bind."));
            if (!IsAcceptablePublicEndpoint(transport, body.PublicEndpoint))
                return Results.BadRequest(new Problem(PublicEndpointRule(transport, body.PublicEndpoint)));
            publicEndpoint = body.PublicEndpoint.Trim();
        }

        try
        {
            var listener = await manager.CreateAsync(
                new ListenerConfig(
                    body.Name.Trim(), transport, body.BindAddress.Trim(), publicEndpoint,
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

        var existing = await listeners.FindAsync(listenerId, cancellationToken);
        if (existing is null || existing.EngagementId != new EngagementId(engagementIdValue))
            return Results.NotFound(new Problem("Listener does not exist in this engagement."));
        if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
            return Results.BadRequest(new Problem("Public endpoint is required."));

        // A complete value is an absolute http(s) URL or a host:port pair. A
        // bare hostname is accepted and completed with the transport's scheme
        // and the listener's own bind port -- the same completion a create
        // applies -- so a repoint never demands more typing than a create.
        var endpoint = body.PublicEndpoint.Trim();
        if (!IsPublicEndpoint(endpoint))
        {
            var completed = CompleteBareHost(existing, endpoint);
            if (completed is null)
                return Results.BadRequest(new Problem(
                    "Public endpoint must be an absolute http(s) URL, a host:port pair, or a bare "
                    + $"hostname (completed with the listener's scheme and port), got '{body.PublicEndpoint}'."));
            endpoint = completed;
        }

        // Repoint swaps the public endpoint -- the redirector implants dial --
        // without touching the bound socket (architecture.md Sec 7/8). The
        // bind address stays put, so a live listener keeps serving; the registry's
        // public-endpoint lookup now resolves the new endpoint and no longer
        // resolves the old one (a burned redirector is severed).
        var listener = await listeners.RepointAsync(listenerId, endpoint, cancellationToken);
        if (listener is null)
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

    // The create-time completion for the HTTP-shaped transports: blank
    // derives the whole endpoint from the bind (implants dial this server
    // directly -- the no-redirector shape), a bare hostname takes the
    // transport's scheme and the listener's own bind port, and complete input
    // passes through verbatim. Returns null when nothing honest can be
    // derived (a wildcard bind has no dialable name) or the value is not a
    // dialable shape at all.
    private static string? DeriveHttpPublicEndpoint(
        ListenerTransport transport, string bindAddress, string? publicEndpoint)
    {
        (IPAddress Host, int Port) bind;
        try
        {
            bind = TransportHost.ParseBindAddress(bindAddress);
        }
        catch
        {
            // A malformed bind is the manager's refusal to name (or the
            // fall-through above passes a complete endpoint to it); nothing
            // to derive from here.
            return null;
        }

        var value = publicEndpoint?.Trim() ?? "";
        if (value.Length == 0)
        {
            if (IsWildcard(bind.Host))
                return null;
            return $"{SchemeOf(transport)}://{HostText(bind.Host)}:{bind.Port}";
        }

        if (IsPublicEndpoint(value))
            return value;

        if (IsBareHost(value))
            return $"{SchemeOf(transport)}://{value}:{bind.Port}";

        return null;
    }

    // The repoint-time completion: a bare hostname takes the listener's
    // transport scheme and its own bind port.
    private static string? CompleteBareHost(Listener listener, string endpoint)
        => IsBareHost(endpoint)
            ? $"{SchemeOf(listener.Transport)}://{endpoint}:{BindPortOf(listener.BindAddress)}"
            : null;

    private static string SchemeOf(ListenerTransport transport)
        => transport == ListenerTransport.Http ? "http" : "https";

    // A wildcard bind covers every interface, which is a legitimate way to
    // open the socket -- but it names no single address, so nothing can dial
    // it and no public endpoint derives from it.
    private static bool IsWildcard(IPAddress host)
        => host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any);

    private static bool IsWildcardBind(string bindAddress)
    {
        try
        {
            return IsWildcard(TransportHost.ParseBindAddress(bindAddress).Host);
        }
        catch
        {
            return false;
        }
    }

    // IPv6 authorities need their brackets; everything else stringifies as
    // dialable.
    private static string HostText(IPAddress host)
        => host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host.ToString();

    private static int BindPortOf(string bindAddress)
    {
        var colon = bindAddress.LastIndexOf(':');
        return int.TryParse(bindAddress[(colon + 1)..], out var port) ? port : 0;
    }

    // A hostname without a port: host characters and at least one letter --
    // the letter requirement keeps an all-digits typo (a port typed alone)
    // from reading as a dialable name.
    private static bool IsBareHost(string value)
        => value.Length > 0
            && value.Any(char.IsLetter)
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

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
        ListenerTransport.Tcp => $"Public endpoint must be the host:port implants dial (e.g. 203.0.113.10:443), got '{got}'.",
        _ => "Public endpoint accepts an absolute http(s) URL, a host:port pair, or a bare hostname "
            + "(completed with the transport's scheme and this listener's port); it may also be left "
            + $"empty, which dials the bind itself. got '{got}'.",
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
