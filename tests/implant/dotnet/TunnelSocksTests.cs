using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

/// <summary>
/// Checks of the tunnel.socks handler (architecture.md Sec 5.2, Sec 10.1,
/// Sec 14): the channel's byte stream is the proxy's own
/// connection-multiplexed grammar -- each connection rides under its id, an
/// open packet names the destination, and the implant dials from its own
/// vantage and answers with the dial's result. Drives the handler against a
/// fake IChannelStream and a loopback echo peer standing in for the third
/// host; the SOCKS half (the listener, the handshake) is the server-side
/// integration suite's subject.
/// </summary>
public class TunnelSocksTests
{
    [Fact]
    public async Task RunAsync_BridgesConnectionsUnderIds_AndSummarizesTheDestinations()
    {
        await using var peer = EchoPeer.Start();
        var channel = new FakeChannel();
        var runner = TunnelSocks.RunAsync(string.Empty, channel, CancellationToken.None);

        // One connection: open names the destination, the dial's result comes
        // back as an opened packet, and the operator's bytes cross to the
        // peer whose answer streams home under the same id.
        await channel.SendAsync(EncodeOpen(1, "127.0.0.1", peer.Port));
        var opened = await channel.WaitUntilPacketAsync(TunnelSocks.PacketKind.Opened, 1);
        Assert.Equal(0, opened[0]);

        await channel.SendAsync(EncodeData(1, "ping"));
        var echoed = await channel.WaitUntilDataAsync(1);
        Assert.Equal("ping", Encoding.UTF8.GetString(echoed));

        // The operator's eof closes the proxy; the summary is the record.
        await channel.SendEofAsync();
        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("1 connections (0 refused)", output);
        Assert.Contains("4 bytes up, 4 bytes down", output);
        Assert.Contains($"127.0.0.1:{peer.Port}", output);
    }

    [Fact]
    public async Task RunAsync_CarriesTwoConnectionsSideBySide()
    {
        await using var peer = EchoPeer.Start();
        var channel = new FakeChannel();
        var runner = TunnelSocks.RunAsync(string.Empty, channel, CancellationToken.None);

        await channel.SendAsync(EncodeOpen(7, "127.0.0.1", peer.Port));
        await channel.WaitUntilPacketAsync(TunnelSocks.PacketKind.Opened, 7);
        await channel.SendAsync(EncodeOpen(9, "127.0.0.1", peer.Port));
        await channel.WaitUntilPacketAsync(TunnelSocks.PacketKind.Opened, 9);

        // Both ids ride the one channel; bytes stay under their own id.
        await channel.SendAsync(EncodeData(7, "seven"));
        Assert.Equal("seven", Encoding.UTF8.GetString(await channel.WaitUntilDataAsync(7)));
        await channel.SendAsync(EncodeData(9, "nine!"));
        Assert.Equal("nine!", Encoding.UTF8.GetString(await channel.WaitUntilDataAsync(9)));

        await channel.SendEofAsync();
        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("2 connections", output);
        Assert.Contains($"127.0.0.1:{peer.Port} x2", output);
    }

    [Fact]
    public async Task RunAsync_RefusesADestinationThatWillNotConnect()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        taken.Stop();

        var channel = new FakeChannel();
        var runner = TunnelSocks.RunAsync(string.Empty, channel, CancellationToken.None);

        await channel.SendAsync(EncodeOpen(1, "127.0.0.1", port));
        var opened = await channel.WaitUntilPacketAsync(TunnelSocks.PacketKind.Opened, 1);
        Assert.NotEqual(0, opened[0]);

