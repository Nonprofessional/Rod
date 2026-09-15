using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Live;
using Rod.CoreState.ShellSessions;
using Rod.Transport.Listeners;

namespace Rod.Transport.Listeners.ShellCatch;

// The shellcatch listener service (architecture.md Sec 8): the catcher half
// of the stream family. Where the TCP listener serves check-ins -- one
// connection, one rod.v1 exchange, closed -- this listener accepts
// connections that speak no protocol at all and holds them: the peer is
// whatever one-liner the operator ran on the target (nc, a bash /dev/tcp
// pipe, a perl or python snippet), it sends a shell's output and reads its
// input for as long as the shell lives. One accepted connection is one
// shell session, served off the accept loop so concurrent shells overlap.

/// <summary>
/// Binds the entry's TCP endpoint and holds caught reverse shells until the
/// host stops.
/// </summary>
internal sealed class ShellCatchListenerService : BackgroundService
{
    // How long a silent connection gets before one newline nudge -- a shell
    // that has printed nothing may simply be waiting for input, and a bare
    // newline is the least thing that provokes a prompt without running
    // anything.
    private static readonly TimeSpan NudgeDelay = TimeSpan.FromSeconds(2);

    // How many output chunks the fingerprinter may consider before giving up
    // and leaving the guess Unknown. A shell that has said three chunks'
    // worth without matching a rule is not going to match the fourth.
    private const int FingerprintChunkBudget = 3;

    private readonly Listener _listener;
    private readonly ShellCatchHub _hub;
    private readonly IShellSessionRegistry _sessions;
    private readonly ILiveEventBus _live;
    private readonly IAuditStore _audit;
    private readonly IListenerRegistry _listeners;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShellCatchListenerService> _logger;

    public ShellCatchListenerService(
        Listener listener,
        ShellCatchHub hub,
        IShellSessionRegistry sessions,
        ILiveEventBus live,
        IAuditStore audit,
        IListenerRegistry listeners,
        TimeProvider clock,
        ILogger<ShellCatchListenerService> logger)
    {
        _listener = listener;
        _hub = hub;
        _sessions = sessions;
        _live = live;
        _audit = audit;
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
        // listening, the same ordering every transport follows.
        await _listeners.RegisterAsync(_listener, stoppingToken);

        _logger.LogInformation(
            "Rod shellcatch listener {Name} catching reverse shells on {Bind} for {Endpoint}.",
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

                // One connection is one shell; serve it off the accept loop
                // so concurrent shells overlap.
                _ = Task.Run(() => ServeAsync(socket, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken stoppingToken)
    {
        // The catch is scoped the only way an anonymous arrival can be: by
        // the engagement-bound listener it landed on (architecture.md Sec 3,
        // Sec 8). A listener without an engagement never runs this service,
        // but the guard keeps that invariant here rather than trusting the
        // create path forever.
        if (_listener.EngagementId is not { } engagement)
        {
            _logger.LogWarning(
                "Shellcatch listener {Name} has no engagement; closing an unscoped connection.",
                _listener.Name);
            socket.Dispose();
            return;
        }

        var remote = socket.RemoteEndPoint is IPEndPoint endpoint
            ? $"{endpoint.Address}:{endpoint.Port}"
            : "unknown";
        var session = await _sessions.OpenAsync(
            engagement, _listener.Id.Value, remote, _clock.GetUtcNow());
        var shell = new CaughtShell(session, socket);
        _hub.Add(shell);

        await RecordOpenedAsync(shell, engagement);

        // A silent shell gets one newline nudge after the grace window;
        // after that the fingerprinter waits for whatever the operator's
        // own first command provokes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NudgeDelay, stoppingToken);
                if (shell.Output.LatestSequence == 0)
                    await shell.WriteInputAsync(string.Empty, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // The host stopped or the shell ended first; nothing to nudge.
            }
        }, stoppingToken);

        try
        {
            await PumpAsync(shell, engagement, stoppingToken);
        }
        finally
        {
            _hub.Remove(session.Id);
            socket.Dispose();
        }
    }

    // The read half of the shell: everything the peer sends lands on the
    // output log and the session's output stamp until the peer goes away.
    // Input arrives from the operator routes through the hub; nothing here
    // writes. The ending is decided by whether an operator asked for the
    // close before the socket died -- every other ending (peer exit, reset,
    // host stop) is a lost shell from the roster's point of view.
    private async Task PumpAsync(CaughtShell shell, EngagementId engagement, CancellationToken stoppingToken)
    {
        var buffer = new byte[8192];
        var fingerprintChunks = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var count = await shell.ReceiveAsync(buffer, stoppingToken);
                if (count == 0)
                    break; // the peer closed: the shell exited

                var now = _clock.GetUtcNow();
                var text = shell.Decode(buffer, count, now);
                await _sessions.NoteOutputAsync(shell.Session.Id, now);

                if (shell.Session.Os == ShellOsGuess.Unknown
                    && fingerprintChunks < FingerprintChunkBudget
                    && text.Any(static ch => !char.IsWhiteSpace(ch)))
                {
                    fingerprintChunks++;
                    await _sessions.MarkFingerprintAsync(
                        shell.Session.Id, ShellFingerprinter.Guess(text));
                }
            }
        }
        catch (Exception ex) when (
            ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The socket died under the read: the normal ending for a lost
            // shell.
        }

