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
/// Acceptance: a <c>tunnel.forward</c> task issued to a pivot-child session
/// executes on its parent and reaches the third host, attributed to the pivot
/// session end to end (architecture.md Sec 5.2, the Pivot class; Sec 14). A
/// pivot child is an identity with no process -- it never handshakes -- so its
/// tasking is claimed by the parent's beacon stream, arrives marked with the
/// child's id, executes in the parent (the tunnel the parent opens from its
/// own vantage), and every record -- issued, dispatched, the input the
/// operator posts, the completion -- attributes to the child. The signature
/// over the forwarded frame binds the child's own id (Sec 9), which this test
/// verifies independently: the parent is the executor, but the tasking was
/// signed for the child.
/// </summary>
public class PivotFrontingRoundTripTests
{
    [Fact]
    public async Task PivotChildsTunnel_ForwardedToTheParent_ReachesTheThirdHostAttributedToTheChild()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // The third host: reachable only from the implant's vantage.
        await using var thirdHost = EchoHost.Start();

        // The parent is a stage-2 implant with a live stream; the child is a
        // Pivot-class identity the parent enrolled (lateral.move, Sec 5.2) --
        // recorded server-side with its ParentImplantId. The child never
        // connects: no handshake, no session, no stream of its own.
        var now = clock.GetUtcNow();
        var engagement = EngagementId.New();
        var parent = Implant.Enroll(ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(parent);
        var child = Implant.EnrollChild(
            ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Pivot, now, parentImplantId: parent.Id);
        await implants.SaveAsync(child);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(parent.Id, engagement), leafKey, CancellationToken.None);

        using var channel = env.ConnectBeacon(X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(parent.Id, "tunnel.forward"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // The operator tasks the child, not the parent. The class gate admits
        // it (the Pivot class carries exactly the tunnel set, Sec 5.2), and
        // the parent's writer -- not the child, which has no writer -- claims
        // it through the fronting claim.
        await AuthenticatedHost.LoginAsync(env.Http);
        var response = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks",
            new
            {
                ImplantId = child.Id.ToString(),
                Verb = "tunnel.forward",
                Arguments = $"127.0.0.1 {thirdHost.Port}",
            });
        response.EnsureSuccessStatusCode();
        var issuedBody = await response.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);

        // The fronted frame arrives on the parent's stream, marked with the
        // child's id -- the marking a fronting implant routes on (Sec 5.2).
        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("tunnel.forward", request.Verb);
        Assert.Equal($"127.0.0.1 {thirdHost.Port}", request.Arguments);
        Assert.True(request.HasTargetImplantId);
        Assert.Equal(child.Id.ToString(), request.TargetImplantId);

        // The signature binds the child's own id, not the parent's (Sec 9):
        // the parent executes the tasking, but the tuple was signed for its
        // target. Verified here against the tasking CA the way the fronting
        // implant's verifier does.
        using var caPublicKey = ca.GetCaCertificate().GetRSAPublicKey()!;
        Assert.True(caPublicKey.VerifyData(
            Canonical(request.TargetImplantId, request.TaskId, request.Verb, request.Arguments),
            request.Signature.Span,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));

        // The parent executes for the child: it opens the tunnel to the third
        // host from its own vantage, exactly as it would for its own tasking.
        using var tunnel = new TcpClient();
        await tunnel.ConnectAsync(IPAddress.Loopback, thirdHost.Port);
        var peerStream = tunnel.GetStream();

