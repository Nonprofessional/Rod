using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with the BCL Task; this file never uses
// the entity by name, so pin Task to the BCL type the async signatures need.
using Task = System.Threading.Tasks.Task;

namespace Rod.Transport.Channels;

// The SOCKS half of the relay bind (architecture.md Sec 10.1 tunnel, Sec 14):
// a teamserver-bound SOCKS listener bridged onto a dispatched tunnel.socks
// channel. Where the raw relay bridges one connection to a fixed destination,
// the SOCKS proxy is the channel's multiplexed arm -- every accepted SOCKS
// connection rides the one channel under its own id, each CONNECT's
// destination travels as an open packet, and the implant dials from its own
// vantage. An operator's SOCKS-configured tooling therefore reaches
// arbitrary hosts through the single task, with no per-connection tasking.
//
// The channel grammar is the verb's own (Sec 10: a channel's byte stream is
// its verb's grammar), mirrored here from the implant-side handler:
//
//     packet := kind(1) connection(4, LE) length(2, LE) payload(length)
//     open   (1): payload := port(2, LE) host-length(1) host-bytes
//     data   (2): payload := the proxied bytes, at most 16384
//     close  (3): payload empty -- the connection ended
//     opened (4): payload := status(1) -- 0 connected, otherwise refused
//
// This side owns the connection ids. Every packet is one channel-input unit,
// so the beacon stream's sink frames them whole, and the channel's output is
// re-framed by the same parser shape the implant runs. The proxy lives and
// dies with the task -- the final TaskResult, the beacon stream ending, a
// malformed stream, or the operator unbinding all close it -- the
// session-scoped lifetime every channel verb shares. SOCKS5, no auth,
// CONNECT only: the documented mainstream proxy surface a browser or
// proxychains speaks, nothing more.

/// <summary>
/// The registry of live SOCKS proxies: binds a SOCKS5 listener onto a
/// dispatched tunnel.socks channel, multiplexes every accepted connection
/// over the channel, and closes each proxy when its task, stream, or operator
/// ends it. Singleton, like the hub it feeds.
/// </summary>
internal sealed class SocksProxyHub
{
    private readonly LiveChannelHub _channels;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, BoundProxy> _byTask = new();

