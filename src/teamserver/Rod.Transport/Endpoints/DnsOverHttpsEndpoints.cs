using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;

namespace Rod.Transport.Endpoints;

// DNS-over-HTTPS (architecture.md Sec 8, the DNS grammar's second
// carriage): the same TXT check-in wire grammar the UDP listener answers,
// carried as RFC 8484 DNS wire messages over HTTP bodies -- GET with the
// urlsafe-base64 `dns` parameter or POST with an application/dns-message
// body. A `doh` listener entry owns the route: the entry's public endpoint
// is the zone its queries are answered under, exactly the UDP listener's
// model, and the arrival port resolves which listener answers. The
// carriage changes; the answer never does (DnsCheckInAnswerer).

/// <summary>
/// Maps the DoH route. Mapped alongside the operator API and the implant
/// endpoints on every listener like them; a request answers only when a
/// doh listener owns the arriving port, and the identity posture is the
/// DNS grammar's own -- the implant is identified by its id alone, the
/// documented egress-restricted tradeoff.
/// </summary>
public static class DnsOverHttpsEndpoints
{
    /// <summary>The DoH route, the conventional RFC 8484 path.</summary>
    public const string Route = "/dns-query";

    /// <summary>The RFC 8484 media type.</summary>
    public const string MediaType = "application/dns-message"; public static IEndpointRouteBuilder MapDnsOverHttpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, async (
            HttpContext http,
            IListenerRegistry listeners,
            DnsBeaconBridge bridge,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
            =>
        {
            var logger = loggerFactory.CreateLogger("Rod.Transport.DnsOverHttps");
            // RFC 8484 GET: the urlsafe-base64 query under the `dns`
            // parameter.
            var encoded = http.Request.Query["dns"].ToString();
            if (encoded.Length == 0)
                return Results.BadRequest(new Problem("The dns parameter is required."));
            byte[] query;
            try
            {
                query = Convert.FromBase64String(PadBase64(encoded.Replace('-', '+').Replace('_', '/')));
            }
            catch (FormatException)
            {
                return Results.BadRequest(new Problem("The dns parameter is not base64."));
            }
            return await AnswerAsync(http, listeners, bridge, logger, query, cancellationToken);
        }).WithName("DnsOverHttpsGet");

        endpoints.MapPost(Route, async (
            HttpContext http,
            IListenerRegistry listeners,
            DnsBeaconBridge bridge,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
            =>
        {
            var logger = loggerFactory.CreateLogger("Rod.Transport.DnsOverHttps");
            // RFC 8484 POST: the wire message as the body, media type
            // advisory (resolvers vary; the bytes decide).
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, cancellationToken);
            return await AnswerAsync(http, listeners, bridge, logger, body.ToArray(), cancellationToken);
        }).WithName("DnsOverHttpsPost");
        return endpoints;
    }

    // Resolves the doh listener that owns the arriving port and answers
    // under its zone: the same port-resolution the enrollment ingress
    // applies, filtered to the DoH transports. No owner means the route is
    // not served on this socket -- an ordinary 404, not a DNS error, so a
    // resolver probing a non-DoH front learns nothing.
    private static async Task<IResult> AnswerAsync(
        HttpContext http,
        IListenerRegistry listeners,
        DnsBeaconBridge bridge,
        ILogger logger,
        byte[] query,
        CancellationToken cancellationToken)
    {
        var port = http.Connection.LocalPort;
        Listener? owner = null;
        foreach (var candidate in await listeners.ListAsync(cancellationToken))
        {
            if (candidate.Transport is not "doh")
                continue;
            if (TryParsePort(candidate.BindAddress) == port)
            {
                owner = candidate;
                break;
            }
        }
        if (owner is null)
            return Results.NotFound(new Problem("No DoH listener serves this socket."));

        var answerer = new DnsCheckInAnswerer(owner, bridge, logger);
        var response = await answerer.AnswerAsync(query, cancellationToken);
        return Results.Bytes(response, MediaType);
    }

    // urlsafe base64 may drop the padding the standard decoder wants.
    private static string PadBase64(string value)
        => value.Length % 4 == 0 ? value : value + new string('=', 4 - value.Length % 4);

    private static int TryParsePort(string bindAddress)
    {
        var colon = bindAddress.LastIndexOf(':');
        return colon >= 0 && int.TryParse(bindAddress[(colon + 1)..], out var port) ? port : 0;
    }

}
