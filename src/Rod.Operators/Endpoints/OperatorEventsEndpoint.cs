using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Live;
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
/// Identity comes from query parameters in this milestone (operatorId, handle,
/// displayName); real operator authentication arrives later and replaces only
/// how the identity is established.
/// </summary>
public static class OperatorEventsEndpoint
{
    public static IEndpointRouteBuilder MapOperatorEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/engagements/{engagementId}/events", StreamAsync)
            .WithName("StreamOperatorEvents");

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
        var identity = ReadOperatorIdentity(context.Request.Query);
        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new Problem("operatorId and handle query parameters are required."), cancellationToken);
            return;
        }

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

    // Reads the operator identity off the query string. The walking skeleton
    // resolves it from the request; real operator auth arrives later and
    // replaces only this read.
    private static OperatorPresenceService.OperatorSnapshot? ReadOperatorIdentity(IQueryCollection query)
    {
        if (!Guid.TryParse(query["operatorId"], out var idValue))
            return null;
        var handle = query["handle"].ToString();
        if (string.IsNullOrWhiteSpace(handle))
            return null;
        var displayName = query["displayName"].ToString();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = handle;

        return new OperatorPresenceService.OperatorSnapshot(new OperatorId(idValue), handle, displayName);
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