    public SocksProxyHub(LiveChannelHub channels, IAuditStore audit, TimeProvider clock)
    {
        _channels = channels;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Starts the proxy's listener. Null when the task already holds a
    /// bind -- one proxy per tunnel. The returned endpoint is what the
    /// operator points SOCKS-configured tooling at.
    /// </summary>
    public Task<(string Host, int Port)?> OpenAsync(TaskRelayHub.RelayBind bind, CancellationToken cancellationToken)
    {
        var proxy = new BoundProxy(_channels, _audit, _clock, bind);
        if (!_byTask.TryAdd(bind.Task.Value, proxy))
            return Task.FromResult<(string, int)?>(null);

        proxy.Start();
        var endpoint = (IPEndPoint)proxy.Endpoint!;
        return Task.FromResult<(string, int)?>((endpoint.Address.ToString(), endpoint.Port));
    }

    /// <summary>
    /// Whether the task currently holds a SOCKS proxy bind.
    /// </summary>
    public bool IsBound(Guid taskId) => _byTask.ContainsKey(taskId);

    /// <summary>
    /// Hands one channel output chunk to the task's proxy, raw. False (a
    /// no-op, never an error) when the task holds no bind. Called from the
    /// beacon ingest before the chunk decodes for the transcript; the proxy
    /// re-frames the stream and dispatches each packet to its connection.
    /// </summary>
    public bool TryDeliver(Guid taskId, ReadOnlyMemory<byte> data)
        => _byTask.TryGetValue(taskId, out var proxy) && proxy.Deliver(data);

    /// <summary>
    /// Closes the task's proxy -- the final TaskResult landed, so the
    /// channel and everything bridged onto it are over.
    /// </summary>
    public void CloseTask(Guid taskId, string reason)
    {
        if (_byTask.TryRemove(taskId, out var proxy))
            proxy.Close(reason);
    }

    /// <summary>
    /// Closes every proxy bridged onto the implant's channels -- its beacon
    /// stream ended, and a channel is session-scoped (architecture.md
    /// Sec 10.3): the proxy dies with the stream that carried it.
    /// </summary>
    public void CloseImplant(ImplantId implant, string reason)
    {
        foreach (var (taskId, proxy) in _byTask)
        {
            if (proxy.Implant == implant && _byTask.TryRemove(taskId, out var removed))
                removed.Close(reason);
        }
    }

    // One bound proxy: the SOCKS listener, every accepted connection under
    // its id, and the frame parser that routes the channel's output to them.
    // Close is idempotent -- the task completing, the stream dying, and the
    // operator unbinding can race, and whichever lands first owns the
    // RelayClosed audit write.
    private sealed class BoundProxy
    {
        // The socket read budget per data packet: the same chunk size the
        // implant's pumps use, so a packet frames inside the channel input
        // ceiling with room to spare.
        private const int ReadChunkBytes = 16 * 1024;

        // The concurrent-connection ceiling: a browser opens a handful at a
        // time; a runaway client must not pin the teamserver in sockets.
        private const int MaxConnections = 64;

        // How long a SOCKS handshake may take: a client that connects and
        // stalls must not hold a socket and a thread-shaped task open.
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

        // How long the proxy waits for the implant's dial result before
        // failing the SOCKS request: the implant refuses its own dials at
        // ten seconds, so this only catches a channel that went quiet.
        private static readonly TimeSpan DialAnswerTimeout = TimeSpan.FromSeconds(30);

        // The per-connection deliver queue depth: channel output parked
        // waiting for the client socket write. A client that cannot keep up
        // fills this and loses its connection -- an honest failure, never
        // silently corrupted bytes.
        private const int MaxQueuedChunks = 64;

        // The unparsed-remainder ceiling for the channel's output stream;
        // past it the stream cannot be framed and the proxy ends.
        private const int MaxParseBufferBytes = 1024 * 1024;

        private readonly LiveChannelHub _channels;
        private readonly IAuditStore _audit;
        private readonly TimeProvider _clock;
        private readonly TaskRelayHub.RelayBind _bind;
        private readonly CancellationTokenSource _done = new();
        private readonly ConcurrentDictionary<uint, ProxyConnection> _connections = new();
        // The channel's output re-framer: unparsed bytes in, whole packets
        // out. Sole writer is the deliver path (the beacon ingest thread).
        private readonly Parser _parser = new();
        private TcpListener? _listener;
        private uint _nextId;
        private int _liveConnections;
        private long _connects;
        private long _bytesUp;
        private long _bytesDown;
        private int _closed;

        public BoundProxy(LiveChannelHub channels, IAuditStore audit, TimeProvider clock, TaskRelayHub.RelayBind bind)
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
            _listener.Start(MaxConnections);
            Endpoint = _listener.LocalEndpoint;
            _ = AcceptAsync();
        }

        // One chunk of the channel's output, raw: re-frame it and route each
        // whole packet -- the implant's dial results and every connection's
        // answers.
        public bool Deliver(ReadOnlyMemory<byte> data)
        {
            if (_done.IsCancellationRequested)
                return false;

            foreach (var packet in _parser.Append(data.Span))
            {
                switch (packet.Kind)
                {
                    case PacketKind.Opened:
                        if (_connections.TryGetValue(packet.Id, out var connection))
                        {
                            var status = packet.Payload is [0] ? (byte)0 : (byte)1;
                            if (status == 0)
                                Interlocked.Increment(ref _connects);
                            connection.AnswerDial(status);
                        }
                        break;
                    case PacketKind.Data:
                        if (_connections.TryGetValue(packet.Id, out var receiving))
                        {
                            Interlocked.Add(ref _bytesDown, packet.Payload.Length);
                            if (!receiving.Deliver(packet.Payload))
                                Teardown(packet.Id);
                        }
                        break;
                    case PacketKind.Close:
                        Teardown(packet.Id);
                        break;
                    case PacketKind.Open:
                        Close("malformed channel stream: the implant does not open connections");
                        return false;
                }
            }

            if (_parser.IsCorrupt)
            {
                Close("malformed channel stream: the packets cannot be framed");
                return false;
            }
            return true;
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;

            _done.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Already stopped; the accept loop owns the ending.
            }

            foreach (var id in _connections.Keys)
                Teardown(id);

            _ = AppendClosedAuditAsync(reason);
        }

