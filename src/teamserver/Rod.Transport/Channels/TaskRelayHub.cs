using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with the BCL Task; this file never uses
// the entity by name, so pin Task to the BCL type the async signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Channels;

// The operator-side relay bind (architecture.md Sec 10.1 tunnel, Sec 10.3): a
// teamserver-bound TCP listener bridged into a live tunnel channel, so an
// operator's unmodified tooling rides the tunnel through one task instead of
// driving it by per-byte input posts. The relay is the machine-shaped stand-in
// for the operator's HTTP posts: its accepted socket's reads enter the same
// LiveChannelHub enqueue the input route uses, and the channel's output chunks
// are handed back raw -- before the transcript's UTF-8 decode -- so the bytes
// that cross the operator's socket are the bytes the channel carried, not a
// lossy text projection of them.
//
// One relay bridges one connection: the tunnel channel is one TCP connection
// on the implant's side, so a second client would multiplex onto a tunnel that
// cannot tell the streams apart. The bind lives and dies with the task -- the
// final TaskResult, the beacon stream ending, or the operator unbinding all
// close it -- which is the session-scoped lifetime every channel verb shares.

/// <summary>
/// The registry of live task relays: binds a TCP listener onto a dispatched
/// tunnel channel, bridges the accepted connection both ways, and closes every
/// relay when its task, stream, or operator ends it. Singleton, like the hub it
/// feeds.
/// </summary>
internal sealed class TaskRelayHub
{
    private readonly LiveChannelHub _channels;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, BoundRelay> _byTask = new();

    public TaskRelayHub(LiveChannelHub channels, IAuditStore audit, TimeProvider clock)
    {
        _channels = channels;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// One relay's identity and attribution, resolved by the binding route:
    /// the task and implant the channel runs on, the engagement and operator
    /// the bind is attributed to, and the listen address requested of
    /// <see cref="BindAsync"/>.
    /// </summary>
    public readonly record struct RelayBind(
        EngagementId EngagementId,
        ImplantId Implant,
        TaskId Task,
        OperatorId Operator,
        string Verb,
        IPAddress Address,
        int Port);

    /// <summary>
    /// Starts the relay's listener. Null when the task already holds a bind --
    /// one relay per tunnel. The listener accepts exactly one connection and
    /// bridges it; the returned endpoint is what the operator points tooling
    /// at.
    /// </summary>
    public Task<(string Host, int Port)?> OpenAsync(RelayBind bind, CancellationToken cancellationToken)
    {
        var relay = new BoundRelay(_channels, _audit, _clock, bind);
        if (!_byTask.TryAdd(bind.Task.Value, relay))
            return Task.FromResult<(string, int)?>(null);

        relay.Start();
        var endpoint = (IPEndPoint)relay.Endpoint!;
        return Task.FromResult<(string, int)?>((endpoint.Address.ToString(), endpoint.Port));
    }

    /// <summary>
    /// Whether the task currently holds a relay bind.
    /// </summary>
    public bool IsBound(Guid taskId) => _byTask.ContainsKey(taskId);

    /// <summary>
    /// Hands one channel output chunk to the task's relay, raw. False (a no-op,
    /// never an error) when the task holds no bind -- the common case on every
    /// non-relayed channel. Called from the beacon ingest before the chunk
    /// decodes for the transcript, so the relay carries the channel's bytes.
    /// </summary>
    public bool TryDeliver(Guid taskId, ReadOnlyMemory<byte> data)
        => _byTask.TryGetValue(taskId, out var relay) && relay.Deliver(data);

    /// <summary>
    /// Closes the task's relay -- the final TaskResult landed, so the channel
    /// and everything bridged onto it are over.
    /// </summary>
    public void CloseTask(Guid taskId, string reason)
    {
        if (_byTask.TryRemove(taskId, out var relay))
            relay.Close(reason);
    }

    /// <summary>
    /// Closes every relay bridged onto the implant's channels -- its beacon
    /// stream ended, and a channel is session-scoped (architecture.md
    /// Sec 10.3): the relay dies with the stream that carried it.
    /// </summary>
    public void CloseImplant(ImplantId implant, string reason)
    {
        foreach (var (taskId, relay) in _byTask)
        {
            if (relay.Implant == implant && _byTask.TryRemove(taskId, out var removed))
                removed.Close(reason);
        }
    }

    // One bound relay: the listener, the single accepted connection, and the
    // two pumps that bridge it onto the channel. Close is idempotent -- the
    // task completing, the stream dying, and the operator unbinding can race,
    // and whichever lands first owns the RelayClosed audit write.
    private sealed class BoundRelay
    {
        // The socket read budget per channel input unit: the same chunk size
        // the implant's own pumps use, so a relayed read frames like any other
        // input the sink drains.
        private const int ReadChunkBytes = 16 * 1024;

        // The deliver queue depth: output chunks parked waiting for the socket
        // write. A relay whose tool cannot keep up fills this and the relay
        // ends -- an honest failure the tool sees as a dropped connection,
        // never silently corrupted bytes.
        private const int MaxQueuedChunks = 256;

        private readonly LiveChannelHub _channels;
        private readonly IAuditStore _audit;
        private readonly TimeProvider _clock;
        private readonly RelayBind _bind;
        private readonly CancellationTokenSource _done = new();
        // The deliver queue: producer (the beacon ingest thread) TryWrites,
        // the socket writer drains. Bounded so a stalled tool cannot pin
        // server memory; a full queue ends the relay.
        private readonly Channel<ReadOnlyMemory<byte>> _outbound =
            Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(MaxQueuedChunks)
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private TcpListener? _listener;
        private long _bytesUp;
        private long _bytesDown;
        private int _closed;

        public BoundRelay(LiveChannelHub channels, IAuditStore audit, TimeProvider clock, RelayBind bind)
        {
            _channels = channels;
            _audit = audit;
            _clock = clock;
            _bind = bind;
        }

        public ImplantId Implant => _bind.Implant;

        public EndPoint? Endpoint { get; private set; }

        public void Start()
        {
            _listener = new TcpListener(_bind.Address, _bind.Port);
            _listener.Start(1);
            Endpoint = _listener.LocalEndpoint;
            _ = AcceptAsync();
        }

        public bool Deliver(ReadOnlyMemory<byte> data)
        {
            // A deliver past the relay's end (a straggler chunk after the
            // final TaskResult) lands in a closed queue; the TryRemove in the
            // hub keeps it from arriving at all once the close wins the race.
            if (_done.IsCancellationRequested)
                return false;
            if (!_outbound.Writer.TryWrite(data))
            {
                Close("relay queue overflow: the operator-side tool is not draining");
                return false;
            }
            return true;
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;

            _done.Cancel();
            _outbound.Writer.TryComplete();
            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Already stopped; the accept loop owns the ending.
            }

            var up = Interlocked.Read(ref _bytesUp);
            var down = Interlocked.Read(ref _bytesDown);
            _ = AppendClosedAuditAsync(reason, up, down);
        }

        // Accepts the one connection this relay bridges, then runs both pumps
        // until the channel ends, the tool disconnects, or the relay closes.
        // A listener stopped before anything connected (the task or stream
        // died first) ends without an audit-worthy connection.
        private async Task AcceptAsync()
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_done.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return; // closed before a tool connected
            }

