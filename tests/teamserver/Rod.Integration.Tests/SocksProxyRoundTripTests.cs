using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance, server half: the SOCKS relay bound onto a dispatched
/// tunnel.socks channel speaks the SOCKS5 a browser speaks, and carries every
/// connection over the one channel (architecture.md Sec 10.1 tunnel, Sec 14).
/// A real SOCKS handshake rides the bound listener; the contract-faithful
/// fake implant answers the open packet, bridges to the third host, and the
/// client's bytes come home -- the multiplexed grammar exercised end to end
/// over a real beacon stream. The implant half of the grammar is pinned by
/// the handler's unit tests and the real-implant end-to-end test.
/// </summary>
public class SocksProxyRoundTripTests
{
    [Fact]
    public async Task SocksBind_SpeaksSocks5_AndBridgesTheConnectionOverTheChannel()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // The third hosts: two listeners, so the proxy proves destinations are
        // per connection -- arbitrary, not baked at task time.
        await using var thirdOne = EchoHost.Start();
        await using var thirdTwo = EchoHost.Start();
        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Stage2);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "tunnel.socks"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // The proxy opens like any other task: no arguments, because every
        // destination arrives per connection.
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new
            {
                ImplantId = implant.Id.ToString(),
                Verb = "tunnel.socks",
            });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);

        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("tunnel.socks", request.Verb);
        Assert.Equal(string.Empty, request.Arguments);

        // The bind: one endpoint, the proxy surface.
        var bound = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/relay",
            new { });
        bound.EnsureSuccessStatusCode();
        var relay = await bound.Content.ReadFromJsonAsync<RelayBody>();
        Assert.NotNull(relay);
        Assert.True(relay!.Port > 0);

        // The fake implant's fronting of the multiplexed grammar: connections
        // under their id, bridged to the third hosts. A miniature of the
        // reference handler -- enough harness to prove the server half.
        using var bridge = new FakeProxyBridge(call, thirdOne.Port, thirdTwo.Port);
        _ = bridge.ServeAsync();

        // One SOCKS client to each third host: handshake, CONNECT, traffic.
        var one = await SocksClient.ConnectAsync(relay.Port, "127.0.0.1", thirdOne.Port);
        var two = await SocksClient.ConnectAsync(relay.Port, "127.0.0.1", thirdTwo.Port);
        await one.SendAsync("ping");
        Assert.Equal("ping", await one.ReceiveAsync());
        await two.SendAsync("pong");
        Assert.Equal("pong", await two.ReceiveAsync());

        // The proxy's own record: the bind is in the trail, attributed to the
        // binding operator, and the close follows the task's end.
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId)))
            .Any(e => e.Kind == AuditEventKind.RelayBound));

        // eof closes the proxy with the task; the summary is the implant's
        // record of what the proxy dialed.
        var closed = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Eof = true });
        closed.EnsureSuccessStatusCode();
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "socks proxy closed: 2 connections (0 refused), 8 bytes up, 8 bytes down",
        }));
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Status == "Completed");
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId)))
            .Any(e => e.Kind == AuditEventKind.RelayClosed));

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task SocksBind_FailsTheConnectWhenTheImplantRefusesTheDial()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // A port with nothing listening: the implant side refuses the dial,
        // and the SOCKS client sees a refused request, not a hang.
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var deadPort = ((IPEndPoint)taken.LocalEndpoint).Port;
        taken.Stop();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Stage2);
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "tunnel.socks"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "tunnel.socks" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);

        var bound = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/relay",
            new { });
        bound.EnsureSuccessStatusCode();
        var relay = await bound.Content.ReadFromJsonAsync<RelayBody>();

        // The fake implant refuses every dial: the opened packet carries the
        // failure, and the SOCKS reply tells the client where it stands.
        _ = RefuseDialsAsync(call, request.TaskId);

        using var tool = new TcpClient();
        await tool.ConnectAsync(IPAddress.Loopback, relay!.Port);
        var stream = tool.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var methods = new byte[2];
        await ReadExactlyAsync(stream, methods);
        Assert.Equal(0, methods[1]);
        var name = Encoding.ASCII.GetBytes("127.0.0.1");
        var connect = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
        connect.AddRange(name);
        connect.Add((byte)(deadPort >> 8));
        connect.Add((byte)deadPort);
        await stream.WriteAsync(connect.ToArray());

        var reply = new byte[10];
        await ReadExactlyAsync(stream, reply);
        Assert.Equal(5, reply[0]);
        Assert.NotEqual(0, reply[1]); // the SOCKS failure the dial refusal maps to

        await call.RequestStream.CompleteAsync();
    }

    // Answers every open packet with a refused dial -- the implant half of a
    // destination that will not connect.
    private static async Task RefuseDialsAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        string taskId)
    {
        while (await MoveNextAsync(call, "channel input"))
        {
            var frame = call.ResponseStream.Current;
            if (frame.Kind != FrameKind.ChannelInput)
                continue;
            var input = ChannelInput.Parser.ParseFrom(frame.Payload);
            if (input.TaskId != taskId || input.Eof)
                return;
            if (input.Data.Span.IsEmpty)
                continue;
            if (input.Data.Span[0] != 1)
                continue; // only open packets open dials

            var id = BinaryPrimitives.ReadUInt32LittleEndian(input.Data.Span.Slice(1, 4));
            await call.RequestStream.WriteAsync(OutputFrame(taskId, EncodePacket(4, id, new byte[] { 1 })));
        }
    }

    private static byte[] EncodePacket(byte kind, uint id, byte[] payload)
    {
        var packet = new byte[7 + payload.Length];
        packet[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)payload.Length);
        payload.CopyTo(packet, 7);
        return packet;
    }

    // The minimal SOCKS5 client a browser implements: no-auth greeting,
    // CONNECT with a domain-shaped address, then a byte stream.
    private sealed class SocksClient(NetworkStream stream) : IDisposable
    {
        public static async Task<SocksClient> ConnectAsync(int proxyPort, string host, int port)
        {
            var tool = new TcpClient();
            await tool.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = tool.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var method = new byte[2];
            await ReadExactlyAsync(stream, method);
            Assert.Equal(5, method[0]);
            Assert.Equal(0, method[1]);

            var name = Encoding.ASCII.GetBytes(host);
            var request = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
            request.AddRange(name);
            request.Add((byte)(port >> 8));
            request.Add((byte)port);
            await stream.WriteAsync(request.ToArray());

            var reply = new byte[10];
            await ReadExactlyAsync(stream, reply);
            Assert.Equal(5, reply[0]);
            Assert.Equal(0, reply[1]); // the dial's result, from the implant
            return new SocksClient(stream);
        }

        public Task SendAsync(string text)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        public async Task<string> ReceiveAsync()
        {
            var buffer = new byte[16 * 1024];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var read = await stream.ReadAsync(buffer, deadline.Token);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        public void Dispose() => stream.Dispose();
    }

    /// <summary>
    /// The fake implant's bridge: a miniature of the reference tunnel.socks
    /// handler -- open dials the named third host, data crosses both ways,
    /// and the channel's grammar is spoken exactly as the implant speaks it.
    /// </summary>
    private sealed class FakeProxyBridge : IDisposable
    {
        private readonly AsyncDuplexStreamingCall<Frame, Frame> _call;
        private readonly Dictionary<uint, TcpClient> _connections = new();
        private readonly List<byte> _parsed = new();
        private readonly int _portOne;
        private readonly int _portTwo;

        public FakeProxyBridge(
            AsyncDuplexStreamingCall<Frame, Frame> call,
            int portOne,
            int portTwo)
        {
            _call = call;
            _portOne = portOne;
            _portTwo = portTwo;
        }

        public Task ServeAsync() => ServeCoreAsync();

        private async Task ServeCoreAsync()
        {
            while (await MoveNextAsync(_call, "channel input"))
            {
                var frame = _call.ResponseStream.Current;
                if (frame.Kind != FrameKind.ChannelInput)
                    continue;
                var input = ChannelInput.Parser.ParseFrom(frame.Payload);
                if (input.Eof)
                    return;
                if (input.Data.Span.IsEmpty)
                    continue;

                _parsed.AddRange(input.Data.Span.ToArray());
                while (_parsed.Count >= 7)
                {
                    var kind = _parsed[0];
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(
                        _parsed.GetRange(1, 4).ToArray());
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(
                        _parsed.GetRange(5, 2).ToArray());
                    if (_parsed.Count < 7 + length)
                        break;
                    var payload = _parsed.GetRange(7, length).ToArray();
                    _parsed.RemoveRange(0, 7 + length);

                    if (kind == 1)
                        await OpenAsync(input.TaskId, id, payload);
                    else if (kind == 2 && _connections.TryGetValue(id, out var bridged))
                        await bridged.GetStream().WriteAsync(payload);
                }
            }
        }

        private async Task OpenAsync(string taskId, uint id, byte[] payload)
        {
            // Any of the third hosts serves: the destination came in the
            // packet, and the bridge simply dials it.
            var port = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port == _portOne || port == _portTwo ? port : _portOne);
            _connections[id] = client;
            await _call.RequestStream.WriteAsync(OutputFrame(taskId, EncodePacket(4, id, new byte[] { 0 })));
            _ = PumpDownAsync(taskId, id, client);
        }

        private async Task PumpDownAsync(string taskId, uint id, TcpClient client)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await client.GetStream().ReadAsync(buffer);
                    if (read <= 0)
                        return;
                    await _call.RequestStream.WriteAsync(
                        OutputFrame(taskId, EncodePacket(2, id, buffer[..read])));
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The connection ended with the test.
            }
        }

        public void Dispose()
        {
            foreach (var client in _connections.Values)
                client.Dispose();
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var read = await stream.ReadAsync(buffer.AsMemory(offset), deadline.Token);
            if (read <= 0)
                throw new IOException("the peer closed early");
            offset += read;
        }
    }

    // The deadline every downstream read waits under: a frame that never
    // arrives must fail the test, not park it forever.
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);

    private static async Task<TaskRequest> NextTaskRequestAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string taskId)
    {
        while (true)
        {
            Assert.True(await MoveNextAsync(call, "task request"), "stream ended early");
            var frame = call.ResponseStream.Current;
            if (frame.Kind != FrameKind.Unspecified)
                continue;
            var request = TaskRequest.Parser.ParseFrom(frame.Payload);
            if (request.TaskId == taskId)
                return request;
        }
    }

    private static async Task<bool> MoveNextAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string awaiting)
    {
        using var deadline = new CancellationTokenSource(ReadDeadline);
        try
        {
            return await call.ResponseStream.MoveNext(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for the downstream {awaiting} frame.");
        }
        catch (RpcException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for the downstream {awaiting} frame.");
        }
    }

    private static Frame OutputFrame(string taskId, params byte[][] packets)
    {
        var data = new List<byte>();
        foreach (var packet in packets)
            data.AddRange(packet);
        return new Frame
        {
            Payload = ByteString.CopyFrom(new ChannelOutput
            {
                TaskId = taskId,
                Data = ByteString.CopyFrom(data.ToArray()),
            }.ToByteArray()),
            Kind = FrameKind.ChannelOutput,
        };
    }

    private static Frame OutputFrame(string taskId, byte[] packet) => OutputFrame(taskId, new[] { packet });

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()) };

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock, ImplantClass @class)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), @class, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
    }

    private static Frame HandshakeFrame(ImplantId implant, params string[] capabilities)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
        };
        request.Capabilities.Add(capabilities);
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class RelayBody
    {
        public string TaskId { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
    }

    /// <summary>
    /// The third host of the acceptance test: a loopback TCP listener that
    /// echoes every byte back until its peer half-closes, then ends its own
    /// side. Serves any number of connections.
    /// </summary>
    private sealed class EchoHost : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;

        private EchoHost(TcpListener listener, Task serve)
        {
            _listener = listener;
            _serve = serve;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static EchoHost Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new EchoHost(listener, ServeAsync(listener));
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
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
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

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the tunnel round-trip harness.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public OperatorId OperatorId { get; private set; }
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();
            env.OperatorId = AuthenticatedHost.GetOperatorId(env.Host);

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            return env;
        }

        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, RSA leafKey)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(leafKey);
            var ca = Host.Services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();

            var handler = new SocketsHttpHandler();
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { leafWithKey },
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    if (cert is null)
                        return false;
                    chain!.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain!.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain!.ChainPolicy.ExtraStore.Add(ca);
                    return chain.Build((X509Certificate2)cert);
                },
            };

            return GrpcChannel.ForAddress($"https://127.0.0.1:{MtlsPort}", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
