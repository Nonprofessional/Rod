using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.Transport.Listeners;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator-facing listener query (roadmap M2.2): which listeners are bound and
/// serving, their transports, bind addresses, and -- crucially -- the public
/// endpoints implants dial (typically a redirector, decoupled from the bind address
/// per architecture.md Sec 8). Read-only; listeners are configured at startup.
/// </summary>
public static class ListenerEndpoints
{
    public static IEndpointRouteBuilder MapListenerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/listeners");

        group.MapGet("/", ListListenersAsync).WithName(nameof(ListListenersAsync));
        group.MapGet("/{id}", GetListenerAsync).WithName(nameof(GetListenerAsync));

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

    // --- DTOs. camelCase JSON is the framework default; records stay clean. ---

    public sealed record ListenerResponse(
        string Id,
        string Name,
        string Transport,
        string BindAddress,
        string PublicEndpoint,
        string State,
        DateTimeOffset CreatedAt);

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
                l.CreatedAt);
    }

    public sealed record Problem(string Error);
}