        await channel.SendEofAsync();
        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("0 connections (1 refused)", output);
    }

    [Fact]
    public async Task RunAsync_EndsWithTheStreamThatCarriesIt()
    {
        await using var peer = EchoPeer.Start();
        var channel = new FakeChannel();
        using var gone = new CancellationTokenSource();

        var runner = TunnelSocks.RunAsync(string.Empty, channel, gone.Token);
        await channel.SendAsync(EncodeOpen(1, "127.0.0.1", peer.Port));
        await channel.WaitUntilPacketAsync(TunnelSocks.PacketKind.Opened, 1);
        gone.Cancel();

        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("channel closed", output);
    }

    [Fact]
    public async Task RunAsync_RefusesArgumentsOutsideTheGrammar()
    {
        var channel = new FakeChannel();
        var (outcome, output) = await TunnelSocks.RunAsync("127.0.0.1 8080", channel, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Equal("tunnel.socks takes no arguments; destinations arrive per connection", output);
    }

    [Fact]
    public async Task RunAsync_FailsAMalformedPacketStream()
    {
        var channel = new FakeChannel();
        var runner = TunnelSocks.RunAsync(string.Empty, channel, CancellationToken.None);

        // An unknown packet kind: the proxy ends rather than limps on a
        // stream it cannot frame.
        await channel.SendAsync(new byte[] { 0x7f, 1, 0, 0, 0, 0, 0 });
        var (outcome, output) = await runner.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("malformed packet stream", output);
    }

    // --- The channel grammar, as the operator side writes it. ---

    private static byte[] EncodeOpen(uint id, string host, int port)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var payload = new byte[3 + hostBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)port);
        payload[2] = (byte)hostBytes.Length;
        hostBytes.CopyTo(payload, 3);
        return Encode(TunnelSocks.PacketKind.Open, id, payload);
    }

    private static byte[] EncodeData(uint id, string text)
        => Encode(TunnelSocks.PacketKind.Data, id, Encoding.UTF8.GetBytes(text));

    private static byte[] Encode(byte kind, uint id, byte[] payload)
    {
        var packet = new byte[7 + payload.Length];
        packet[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)payload.Length);
        payload.CopyTo(packet, 7);
        return packet;
    }

    /// <summary>
    /// A fake channel: collects the output chunks the handler streams and
    /// hands the handler input on demand, plus packet-level waits so the
    /// tests sequence open/data/close like the operator side would.
    /// </summary>
    private sealed class FakeChannel : IChannelStream
    {
        private readonly System.Threading.Channels.Channel<(byte[]? Data, bool Eof)> _input =
            System.Threading.Channels.Channel.CreateUnbounded<(byte[]? Data, bool Eof)>();

        private readonly List<byte> _output = new();
        private readonly object _outputGate = new();

        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            lock (_outputGate)
            {
                _output.AddRange(data.ToArray());
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<(byte[]? Data, bool Eof)> ReadInputAsync(CancellationToken cancellationToken)
            => _input.Reader.ReadAsync(cancellationToken);

        public Task SendAsync(byte[] data)
            => _input.Writer.WriteAsync((data, false)).AsTask();

        public Task SendEofAsync()
            => _input.Writer.WriteAsync((Array.Empty<byte>(), true)).AsTask();

        // Waits until a whole packet with the kind and id arrives and returns
        // its payload -- the observable effect of a connection's dial or its
        // relayed bytes.
        public async Task<byte[]> WaitUntilPacketAsync(byte kind, uint id, TimeSpan? deadline = null)
        {
            var until = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(10));
            while (DateTimeOffset.UtcNow < until)
            {
                foreach (var packet in ParsePackets())
                {
                    if (packet.Kind == kind && packet.Id == id)
                        return packet.Payload;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"timed out waiting for packet kind {kind} id {id}");
        }

        public async Task<byte[]> WaitUntilDataAsync(uint id, TimeSpan? deadline = null)
        {
            var until = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(10));
            while (DateTimeOffset.UtcNow < until)
            {
                foreach (var packet in ParsePackets())
                {
                    if (packet.Kind == TunnelSocks.PacketKind.Data && packet.Id == id && packet.Payload.Length > 0)
                        return packet.Payload;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"timed out waiting for data on connection {id}");
        }

        private List<(byte Kind, uint Id, byte[] Payload)> ParsePackets()
        {
            var packets = new List<(byte, uint, byte[])>();
            lock (_outputGate)
            {
                var offset = 0;
                while (offset + 7 <= _output.Count)
                {
                    var kind = _output[offset];
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(
                        _output.GetRange(offset + 1, 4).ToArray());
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(
                        _output.GetRange(offset + 5, 2).ToArray());
                    if (offset + 7 + length > _output.Count)
                        break;
                    var payload = _output.GetRange(offset + 7, length).ToArray();
                    packets.Add((kind, id, payload));
                    offset += 7 + length;
                }
            }
            return packets;
        }
    }

    /// <summary>
    /// The third host of these tests: a loopback TCP listener that echoes
    /// every byte back until its peer half-closes, then ends its own side.
    /// Serves any number of connections -- the proxy multiplexes.
    /// </summary>
    private sealed class EchoPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;

        private EchoPeer(TcpListener listener, Task serve)
        {
            _listener = listener;
            _serve = serve;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static EchoPeer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new EchoPeer(listener, ServeAsync(listener));
        }

        private static async Task ServeAsync(TcpListener listener)
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync();
                }
                catch (SocketException)
                {
                    return; // disposed
                }
                catch (ObjectDisposedException)
                {
                    return; // disposed
                }

                _ = ServeOneAsync(socket);
            }
        }

        private static async Task ServeOneAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var received = 0;
                    try
                    {
                        received = await socket.ReceiveAsync(buffer, SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    if (received <= 0)
                        return;
                    var sent = 0;
                    while (sent < received)
                        sent += await socket.SendAsync(
                            buffer.AsMemory(sent, received - sent), SocketFlags.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }
}
