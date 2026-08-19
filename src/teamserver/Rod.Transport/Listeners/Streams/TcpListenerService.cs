using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Implants;

namespace Rod.Transport.Listeners.Streams;

// The raw-TCP listener service (architecture.md Sec 8): the socket half of
// the stream check-in transports, for segment networks that allow arbitrary
// sockets but no HTTP shape. The entry binds its TCP endpoint (the bind
// address), registers itself into the listener registry the same
// bind-then-register way every transport follows, and accepts connections in
// a loop: one connection is one check-in through the shared StreamBeaconBridge,
// then closed -- the same one-connection-one-check-in cadence the named pipe
// serves, over the transport a locked-down segment still permits.

/// <summary>
/// Binds the entry's TCP endpoint and serves check-ins until the host stops.
/// </summary>
internal sealed class TcpListenerService : BackgroundService
{
    private readonly ListenerConfig _listener;
    private readonly StreamBeaconBridge _bridge;
    private readonly IListenerRegistry _listeners;
    private readonly TimeProvider _clock;
    private readonly ILogger<TcpListenerService> _logger;

    public TcpListenerService(
        ListenerConfig listener,
        StreamBeaconBridge bridge,
        IListenerRegistry listeners,
        TimeProvider clock,
        ILogger<TcpListenerService> logger)
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
        var listener = new TcpListener(host, port);
        listener.Start();

        // Bind first, then register: the registry reflects what is actually
        // listening, the same ordering the Kestrel-bound transports follow.
        await _listeners.RegisterAsync(
            Listener.Define(
                ListenerId.New(), _listener.Name, _listener.Transport,
                _listener.BindAddress, _listener.PublicEndpoint, _clock.GetUtcNow()),
            stoppingToken);

        _logger.LogInformation(
            "Rod TCP listener {Name} answering socket check-ins on {Bind} for {Endpoint}.",
            _listener.Name, _listener.BindAddress, _listener.PublicEndpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue; // transient; the next accept retries
                }

                // One connection is one check-in; serve it off the accept
                // loop so concurrent polls overlap.
                _ = Task.Run(async () =>
                {
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    try
                    {
                        await _bridge.HandleCheckInAsync(stream, stoppingToken);
                    }
                    finally
                    {
                        stream.Dispose();
                    }
                }, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    // Parses "host:port" for the TCP bind; accepts an IP (v4/v6) or "*" for
    // any interface -- the same shapes the Kestrel listener path accepts, and
    // a local duplicate of that parse because it is transport plumbing, not
    // policy (the DNS service keeps its own copy for the same reason).
    private static (IPAddress Host, int Port) ParseBindAddress(string bindAddress)
    {
        var span = bindAddress.AsSpan();
        IPAddress host;
        int port;

        if (span.Length > 0 && span[0] == '[')
        {
            var end = span.IndexOf(']');
            if (end < 0 || end + 2 > span.Length || span[end + 1] != ':')
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' is not a valid '[host]:port'.");
            if (!IPAddress.TryParse(span[1..end], out var parsedV6))
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an unparseable host.");
            host = parsedV6;
            if (!int.TryParse(span[(end + 2)..], out port))
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an unparseable port.");
        }
        else
        {
            var colon = span.LastIndexOf(':');
            if (colon < 0)
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' is not a valid 'host:port'.");
            var hostPart = span[..colon];
            if (hostPart.SequenceEqual("*".AsSpan()) || hostPart.SequenceEqual("+".AsSpan()))
                host = IPAddress.Any;
            else if (!IPAddress.TryParse(hostPart, out var parsed))
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an unparseable host.");
            else
                host = parsed;
            if (!int.TryParse(span[(colon + 1)..], out port))
                throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an unparseable port.");
        }

        if (port < 1 || port > 65535)
            throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an out-of-range port.");

        return (host, port);
    }
}
