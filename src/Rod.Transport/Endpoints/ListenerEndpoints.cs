using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing listener endpoints (roadmap M2.2 read view, M4.4
/// repoint): which listeners are bound and serving, their transports, bind
/// addresses, and -- crucially -- the public endpoints implants dial (typically
/// a redirector, decoupled from the bind address per architecture.md Sec 8).
/// Listeners are bound at startup; at runtime an operator can repoint a
/// listener's public endpoint to swap a burned redirector without touching the
/// backend (architecture.md Sec 7/8, M4.4).
/// </summary>
public static class ListenerEndpoints
{
    public static IEndpointRouteBuilder MapListenerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/listeners");

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

        // Repoint swaps the public endpoint -- the redirector implants dial --
        // without touching the bound socket (architecture.md Sec 7/8, M4.4). The
        // bind address stays put, so a live listener keeps serving; the registry's
        // public-endpoint lookup now resolves the new endpoint and no longer
        // resolves the old one (a burned redirector is severed).
        var listener = await listeners.RepointAsync(listenerId, body.PublicEndpoint, cancellationToken);
        if (listener is null)
            return Results.NotFound(new Problem("Listener is not registered."));

        return Results.Ok(Response.Of(listener));
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
                l.Transport.ToString().ToLowerInvariant(),
                l.BindAddress,
                l.PublicEndpoint,
                l.State.ToString().ToLowerInvariant(),
                l.CreatedAt,
                l.RepointedAt);
    }

    public sealed record Problem(string Error);
}
