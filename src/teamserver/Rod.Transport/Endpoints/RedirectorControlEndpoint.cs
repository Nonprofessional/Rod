using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rod.CoreState;
using Rod.CoreState.Listeners;
using Rod.CoreState.Redirectors;
using Rod.Transport.Listeners;
using Rod.Transport.Redirectors;

namespace Rod.Transport.Endpoints;

/// <summary>
/// The redirector control channel (architecture.md Sec 8): the deployment-
/// scoped WebSocket surface nodes dial home on. The channel is
/// redirector-initiated and outbound-only -- no management listener sits on
/// the edge box -- and it never rides the implant ingress: a request that
/// arrived on an engagement-scoped listener is refused outright, so the
/// engagement's sockets never name a control surface a target could probe.
/// The shared tier (the operator front, or a deliberate shared ingress)
/// carries it, which is the same trust tier the console rides.
///
/// Identity is application-layer like every surface here: the first message
/// enrolls against a pre-provisioned credential (single-use; the response
/// mints the node secret shown exactly once) or reconnects with the node
/// secret, and the open connection is the session. Heartbeats report build
/// provenance and keep the fleet record's stamps fresh; rule pushes arrive
/// out of band whenever a front mutation touches the node.
/// </summary>
internal static class RedirectorControlEndpoint
{
    // Control messages are fleet plumbing: a credential, a rule set. A frame
    // beyond this size is not a redirector.
    private const int MaxMessageBytes = 256 * 1024;

    public static void MapRedirectorControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/redirectors/control", HandleAsync);
    }

    private static async Task HandleAsync(
        HttpContext http,
        IListenerRegistry listeners,
        IRedirectorFleet fleet,
        CoreState.Listeners.IListenerStore definitions,
        Microsoft.Extensions.Options.IOptions<RedirectorControlOptions> controlOptions,
        RedirectorControlHub hub,
        CancellationToken cancellationToken)
    {
        // The implant ingress never carries the control channel (architecture.md
        // Sec 8): a socket resolving to an engagement-scoped listener refuses
        // outright. An unknown port is the test harness shape the public edge
        // tolerates.
        if (http.Connection.LocalPort is int port
            && await listeners.FindByLocalPortAsync(port, cancellationToken) is { EngagementId: not null })
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var remote = http.Connection.RemoteIpAddress?.ToString();
        // The upstream face rules name when no override is configured: the
        // local address this node dialed. A null (unknown-family socket)
        // falls back to loopback -- a shape only the test server produces.
        var localFace = http.Connection.LocalIpAddress?.ToString() ?? "127.0.0.1";

        RedirectorNodeId? node = null;
        try
        {
            // The first message establishes identity; everything after it is
            // session traffic.
            var first = await ReceiveAsync(socket, cancellationToken);
            if (first is null)
                return;

            RedirectorNodeId nodeId;
            switch (first.Type)
            {
                case RedirectorControlMessages.Enroll:
                    {
                        if (string.IsNullOrEmpty(first.Credential))
                        {
                            await SendAsync(socket, ControlMessages.Refused("unknown"), cancellationToken);
                            return;
                        }

                        try
                        {
                            var enrolled = await fleet.EnrollAsync(
                                first.Credential, remote, first.Version, first.Commit,
                                DateTimeOffset.UtcNow, cancellationToken);
                            nodeId = enrolled.Node.Id;
                            var enrollRules = await RedirectorRules.BuildAsync(
                                definitions, nodeId, controlOptions.Value.AdvertisedUpstreamHost ?? "", localFace,
                                cancellationToken);
                            await SendAsync(
                                socket,
                                ControlMessages.EnrolledResult(nodeId.ToString(), enrolled.NodeSecret, enrollRules),
                                cancellationToken);
                        }
                        catch (RedirectorEnrollException ex)
                        {
                            await SendAsync(socket, ControlMessages.Refused(ex.Reason.ToString().ToLowerInvariant()), cancellationToken);
                            return;
                        }

                        break;
                    }

                case RedirectorControlMessages.Hello:
                    {
                        if (!RedirectorNodeId.TryParse(first.NodeId, out nodeId)
                            || string.IsNullOrEmpty(first.NodeSecret))
                        {
                            await SendAsync(socket, ControlMessages.Refused("unknown"), cancellationToken);
                            return;
                        }

                        // Retirement is public to the holder (the roster shows
                        // it); a wrong secret is not distinguishable from an
                        // unknown id -- no existence oracle on the control
                        // channel.
                        var existing = await fleet.FindAsync(nodeId, cancellationToken);
                        if (existing?.RetiredAt is not null)
                        {
                            await SendAsync(socket, ControlMessages.Refused("retired"), cancellationToken);
                            return;
                        }

                        try
                        {
                            await fleet.AuthenticateAsync(nodeId, first.NodeSecret, DateTimeOffset.UtcNow, cancellationToken);
                        }
                        catch (RedirectorNodeRefusedException)
                        {
                            await SendAsync(socket, ControlMessages.Refused("unknown"), cancellationToken);
                            return;
                        }

                        var helloRules = await RedirectorRules.BuildAsync(
                            definitions, nodeId, controlOptions.Value.AdvertisedUpstreamHost ?? "", localFace,
                            cancellationToken);
                        await SendAsync(socket, ControlMessages.WelcomeResult(helloRules), cancellationToken);
                        break;
                    }

                default:
                    await SendAsync(socket, ControlMessages.Refused("unknown"), cancellationToken);
                    return;
            }

            node = nodeId;
            hub.Attach(nodeId, socket, localFace);

            // The steady state: heartbeats and acks report and keep the
            // record fresh. Anything unrecognized is ignored -- the grammar
            // grows only by addition, and an older node's unknown-newer
            // message must not kill its connection.
            while (await ReceiveAsync(socket, cancellationToken) is { } message)
            {
                switch (message.Type)
                {
                    case RedirectorControlMessages.Heartbeat:
                    case RedirectorControlMessages.Ack:
                        await fleet.TouchAsync(nodeId, remote, message.Version, message.Commit,
                            DateTimeOffset.UtcNow, cancellationToken);
                        break;
                }
            }
        }
        catch (WebSocketException)
        {
            // The node dropped without a close handshake; its reconnect is
            // the recovery.
        }
        finally
        {
            if (node is { } attached)
                hub.Detach(attached, socket);
        }
    }

    private static async Task<ControlMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;

            buffer.Write(chunk, 0, received.Count);
            if (buffer.Length > MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", cancellationToken);
                return null;
            }

            if (received.EndOfMessage)
                return JsonSerializer.Deserialize<ControlMessage>(buffer.ToArray(), WireJson.Options);
        }
    }

    private static Task SendAsync(WebSocket socket, ControlMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, WireJson.Options);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
}
