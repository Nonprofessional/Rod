using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rod.Transport.Listeners.Dns;

// The DNS listener's UDP server (architecture.md Sec 8): one hosted service
// per DNS listener entry, bound on the entry's address, answering TXT
// check-ins under the entry's public endpoint (the zone). The wire grammar
// lives in DnsCheckInNames and the contract doc; the tasking/presence
// composition lives in DnsBeaconBridge. Names in the zone that are not
// check-ins are answered NXDOMAIN, the shape a resolver expects for an
// unknown name, so the zone does not advertise what it is.

/// <summary>
/// Serves one DNS listener entry's datagrams. Registered by
/// <c>UseRodListeners</c> for every <see cref="ListenerTransport.Dns"/>
/// entry; UDP-bound, single receive loop, one task per datagram. The entry
/// becomes a registry listener once its socket is bound -- the same
/// bind-then-register shape the Kestrel path follows.
/// </summary>
internal sealed class DnsListenerService : BackgroundService
{
    private readonly ListenerConfig _listener;
    private readonly DnsBeaconBridge _bridge;
    private readonly IListenerRegistry _listeners;
    private readonly TimeProvider _clock;
    private readonly ILogger<DnsListenerService> _logger;

    public DnsListenerService(
        ListenerConfig listener,
        DnsBeaconBridge bridge,
        IListenerRegistry listeners,
        TimeProvider clock,
        ILogger<DnsListenerService> logger)
    {
        _listener = listener;
        _bridge = bridge;
        _listeners = listeners;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (host, port) = ParseBindAddress(_listener.BindAddress);
        using var udp = new UdpClient(new IPEndPoint(host, port));

        // Bind first, then register: the registry reflects what is actually
        // listening, the same ordering the Kestrel-bound transports follow.
        await _listeners.RegisterAsync(
            Listener.Define(
                ListenerId.New(), _listener.Name, _listener.Transport,
                _listener.BindAddress, _listener.PublicEndpoint, _clock.GetUtcNow()),
            stoppingToken);

        _logger.LogInformation("Rod DNS listener {Name} answering TXT check-ins for zone {Zone} on {Bind}.",
            _listener.Name, _listener.PublicEndpoint, _listener.BindAddress);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue; // a malformed send or a transient socket error: next datagram
            }

            // Answer on a task of its own so one slow check-in (a task claim,
            // an audit append) never head-of-line blocks the receive loop.
            _ = AnswerAsync(udp, datagram, stoppingToken);
        }
    }

    private async Task AnswerAsync(UdpClient udp, UdpReceiveResult datagram, CancellationToken cancellationToken)
    {
        var response = Answer(datagram.Buffer, out var questionName);
        try
        {
            await udp.SendAsync(response, response.Length, datagram.RemoteEndPoint);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // The client vanished or the listener is stopping; the datagram is
            // disposable -- the implant's next check-in retries.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS listener {Name} failed answering {Question}.", _listener.Name, questionName);
        }
    }

    /// <summary>
    /// Builds the response datagram for one query: a poll or result chunk
    /// under the zone gets the check-in treatment; anything else in the zone
    /// is NXDOMAIN; a query for another zone entirely is REFUSED (rcode 5) --
    /// this listener is not an open resolver.
    /// </summary>
    private byte[] Answer(byte[] query, out string questionName)
    {
        questionName = "";
        var parsed = DnsCodec.ParseQuery(query);
        if (parsed?.Question is not { } question)
            return EmptyResponse(parsed?.Id ?? 0, responseCode: 1); // FORMERR

        questionName = question.Name;
        var zone = _listener.PublicEndpoint.TrimEnd('.').ToLowerInvariant();
        var name = question.Name.ToLowerInvariant();

        // Only TXT check-ins under our zone; no recursion, no other records.
        if (!name.EndsWith(zone, StringComparison.Ordinal))
            return EmptyResponse(parsed.Id, responseCode: 5); // REFUSED: not our zone

        if (question.Type != DnsCodec.TxtType)
            return EmptyResponse(parsed.Id, responseCode: 3); // NXDOMAIN: TXT only

        var response = new DnsMessage
        {
            Id = parsed.Id,
            IsResponse = true,
            Question = question,
            ResponseCode = 0,
        };

        try
        {
            if (DnsCheckInNames.TryParsePoll(name, zone) is { } poll)
            {
                var marshaled = _bridge.PollAsync(poll.Implant, CancellationToken.None).GetAwaiter().GetResult();
                if (marshaled is not null)
                    response.Answers.Add(TxtAnswer(name, DnsCheckInNames.Encode(marshaled)));
            }
            else if (DnsCheckInNames.TryParseResult(name, zone) is { } chunk)
            {
                _bridge.ResultChunkAsync(
                        chunk.Implant, chunk.Task, chunk.Outcome, chunk.Sequence, chunk.Terminal, chunk.Chunk,
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            else
            {
                response.ResponseCode = 3; // NXDOMAIN: in-zone but not a check-in
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS listener {Name} failed a check-in for {Question}.", _listener.Name, questionName);
            return EmptyResponse(parsed.Id, responseCode: 2); // SERVFAIL
        }

        return DnsCodec.EncodeResponse(response);
    }

    private static DnsTxtAnswer TxtAnswer(string name, string encoded)
    {
        // Split the base32 payload into TXT strings of at most 200 chars: the
        // record's strings concatenate back into one payload on the implant
        // side, and the split keeps any single string well under the 255-byte
        // TXT limit with the EDNS0 budget in mind.
        var strings = new List<string>();
        for (var offset = 0; offset < encoded.Length; offset += 200)
            strings.Add(encoded.Substring(offset, Math.Min(200, encoded.Length - offset)));
        return new DnsTxtAnswer(name, strings);
    }

    private static byte[] EmptyResponse(ushort id, ushort responseCode)
        => DnsCodec.EncodeResponse(new DnsMessage { Id = id, IsResponse = true, ResponseCode = responseCode });

    // Parses "host:port" for the UDP bind; accepts an IP (v4/v6) or "*" for
    // any interface -- the same shapes the Kestrel listener path accepts.
    private static (IPAddress Host, int Port) ParseBindAddress(string bindAddress)
    {
        var span = bindAddress.AsSpan();
        IPAddress host;
        int port;

        if (span.Length > 0 && span[0] == '[')
        {
            var end = span.IndexOf(']');
            if (end < 0 || end + 2 > span.Length || span[end + 1] != ':')
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' is not a valid '[host]:port'.");
            if (!IPAddress.TryParse(span[1..end], out var parsedV6))
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' has an unparseable host.");
            host = parsedV6;
            if (!int.TryParse(span[(end + 2)..], out port))
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' has an unparseable port.");
        }
        else
        {
            var colon = span.LastIndexOf(':');
            if (colon < 0)
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' is not a valid 'host:port'.");
            var hostPart = span[..colon];
            if (hostPart.SequenceEqual("*".AsSpan()) || hostPart.SequenceEqual("+".AsSpan()))
                host = IPAddress.Any;
            else if (!IPAddress.TryParse(hostPart, out var parsed))
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' has an unparseable host.");
            else
                host = parsed;
            if (!int.TryParse(span[(colon + 1)..], out port))
                throw new InvalidOperationException($"DNS bind address '{bindAddress}' has an unparseable port.");
        }

        if (port < 1 || port > 65535)
            throw new InvalidOperationException($"DNS bind address '{bindAddress}' has an out-of-range port.");
        return (host, port);
    }
}
