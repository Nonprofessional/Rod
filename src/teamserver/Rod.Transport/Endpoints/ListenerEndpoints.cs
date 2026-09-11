using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Listeners;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Providers;

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
        var provider = TransportProviders.Find(body.Transport?.Trim());
        if (provider is null)
            return Results.BadRequest(new Problem(
                "Transport is not recognized. Use one of: " +
                string.Join(", ", TransportProviders.Names()) + "."));
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
        // listener's own bind port; a host:port pair is completed with the
        // transport's scheme; an absolute URL passes through verbatim. The
        // stored form is always complete, so the roster and every build read
        // one uniform shape. The stream transports cannot derive -- a DNS
        // zone or pipe path is not a function of the bind -- so they require
        // it spelled out.
        string publicEndpoint;
        if (provider is KestrelEndpointProvider)
        {
            var derived = DeriveHttpPublicEndpoint(provider, body.BindAddress.Trim(), body.PublicEndpoint);
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
            else if (PublicEndpointShapes.IsAbsoluteHttpUrl(body.PublicEndpoint.Trim()))
            {
                // A complete endpoint with an underivable bind: the manager's
                // bind validation names the real problem (a 400 with its
                // message), so fall through instead of blaming the endpoint.
                publicEndpoint = body.PublicEndpoint.Trim();
            }
            else if (PublicEndpointShapes.IsHostPort(body.PublicEndpoint.Trim()))
            {
                // The same completion the derivation applies, kept for the
                // underivable-bind fall-through so a host:port never rides
                // into builds scheme-less.
                publicEndpoint = $"{provider.PublicEndpointScheme}://{body.PublicEndpoint.Trim()}";
            }
            else
            {
                return Results.BadRequest(new Problem(
                    provider.DescribePublicEndpointRule(body.PublicEndpoint ?? "")));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(body.PublicEndpoint))
                return Results.BadRequest(new Problem(
                    $"Public endpoint is required for the {provider.Transport} transport: the zone or path implants dial cannot be derived from the bind."));
            if (!provider.AcceptsPublicEndpoint(body.PublicEndpoint))
                return Results.BadRequest(new Problem(provider.DescribePublicEndpointRule(body.PublicEndpoint)));
            publicEndpoint = body.PublicEndpoint.Trim();
        }

        try
        {
            var listener = await manager.CreateAsync(
                new ListenerConfig(
                    body.Name.Trim(), provider.Transport, body.BindAddress.Trim(), publicEndpoint,
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

    // Deleting a listener that live implants enrolled through cuts their
    // ingress: they keep dialing an endpoint nobody serves and go dark. The
    // guard counts those implants (retired ones are out of operation and do
    // not count) and refuses with the count unless the caller forces -- the
    // two-step delete the operator UI walks.
    private static async Task<IResult> DeleteListenerAsync(
        string engagementId,
        string id,
        bool? force,
        ListenerManager manager,
        IListenerRegistry listeners,
        IImplantRepository implants,
        CancellationToken cancellationToken)
    {
        var (error, engagementIdValue, listenerId) = Resolve(engagementId, id);
        if (error is not null)
            return error;

        var listener = await listeners.FindAsync(listenerId, cancellationToken);
        if (!Owns(listener, engagementIdValue))
            return Results.NotFound(new Problem("Listener does not exist in this engagement."));

        if (force is not true)
        {
            var enrolled = (await implants.ListByEngagementAsync(
                    new EngagementId(engagementIdValue), cancellationToken))
                .Where(i => i.EnrolledViaListenerId == listenerId.Value && !i.IsRetired)
                .ToArray();
            if (enrolled.Length > 0)
            {
                var names = string.Join(", ", enrolled
                    .Select(i => i.Hostname ?? i.Id.ToString())
                    .Take(3));
                return Results.Conflict(new Problem(
                    $"{enrolled.Length} live implant{(enrolled.Length == 1 ? "" : "s")} enrolled through listener " +
                    $"'{listener!.Name}' ({names}{(enrolled.Length > 3 ? ", …" : "")}) lose this ingress and go " +
                    "dark. Delete anyway with force=true."));
            }
        }

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

        // A stored endpoint is always complete: an absolute http(s) URL
        // passes through, a bare hostname takes the transport's scheme and
        // the listener's own bind port, and a host:port pair takes the
        // transport's scheme -- the same completion a create applies, so a
        // repoint never demands more typing than a create and the roster
        // keeps one uniform shape.
        var endpoint = body.PublicEndpoint.Trim();
        if (!PublicEndpointShapes.IsAbsoluteHttpUrl(endpoint))
        {
            var scheme = TransportProviders.Find(existing.Transport)?.PublicEndpointScheme ?? "https";
            var completed = PublicEndpointShapes.IsHostPort(endpoint)
                ? $"{scheme}://{endpoint}"
                : CompleteBareHost(existing, endpoint);
            if (completed is null)
                return Results.BadRequest(new Problem(
                    "Public endpoint must be an absolute http(s) URL, a host:port pair, or a bare "
                    + $"hostname -- each completed with the listener's scheme and port -- got '{body.PublicEndpoint}'."));
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
                listener.Transport,
                listener.BindAddress,
                listener.PublicEndpoint,
                listener.CreatedAt,
                listener.RepointedAt),
            cancellationToken);

        return Results.Ok(Response.Of(listener));
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

    // The create-time completion for the HTTP-shaped transports: blank
    // derives the whole endpoint from the bind (implants dial this server
    // directly -- the no-redirector shape), a bare hostname takes the
    // transport's scheme and the listener's own bind port, a host:port pair
    // takes the transport's scheme, and an absolute URL passes through
    // verbatim. Returns null when nothing honest can be derived (a wildcard
    // bind has no dialable name) or the value is not a dialable shape at all.
    private static string? DeriveHttpPublicEndpoint(
        ITransportProvider provider, string bindAddress, string? publicEndpoint)
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
            return $"{provider.PublicEndpointScheme}://{HostText(bind.Host)}:{bind.Port}";
        }

        if (PublicEndpointShapes.IsAbsoluteHttpUrl(value))
            return value;

        if (PublicEndpointShapes.IsHostPort(value))
            return $"{provider.PublicEndpointScheme}://{value}";

        if (PublicEndpointShapes.IsBareHost(value))
            return $"{provider.PublicEndpointScheme}://{value}:{bind.Port}";

        return null;
    }

    // The repoint-time completion: a bare hostname takes the listener's
    // transport scheme and its own bind port.
    private static string? CompleteBareHost(Listener listener, string endpoint)
    {
        var scheme = TransportProviders.Find(listener.Transport)?.PublicEndpointScheme ?? "https";
        return PublicEndpointShapes.IsBareHost(endpoint)
            ? $"{scheme}://{endpoint}:{BindPortOf(listener.BindAddress)}"
            : null;
    }

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

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    /// <summary>
    /// Request to create one of this engagement's listeners. The transport
    /// names a registered provider's wire name (case-insensitive); the bind
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
                // The transport's wire name (the registry key a provider
                // registered under), which the listing and the operator UI
                // render verbatim.
                l.Transport,
                l.BindAddress,
                l.PublicEndpoint,
                l.State.ToString().ToLowerInvariant(),
                l.CreatedAt,
                l.RepointedAt);
    }

    public sealed record Problem(string Error);
}