        private void Teardown(uint id)
        {
            if (_connections.TryRemove(id, out var connection))
            {
                Interlocked.Decrement(ref _liveConnections);
                connection.End();
            }
        }

        // Accepts SOCKS clients for the life of the bind: each gets its own
        // handshake and, on a successful CONNECT, its id on the channel.
        private async Task AcceptAsync()
        {
            while (!_done.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(_done.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return; // the bind ended
                }

                if (Interlocked.Increment(ref _liveConnections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _liveConnections);
                    client.Close();
                    continue;
                }

                _ = ServeClientAsync(client);
            }
        }

        // One SOCKS client: the greeting and the CONNECT request, then the
        // bridged session. SOCKS5 with no auth and CONNECT only -- the
        // surface a browser or proxychains speaks.
        private async Task ServeClientAsync(TcpClient client)
        {
            var id = Interlocked.Increment(ref _nextId);
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_done.Token);
                handshake.CancelAfter(HandshakeTimeout);
                var socket = client.GetStream();

                // The greeting: VER=5, the offered methods. No-auth (0x00)
                // must be among them or the client cannot use this proxy.
                var greeting = await ReadExactlyAsync(socket, 2, handshake.Token);
                if (greeting[0] != 5 || greeting[1] == 0)
                    return;
                var methods = await ReadExactlyAsync(socket, greeting[1], handshake.Token);
                if (!methods.Contains((byte)0))
                {
                    await socket.WriteAsync(new byte[] { 5, 0xFF }, handshake.Token);
                    return;
                }
                await socket.WriteAsync(new byte[] { 5, 0 }, handshake.Token);

                // The request: VER=5, CMD, RSV, ATYP, the address, the port.
                var request = await ReadExactlyAsync(socket, 4, handshake.Token);
                if (request[0] != 5)
                    return;
                if (request[1] != 1)
                {
                    await WriteReplyAsync(socket, 0x07, handshake.Token); // command not supported
                    return;
                }

                string host;
                switch (request[3])
                {
                    case 0x01:
                        host = new IPAddress(await ReadExactlyAsync(socket, 4, handshake.Token)).ToString();
                        break;
                    case 0x03:
                        var nameLength = (await ReadExactlyAsync(socket, 1, handshake.Token))[0];
                        host = Encoding.ASCII.GetString(await ReadExactlyAsync(socket, nameLength, handshake.Token));
                        break;
                    case 0x04:
                        host = new IPAddress(await ReadExactlyAsync(socket, 16, handshake.Token)).ToString();
                        break;
                    default:
                        await WriteReplyAsync(socket, 0x08, handshake.Token); // address type not supported
                        return;
                }
                var portBytes = await ReadExactlyAsync(socket, 2, handshake.Token);
                var port = (portBytes[0] << 8) | portBytes[1];

                // The connection joins the channel: open names the
                // destination, and the implant's dial result is the SOCKS
                // reply's truth. A channel that will not take the packet
                // fails the request where the client looks for the answer.
                var connection = new ProxyConnection(this, client, id);
                _connections[id] = connection;
                if (!Enqueue(EncodeOpen(id, host, port)))
                {
                    await WriteReplyAsync(socket, 0x01, CancellationToken.None);
                    Teardown(id);
                    return;
                }