        // The operator's input posts to the child's task route through the
        // fronting stream's sink: the channel input frame lands on the
        // parent's stream, addressed to the child's task.
        var sent = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{request.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("ping") });
        sent.EnsureSuccessStatusCode();
        var input = await NextChannelInputAsync(call, request.TaskId);
        Assert.Equal("ping", Encoding.UTF8.GetString(input.Data.Span));

        // The relay: the parent forwards to the third host and streams the
        // answer back as the child's channel output.
        await peerStream.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        var buffer = new byte[16 * 1024];
        var echoed = await peerStream.ReadAsync(buffer);
        Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, echoed));
        await call.RequestStream.WriteAsync(OutputFrame(request.TaskId, "ping"));

        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagement}/tasks/{request.TaskId}"))!.Output == "ping");

        // The parent's eof half-closes the tunnel; the third host ends its
        // side; the parent reports the child's final TaskResult.
        var closed = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{request.TaskId}/input",
            new { Eof = true });
        closed.EnsureSuccessStatusCode();
        var eof = await NextChannelInputAsync(call, request.TaskId);
        Assert.True(eof.Eof);
        tunnel.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await peerStream.ReadAsync(buffer));
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "tunnel to 127.0.0.1 closed: relayed 4 bytes up, 4 bytes down",
        }));

        // The attributed arc, end to end on the child: issued, dispatched,
        // the operator's two input posts (the bytes and the eof), and the
        // completion -- every event carries the pivot child's implant id, the
        // session that owns the work even though the parent's stream carried
        // every byte.
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 5);
        var events = await audit.ForTaskAsync(Guid.Parse(request.TaskId));
        Assert.All(events, e => Assert.Equal(child.Id.Value, e.ImplantId));
        Assert.Equal(
            new[] { "TaskIssued", "TaskDispatched", "ChannelInput", "ChannelInput", "TaskCompleted" },
            events.Select(e => e.Kind.ToString()).ToArray());

        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{engagement}/tasks/{request.TaskId}");
        Assert.NotNull(fetched);
        Assert.Equal(child.Id.ToString(), fetched!.ImplantId);
        Assert.Equal("Completed", fetched.Status);
        Assert.Equal("pingtunnel to 127.0.0.1 closed: relayed 4 bytes up, 4 bytes down", fetched.Output);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task FrontedTasking_ParksUntilTheParentsStreamClaimsIt()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var now = clock.GetUtcNow();
        var engagement = EngagementId.New();
        var parent = Implant.Enroll(ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(parent);
        var child = Implant.EnrollChild(
            ImplantId.New(), engagement, now.AddDays(30), ImplantClass.Pivot, now, parentImplantId: parent.Id);
        await implants.SaveAsync(child);

        // No parent stream is open: the child's tasking parks queued -- there
        // is no fronting writer to claim it and no child process to poll.
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks",
            new
            {
                ImplantId = child.Id.ToString(),
                Verb = "tunnel.forward",
                Arguments = "host.example 443",
            });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        var parked = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{engagement}/tasks/{issuedBody!.TaskId}");
        Assert.Equal("Queued", parked!.Status);

        // Input for a parked fronted channel has no stream to ride: the
        // route's conflict, not a silent drop.
        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{issuedBody.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("ping") });
        Assert.Equal(StatusCodes.Status409Conflict, (int)refused.StatusCode);
    }

    // The canonical signed encoding from the TaskRequest contract in
    // rod.proto: per field, the little-endian uint32 UTF-8 byte length then
    // the bytes. The nonceless four-element shape is what fronted tasking
    // carries (a pivot child never handshakes, so it never negotiated the
    // replay-nonce arm).
    private static byte[] Canonical(string implantId, string taskId, string verb, string arguments)
    {
        var fields = new[] { implantId, taskId, verb, arguments };
        var encoded = fields.Select(Encoding.UTF8.GetBytes).ToArray();
        using var buffer = new MemoryStream();
        foreach (var field in encoded)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)field.Length);
            buffer.Write(length);
            buffer.Write(field);
        }
        return buffer.ToArray();
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

    private static async Task<ChannelInput> NextChannelInputAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string taskId)
    {
        while (true)
        {
            Assert.True(await MoveNextAsync(call, "channel input"), "stream ended early");
            var frame = call.ResponseStream.Current;
            if (frame.Kind != FrameKind.ChannelInput)
                continue;
            var input = ChannelInput.Parser.ParseFrom(frame.Payload);
            if (input.TaskId == taskId)
                return input;
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

    private static Frame OutputFrame(string taskId, string text)
        => new()
        {
            Payload = ByteString.CopyFrom(new ChannelOutput
            {
                TaskId = taskId,
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes(text)),
            }.ToByteArray()),
            Kind = FrameKind.ChannelOutput,
        };

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()) };

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

    private sealed class TaskBody
    {
        public string TaskId { get; set; } = "";
        public string EngagementId { get; set; } = "";
        public string ImplantId { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
    }

    /// <summary>
    /// The third host of the acceptance test: a loopback TCP listener that
    /// echoes every byte back until its peer half-closes, then ends its own
    /// side. Standing in for the network segment only the implant can reach.
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
            var serve = ServeAsync(listener);
            return new EchoHost(listener, serve);
        }

        private static async Task ServeAsync(TcpListener listener)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
            }
            catch (SocketException)
            {
                return; // disposed before anything connected
            }
            catch (ObjectDisposedException)
            {
                return;
            }

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
                        return; // the peer reset the connection
                    }
                    if (received <= 0)
                        return; // the peer half-closed; end our side too
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
