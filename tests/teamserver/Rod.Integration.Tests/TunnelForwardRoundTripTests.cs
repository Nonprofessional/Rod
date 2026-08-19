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
/// Acceptance: a task on an implant reaches a third host through a
/// tunnel.forward channel, and the traffic is attributed end to end
/// (architecture.md Sec 5.2, Sec 14 -- the tunnel verbs). Drives the full
/// slice through a real Kestrel mTLS endpoint with a contract-faithful fake
/// implant: the operator issues <c>tunnel.forward</c> naming the third host,
/// the TaskRequest opens the channel, the implant bridges the channel to a TCP
/// connection it opens from its own vantage, the operator's bytes flow down as
/// ChannelInput and the third host's answers stream back as ChannelOutput onto
/// the task's transcript, and the final TaskResult closes the task with the
/// traffic as its record. A Pivot-class implant runs the same verb -- the class
/// carries exactly the tunnel set, so the tunneling artifact is taskable the
/// day it is built.
/// </summary>
public class TunnelForwardRoundTripTests
{
    [Fact]
    public async Task TunnelForward_BridgesTheOperatorToAThirdHost_AndAttributesTheTraffic()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // The third host: reachable from the implant's vantage, not the
        // operator's -- everything below reaches it only through the tunnel.
        await using var thirdHost = EchoHost.Start();
        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Stage2);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "tunnel.forward"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // The operator opens the tunnel like any other task, naming the third
        // host and port.
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new
            {
                ImplantId = implant.Id.ToString(),
                Verb = "tunnel.forward",
                Arguments = $"127.0.0.1 {thirdHost.Port}",
            });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);

        // The channel opens: the TaskRequest arrives downstream and the implant
        // bridges it to a TCP connection of its own.
        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("tunnel.forward", request.Verb);
        Assert.Equal($"127.0.0.1 {thirdHost.Port}", request.Arguments);
        using var tunnel = new TcpClient();
        await tunnel.ConnectAsync(IPAddress.Loopback, thirdHost.Port);
        // One stream held for the test's life: GetStream re-checks the socket's
        // connected flag, which a half-close clears, and the half-close below
        // is part of this test's grammar.
        var peerStream = tunnel.GetStream();

        // The operator sends traffic down the tunnel. The bytes arrive on the
        // channel as ChannelInput, the implant relays them to the third host,
        // and the answer streams back as ChannelOutput onto the transcript --
        // readable live, while the tunnel runs.
        var sent = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("ping") });
        sent.EnsureSuccessStatusCode();
        var input = await NextChannelInputAsync(call, request.TaskId);
        Assert.Equal("ping", Encoding.UTF8.GetString(input.Data.Span));

        var tunnelBuffer = new byte[16 * 1024];
        await peerStream.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        var read = await peerStream.ReadAsync(tunnelBuffer);
        Assert.Equal("ping", Encoding.UTF8.GetString(tunnelBuffer, 0, read));
        await call.RequestStream.WriteAsync(OutputFrame(request.TaskId, "ping"));

        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Output == "ping");

        // The operator closes the tunnel's stdin: eof rides the same route, the
        // implant half-closes the TCP connection, and the third host ending its
        // side closes the task.
        var closed = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Eof = true });
        closed.EnsureSuccessStatusCode();
        var eof = await NextChannelInputAsync(call, request.TaskId);
        Assert.True(eof.Eof);

        tunnel.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await peerStream.ReadAsync(tunnelBuffer));
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "tunnel closed: relayed 4 bytes up, 4 bytes down",
        }));

        // The tunnel's attributed arc: issued, dispatched, two input posts (the
        // bytes and the eof), and the completion carrying the traffic.
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 5);

        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}");
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("pingtunnel closed: relayed 4 bytes up, 4 bytes down", fetched.Output);
        Assert.Equal("Succeeded", fetched.Outcome);
        Assert.Equal(
            new[] { "TaskIssued", "TaskDispatched", "ChannelInput", "ChannelInput", "TaskCompleted" },
            fetched.Audit.Select(e => e.Kind).ToArray());

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task TunnelForward_IsTaskableOnThePivotClass()
    {
        // The pivot class carries exactly the tunnel set (architecture.md Sec
        // 5.2): a Pivot-class implant takes tunnel.forward tasking the moment it
        // enrols -- the class stopped admitting nothing.
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        await using var thirdHost = EchoHost.Start();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Pivot);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "tunnel.forward"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new
            {
                ImplantId = implant.Id.ToString(),
                Verb = "tunnel.forward",
                Arguments = $"127.0.0.1 {thirdHost.Port}",
            });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("tunnel.forward", request.Verb);

        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "tunnel closed: relayed 0 bytes up, 0 bytes down",
        }));
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Status == "Completed");

        await call.RequestStream.CompleteAsync();
    }

    // The deadline every downstream read waits under: a frame that never
    // arrives must fail the test, not park it forever.
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);

    // Reads downstream frames until the TaskRequest for taskId arrives. The
    // handshake precedes tasking; other kind-bearing downstream frames are
    // skipped (a channel input racing the dispatch, never before it).
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

    // Reads downstream frames until the ChannelInput for taskId arrives --
    // the only kind-bearing downstream frame today.
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

    // One bounded downstream read: the raw gRPC wait carries no deadline of
    // its own, and a hung suite costs an hour -- a missing frame fails the
    // test with what it was waiting for instead.
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

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class AuditBody
    {
        public string Kind { get; set; } = "";
        public string? Output { get; set; }
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
    /// plain-HTTP operator API. Mirrors the interactive-shell round-trip
    /// harness.
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