                var status = await connection.DialAnswer.Task.WaitAsync(DialAnswerTimeout, handshake.Token);
                if (status != 0)
                {
                    await WriteReplyAsync(socket, 0x01, CancellationToken.None);
                    _ = Enqueue(EncodeClose(id));
                    Teardown(id);
                    return;
                }

                await WriteReplyAsync(socket, 0x00, handshake.Token);
                _ = connection.PumpUpAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The handshake stalled, the client vanished, or the proxy
                // closed: end the connection, whatever state it reached.
                if (_connections.TryRemove(id, out var connection))
                {
                    Interlocked.Decrement(ref _liveConnections);
                    connection.End();
                }
                else
                {
                    client.Close();
                }
            }
        }

        // One unit onto the channel: the same enqueue the operator input
        // route uses. False when the stream cannot take it.
        private bool Enqueue(byte[] packet)
            => _channels.TryEnqueue(_bind.Implant, _bind.Task.Value, packet, eof: false);

        private static async Task WriteReplyAsync(NetworkStream socket, byte reply, CancellationToken cancellationToken)
        {
            // VER=5, REP, RSV=0, ATYP=IPv4, BND.ADDR=0.0.0.0, BND.PORT=0 --
            // the bound address is the proxy's, not the target's; SOCKS
            // clients route on the reply code, not the bound endpoint.
            await socket.WriteAsync(
                new byte[] { 5, reply, 0, 1, 0, 0, 0, 0, 0, 0 },
                cancellationToken);
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream socket, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await socket.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read <= 0)
                    throw new IOException("the SOCKS client closed mid-handshake");
                offset += read;
            }
            return buffer;
        }

        private static byte[] EncodeOpen(uint id, string host, int port)
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            var payload = new byte[3 + hostBytes.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)port);
            payload[2] = (byte)hostBytes.Length;
            hostBytes.CopyTo(payload, 3);
            return Encode(PacketKind.Open, id, payload);
        }

        private static byte[] EncodeData(uint id, byte[] payload)
            => Encode(PacketKind.Data, id, payload);

        private static byte[] EncodeClose(uint id)
            => Encode(PacketKind.Close, id, Array.Empty<byte>());

        private static byte[] Encode(byte kind, uint id, byte[] payload)
        {
            var packet = new byte[7 + payload.Length];
            packet[0] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)payload.Length);
            payload.CopyTo(packet, 7);
            return packet;
        }

        private async Task AppendClosedAuditAsync(string reason)
        {
            var connections = Interlocked.Read(ref _connects);
            var up = Interlocked.Read(ref _bytesUp);
            var down = Interlocked.Read(ref _bytesDown);
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
                        payload: $"{reason}; {connections} connections, {up} bytes up, {down} bytes down",
                        output: null,
                        outcome: "closed",
                        at: _clock.GetUtcNow()),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The close audit is the proxy's only durable trace beyond
                // the task's own transcript; a store failure must not take
                // the process down with a listener it can no longer account
                // for.
            }
        }

        // One proxied SOCKS connection: the client socket, the dial answer
        // the SOCKS reply waits on, and the bounded queue the channel's
        // output drains into.
        private sealed class ProxyConnection
        {
            private readonly BoundProxy _proxy;
            private readonly TcpClient _client;
            private readonly object _writerGate = new();
            private readonly Channel<byte[]> _downbound =
                Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedChunks)
                {
                    SingleReader = true,
                });
            private Task? _writer;

            public ProxyConnection(BoundProxy proxy, TcpClient client, uint id)
            {
                _proxy = proxy;
                _client = client;
                Id = id;
            }

            public uint Id { get; }

            public TaskCompletionSource<byte> DialAnswer { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            // The implant's dial result, from the channel's opened packet.
            public void AnswerDial(byte status) => DialAnswer.TrySetResult(status);

            // One data packet onto the connection's write queue. False when
            // the queue is full -- the client is not draining, and its
            // connection ends rather than pinning the proxy.
            public bool Deliver(byte[] payload)
            {
                StartWriter();
                return _downbound.Writer.TryWrite(payload);
            }

            // The client's bytes onto the channel: one data packet per read,
            // through the same enqueue the operator input route uses. A
            // channel that will not take the packet ends the connection --
            // the client sees a dropped proxy connection, not a stall.
            public async Task PumpUpAsync()
            {
                var buffer = new byte[ReadChunkBytes];
                try
                {
                    var socket = _client.GetStream();
                    while (true)
                    {
                        var read = await socket.ReadAsync(buffer, _proxy._done.Token);
                        if (read <= 0)
                            break;
                        if (!_proxy.Enqueue(BoundProxy.EncodeData(Id, buffer.AsSpan(0, read).ToArray())))
                            return;
                        Interlocked.Add(ref _proxy._bytesUp, read);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // The proxy closed or the client went away; the ending is owned.
                }
                finally
                {
                    _ = _proxy.Enqueue(BoundProxy.EncodeClose(Id));
                    _proxy.Teardown(Id);
                }
            }

            // Ends the connection: complete the queue, close the socket.
            // Idempotent -- the proxy's teardown and a finished writer can
            // both land here.
            public void End()
            {
                _downbound.Writer.TryComplete();
                _client.Close();
            }

            // The channel's bytes onto the client socket, one queued packet
            // per write. Started under a gate on the first deliver -- the
            // channel may deliver before the SOCKS reply is even written --
            // so exactly one writer ever reads the single-reader queue.
            private void StartWriter()
            {
                lock (_writerGate)
                {
                    _writer ??= WriteDownAsync();
                }
            }

            private async Task WriteDownAsync()
            {
                try
                {
                    var socket = _client.GetStream();
                    await foreach (var packet in _downbound.Reader.ReadAllAsync(_proxy._done.Token))
                        await socket.WriteAsync(packet, CancellationToken.None);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // The client went away or the proxy closed; the ending is owned.
                }
            }
        }

        // Re-frames the channel's output bytes into whole packets -- the
        // mirror of the implant-side parser, the same grammar read the other
        // direction.
        private sealed class Parser
        {
            private const int MaxPacketBytes = ReadChunkBytes;

            private byte[] _buffer = new byte[MaxPacketBytes + 7];
            private int _length;

            public bool IsCorrupt { get; private set; }

            public IReadOnlyList<(byte Kind, uint Id, byte[] Payload)> Append(ReadOnlySpan<byte> data)
            {
                var packets = new List<(byte, uint, byte[])>();
                if (IsCorrupt)
                    return packets;

                if (data.Length + _length > MaxParseBufferBytes)
                {
                    IsCorrupt = true;
                    return packets;
                }
                if (data.Length + _length > _buffer.Length)
                {
                    var grown = new byte[Math.Max(data.Length + _length, _buffer.Length * 2)];
                    _buffer.AsSpan(0, _length).CopyTo(grown);
                    _buffer = grown;
                }
                data.CopyTo(_buffer.AsSpan(_length));
                _length += data.Length;

                while (_length >= 7)
                {
                    var kind = _buffer[0];
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(1));
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(5));
                    if (kind is not (PacketKind.Open or PacketKind.Data or PacketKind.Close or PacketKind.Opened)
                        || length > MaxPacketBytes)
                    {
                        IsCorrupt = true;
                        return packets;
                    }
                    if (_length < 7 + length)
                        break; // the packet's tail is still in flight

                    packets.Add((kind, id, _buffer[7..(7 + length)]));
                    var consumed = 7 + length;
                    Array.Copy(_buffer, consumed, _buffer, 0, _length - consumed);
                    _length -= consumed;
                }
                return packets;
            }
        }
    }

    // The packet kinds of the channel grammar, shared with the endpoint's
    // callers through the hub's surface only.
    private static class PacketKind
    {
        public const byte Open = 1;
        public const byte Data = 2;
        public const byte Close = 3;
        public const byte Opened = 4;
    }
}
