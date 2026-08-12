using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
using Rod.CoreState.Operators;
using Rod.Operators.Presence;

namespace Rod.Operators.Endpoints;

/// <summary>
/// The operator-facing live event stream (roadmap M2.4, architecture.md Sec 4.1
/// layer 4): a Server-Sent Events endpoint that keeps an operator session open
/// per engagement and pushes every live event on it -- operator joined/left,
/// task issued, task completed. This is the wire path for "two operators see
/// each other's actions live": each connected browser opens one stream and
/// receives its engagement's events as they are published.
///
/// On connect the operator is joined (publishing <see cref="LiveEventKind.OperatorJoined"/>
/// to peers) and the current presence roster is sent as the first frame, so a
/// late joiner sees who is already online before any events arrive. On
/// disconnect (client cancellation) the operator is left.
///
/// Identity is derived from the authenticated operator principal (cookie
/// session): the route carries <see cref="OperatorClaims.RequireAuthorization"/>,
/// and the id/handle/display name are read off the principal's claims rather
/// than the request, so a live stream is only ever opened for a logged-in
/// operator. EventSource carries the auth cookie (SameSite=Lax) with the
/// connection.
/// </summary>
public static class OperatorEventsEndpoint
{
    public static IEndpointRouteBuilder MapOperatorEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/events", StreamAsync)
            .WithName("StreamOperatorEvents")
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task StreamAsync(
        string engagementId,
        HttpContext context,
        ILiveEventBus bus,
        OperatorPresenceService presence,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(engagementId, out var idValue))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new Problem("Engagement id is not a valid identifier."), cancellationToken);
            return;
        }

        var engagement = new EngagementId(idValue);
        var identityClaim = context.User.TryGetOperatorIdentity();
        if (identityClaim is null)
        {
            // RequireAuthorization rejects anonymous requests before the handler
            // runs; this is the defense-in-depth fallback for a principal that
            // authenticated but lacks operator claims.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new Problem("An authenticated operator session is required."), cancellationToken);
            return;
        }

        var identity = new OperatorPresenceService.OperatorSnapshot(
            identityClaim.Value.Id, identityClaim.Value.Handle, identityClaim.Value.DisplayName);

        // SSE framing: chunked text/event-stream, never cached (the stream is
        // live and per-connection).
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering (nginx)

        // Join before opening the stream so peers see the join, and seed the
        // late joiner with the current roster as the first event.
        await presence.JoinAsync(engagement, identity, cancellationToken);
        try
        {
            await WriteEventAsync(context.Response, "hello", new
            {
                operators = (await presence.ListAsync(engagement, cancellationToken))
                    .Select(o => new { id = o.Id.ToString(), handle = o.Handle, displayName = o.DisplayName })
                    .ToArray(),
            }, cancellationToken);

            // The bus yields until the client disconnects (cancellation). Each
            // published event on this engagement is framed and flushed.
            await foreach (var @event in bus.SubscribeAsync(engagement, cancellationToken))
            {
                await WriteEventAsync(context.Response, @event.Kind.ToString(), new
                {
                    kind = @event.Kind.ToString(),
                    engagementId = @event.EngagementId.ToString(),
                    operatorId = @event.OperatorId.ToString(),
                    implantId = @event.ImplantId?.ToString(),
                    taskId = @event.TaskId?.ToString(),
                    payload = @event.Payload,
                    at = @event.At,
                }, cancellationToken);
            }
        }
        finally
        {
            await presence.LeaveAsync(engagement, identity.Id, CancellationToken.None);
        }
    }

    // Writes one SSE frame: an event name line, a data line carrying a JSON
    // payload, and a blank line to terminate. Flushed so the client sees it
    // immediately rather than on the next buffer boundary.
    private static async Task WriteEventAsync(
        HttpResponse response, string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        await response.WriteAsync(frame, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public sealed record Problem(string Error);
}