        // The shell leaves the hub the moment its socket dies -- the routes
        // must not resolve a shell whose read side is gone, whatever the
        // durable marking's timing. The marking and the end records follow;
        // the serve path's finally re-removes harmlessly.
        _hub.Remove(shell.Session.Id);

        var at = _clock.GetUtcNow();
        if (shell.ClosedByOperator)
            await _sessions.CloseAsync(shell.Session.Id, at);
        else
            await _sessions.MarkLostAsync(shell.Session.Id, at);

        await RecordEndedAsync(shell, engagement, at);
    }

    private async Task RecordOpenedAsync(CaughtShell shell, EngagementId engagement)
    {
        var at = _clock.GetUtcNow();
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Value,
                operatorId: OperatorId.Empty.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "shell.session.opened",
                kind: AuditEventKind.ShellSessionOpened,
                payload: $"remote={shell.RemoteAddress} listener={_listener.Id}",
                output: null,
                outcome: shell.Session.Id.ToString(),
                at));
        await _live.PublishAsync(
            LiveEvent.ShellSession(
                engagement,
                LiveEventKind.ShellSessionOpened,
                JsonSerializer.Serialize(new ShellLivePayload(
                    shell.Session.Id.ToString(), "live", shell.RemoteAddress, ShellOsGuess.Unknown)),
                at));
    }

    private async Task RecordEndedAsync(CaughtShell shell, EngagementId engagement, DateTimeOffset at)
    {
        var status = shell.ClosedByOperator ? "closed" : "lost";
        await _audit.AppendAsync(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement.Value,
                operatorId: OperatorId.Empty.Value,
                implantId: Guid.Empty,
                taskId: Guid.Empty,
                verb: "shell.session.ended",
                kind: AuditEventKind.ShellSessionEnded,
                payload: $"status={status} lastInput={FormatStamp(shell.Session.LastInputAt)} "
                    + $"lastOutput={FormatStamp(shell.Session.LastOutputAt)}",
                output: null,
                outcome: shell.Session.Id.ToString(),
                at));
        await _live.PublishAsync(
            LiveEvent.ShellSession(
                engagement,
                LiveEventKind.ShellSessionEnded,
                JsonSerializer.Serialize(new ShellLivePayload(
                    shell.Session.Id.ToString(), status, shell.RemoteAddress, shell.Session.Os)),
                at));
    }

    private static string FormatStamp(DateTimeOffset? stamp)
        => stamp?.ToString("O") ?? "never";

    // Parses "host:port" for the TCP bind; accepts an IP (v4/v6) or "*" for
    // any interface -- the same shapes the TCP check-in listener accepts,
    // and a local duplicate of that parse because it is transport plumbing,
    // not policy (the house convention the DNS and TCP services follow).
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

        if (port is < 0 or > 65535)
            throw new InvalidOperationException($"TCP bind address '{bindAddress}' has an out-of-range port.");
        return (host, port);
    }

    // The live roster payload: one JSON line the console parses to refresh
    // its shell table. Kept as a serializer-friendly record at the bottom of
    // the file, the house shape for wire-adjacent DTOs.
    private sealed record ShellLivePayload(string SessionId, string Status, string Remote, ShellOsGuess Os);
}