            using (client)
            {
                client.NoDelay = true;
                var up = PumpUpAsync(client);
                var down = PumpDownAsync(client);
                var ended = await Task.WhenAny(up, down);
                Close(ended == up
                    ? "the operator-side tool closed the connection"
                    : "the channel stopped delivering output");
                await Task.WhenAll(up, down);
            }
        }

        // The tool's bytes onto the channel: each socket read becomes one
        // input unit through the same enqueue the operator input route uses,
        // and the tool's half-close rides as eof -- the tunnel's send-side
        // shutdown. An enqueue the stream cannot take ends the relay: the
        // tunnel is not draining, and the tool must see that, not stall.
        private async Task PumpUpAsync(TcpClient client)
        {
            var socket = client.GetStream();
            var buffer = new byte[ReadChunkBytes];
            try
            {
                while (true)
                {
                    var read = await socket.ReadAsync(buffer, _done.Token);
                    if (read <= 0)
                    {
                        _channels.TryEnqueue(_bind.Implant, _bind.Task.Value, ReadOnlyMemory<byte>.Empty, eof: true);
                        return;
                    }

                    var data = new byte[read];
                    Array.Copy(buffer, data, read);
                    if (!_channels.TryEnqueue(_bind.Implant, _bind.Task.Value, data, eof: false))
                    {
                        Close("the beacon stream stopped accepting channel input");
                        return;
                    }
                    Interlocked.Add(ref _bytesUp, read);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The relay closed under the pump; the close path owns the ending.
            }
        }

        // The channel's output onto the tool's socket: the deliver queue
        // drains one chunk per write, the queue's bounded capacity is the
        // backpressure, and the channel ending completes the queue so this
        // pump returns with the relay.
        private async Task PumpDownAsync(TcpClient client)
        {
            var socket = client.GetStream();
            try
            {
                await foreach (var chunk in _outbound.Reader.ReadAllAsync(_done.Token))
                {
                    await socket.WriteAsync(chunk, CancellationToken.None);
                    Interlocked.Add(ref _bytesDown, chunk.Length);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The tool disconnected or the relay closed; the ending is owned.
            }
        }

        private async Task AppendClosedAuditAsync(string reason, long up, long down)
        {
            try
            {
                await _audit.AppendAsync(
                    AuditEvent.Fact(
                        eventId: Guid.NewGuid(),
                        engagementId: _bind.EngagementId.Value,
                        operatorId: _bind.Operator.Value,
                        implantId: _bind.Implant.Value,
                        taskId: _bind.Task.Value,
                        verb: _bind.Verb,
                        kind: AuditEventKind.RelayClosed,
                        payload: $"{reason}; relayed {up} bytes up, {down} bytes down",
                        output: null,
                        outcome: "closed",
                        at: _clock.GetUtcNow()),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The close audit is the relay's only durable trace beyond the
                // task's own transcript; a store failure must not take the
                // process down with a listener it can no longer account for.
            }
        }
    }
}
