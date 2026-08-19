using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Implants;

namespace Rod.Transport.Listeners.Streams;

// The SMB listener service (architecture.md Sec 8): the named-pipe half of
// the stream check-in transports. Named-pipe check-ins serve Windows segments
// where neither HTTP nor DNS egress exists -- the pipe is the shape such a
// segment still allows. Each listener entry binds its pipe (the bind address
// is the bare pipe name), registers itself into the listener registry the
// same bind-then-register way every transport follows, and then accepts
// connections in a loop: one connection is one check-in, served through the
// shared StreamBeaconBridge and closed. The next server instance is already
// waiting while the current one is served, so concurrent check-ins from
// several implants overlap instead of queueing.

/// <summary>
/// Binds the entry's named pipe and serves check-ins until the host stops.
/// </summary>
internal sealed class SmbListenerService : BackgroundService
{
    private readonly ListenerConfig _listener;
    private readonly StreamBeaconBridge _bridge;
    private readonly IListenerRegistry _listeners;
    private readonly TimeProvider _clock;
    private readonly ILogger<SmbListenerService> _logger;

    public SmbListenerService(
        ListenerConfig listener,
        StreamBeaconBridge bridge,
        IListenerRegistry listeners,
        TimeProvider clock,
        ILogger<SmbListenerService> logger)
    {
        _listener = listener;
        _bridge = bridge;
        _listeners = listeners;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = _listener.BindAddress.Trim();

        // Bind first, then register: the registry reflects what is actually
        // listening, the same ordering the Kestrel-bound transports follow.
        await _listeners.RegisterAsync(
            Listener.Define(
                ListenerId.New(), _listener.Name, _listener.Transport,
                _listener.BindAddress, _listener.PublicEndpoint, _clock.GetUtcNow()),
            stoppingToken);

        _logger.LogInformation(
            "Rod SMB listener {Name} answering pipe check-ins on {Pipe} for {Endpoint}.",
            _listener.Name, pipeName, _listener.PublicEndpoint);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // The pipe namespace hiccuped (a peer reset mid-negotiation on
                // some platforms); a fresh instance retries.
                continue;
            }

            // Serve this connection off the accept loop and let the next
            // instance wait: check-ins are short, but several implants may
            // poll at once.
            var connection = server;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bridge.HandleCheckInAsync(connection, stoppingToken);
                }
                finally
                {
                    connection.Dispose();
                }
            }, stoppingToken);
        }
    }
}
