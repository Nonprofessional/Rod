using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rod.Transport.Listeners.Dns;

// The DNS listener's UDP server (architecture.md Sec 8): one hosted service
// per DNS listener entry, bound on the entry's address, answering TXT
// contacts under the entry's public endpoint (the zone). The wire grammar
// lives in DnsContactNames and the contract doc; the tasking/presence
// composition lives in DnsBeaconBridge. Names in the zone that are not
// contacts are answered NXDOMAIN, the shape a resolver expects for an
// unknown name, so the zone does not advertise what it is.

/// <summary>
/// Serves one DNS listener entry's datagrams. Registered by
/// <c>UseRodListeners</c> for every dns entry
/// entry; UDP-bound, single receive loop, one task per datagram. The entry
/// becomes a registry listener once its socket is bound -- the same
/// bind-then-register shape the Kestrel path follows.
/// </summary>
internal sealed class DnsListenerService : BackgroundService
{
    private readonly Listener _listener;
    private readonly DnsContactAnswerer _answerer;
    private readonly IListenerRegistry _listeners;
    private readonly ILogger<DnsListenerService> _logger;

    public DnsListenerService(
        Listener listener,
        DnsBeaconBridge bridge,
        IListenerRegistry listeners,
        ILogger<DnsListenerService> logger)
    {
        _listener = listener;
        // The shared answer core: the same wire grammar the DoH route serves
        // over HTTP bodies, behind this service's UDP socket.
        _answerer = new DnsContactAnswerer(listener, bridge, logger);
        _listeners = listeners;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (host, port) = ParseBindAddress(_listener.BindAddress);
        using var udp = new UdpClient(new IPEndPoint(host, port));

        // Bind first, then register: the registry reflects what is actually
        // listening, the same ordering the Kestrel-bound transports follow.
        await _listeners.RegisterAsync(_listener, stoppingToken);

        _logger.LogInformation("Rod DNS listener {Name} answering TXT contacts for zone {Zone} on {Bind}.",
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

            // Answer on a task of its own so one slow contact (a task claim,
            // an audit append) never head-of-line blocks the receive loop.
            _ = AnswerAsync(udp, datagram, stoppingToken);
        }
    }

    private async Task AnswerAsync(UdpClient udp, UdpReceiveResult datagram, CancellationToken cancellationToken)
    {
        var response = await _answerer.AnswerAsync(datagram.Buffer, cancellationToken);
        try
        {
            await udp.SendAsync(response, response.Length, datagram.RemoteEndPoint);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // The client vanished or the listener is stopping; the datagram
            // is disposable -- the implant's next contact retries.
        }
    }

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
