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
/// Acceptance: an unmodified operator-side tool reaches a third host through
/// a stage-2 implant's tunnel without a per-byte API call, and the flow is
/// attributed end to end (architecture.md Sec 5.2, Sec 10.1 tunnel, Sec 14).
/// The operator binds a teamserver-side relay port onto a dispatched
/// <c>tunnel.forward</c> channel; a plain TCP client -- no Rod API, no
/// protobuf, nothing but a socket -- connects to the relay, and its bytes
/// cross the channel the signed TaskRequest opened exactly as posted input
/// would: down as ChannelInput frames, back as ChannelOutput the relay hands
/// the socket raw. The trail carries the arc without a single ChannelInput
/// event, which is the point -- the tool never touched the operator API.
/// </summary>
public class RelayBindRoundTripTests
{
    [Fact]
    public async Task RelayBind_CarriesAnUnmodifiedToolThroughTheTunnel_AndAttributesTheFlow()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // The third host: reachable only from the implant's vantage. The
        // operator-side tool below never opens a socket to it -- everything
        // crosses the tunnel.
        await using var thirdHost = EchoHost.Start();
        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Stage2);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "tunnel.forward"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // The tunnel opens like any other task; the implant bridges it to a
        // TCP connection of its own to the third host.
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

        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("tunnel.forward", request.Verb);
        using var tunnel = new TcpClient();
        await tunnel.ConnectAsync(IPAddress.Loopback, thirdHost.Port);
        var peerStream = tunnel.GetStream();

        // The one operator action besides issuing the task: bind the relay.
        // Loopback by default, an ephemeral port, one relay per task.
        var bound = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/relay",
            new { });
        bound.EnsureSuccessStatusCode();
        var relay = await bound.Content.ReadFromJsonAsync<RelayBody>();
        Assert.NotNull(relay);
        Assert.Equal(request.TaskId, relay!.TaskId);
        Assert.True(relay.Port > 0);

        // The unmodified tool: a bare TCP client. It knows nothing of the
        // engagement, the task, or the API -- it speaks to a port, and the
        // tunnel does the rest.
        using var tool = new TcpClient();
        await tool.ConnectAsync(IPAddress.Loopback, relay.Port);
        var toolStream = tool.GetStream();
        await toolStream.WriteAsync(Encoding.UTF8.GetBytes("ping"));

        // The tool's bytes arrive on the channel as ChannelInput -- the relay
        // drove them through the same enqueue the input route uses -- and the
        // implant relays them to the third host, whose answer streams back.
        var input = await NextChannelInputAsync(call, request.TaskId);
        Assert.Equal("ping", Encoding.UTF8.GetString(input.Data.Span));
        await peerStream.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        var buffer = new byte[16 * 1024];
        var echoed = await peerStream.ReadAsync(buffer);
        Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, echoed));
        await call.RequestStream.WriteAsync(OutputFrame(request.TaskId, "ping"));

        // The answer lands on the tool's socket raw -- the bytes the channel
        // carried, not a transcript projection of them.
        var received = await ReadAllAsync(toolStream, 4);
        Assert.Equal("ping", Encoding.UTF8.GetString(received));

        // The tool half-closes: eof rides the channel the same way, the
        // implant half-closes the tunnel, the third host ends its side, and
        // the tunnel's final TaskResult closes the task and the relay.
        tool.Client.Shutdown(SocketShutdown.Send);
        var eof = await NextChannelInputAsync(call, request.TaskId);
        Assert.True(eof.Eof);
        tunnel.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await peerStream.ReadAsync(buffer));
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "tunnel closed: relayed 4 bytes up, 4 bytes down",
        }));

        // The attributed arc: issued, dispatched, bound, completed, closed --
        // and not one ChannelInput event, because the tool never posted any.
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 5);
        var kinds = (await audit.ForTaskAsync(Guid.Parse(request.TaskId)))
            .Select(e => e.Kind.ToString())
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "RelayBound", "RelayClosed", "TaskCompleted", "TaskDispatched", "TaskIssued" },
            kinds);

        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}");
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("pingtunnel closed: relayed 4 bytes up, 4 bytes down", fetched.Output);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task RelayBind_IsRefusedForAnythingButADispatchedTunnel()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        var (implant, _, _) = await EnrollImplantAsync(implants, ca, clock, ImplantClass.Stage2);

        await AuthenticatedHost.LoginAsync(env.Http);

        // A shell task is not a tunnel: the relay is tunnel-only, so the bind
        // is a well-formed refusal whatever the task's state.
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new
            {
                ImplantId = implant.Id.ToString(),
                Verb = "shell.exec",
                Arguments = "true",
            });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        var shellBind = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{issuedBody!.TaskId}/relay",
            new { });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)shellBind.StatusCode);

        // An unknown task has nothing to bridge.
        var missing = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{Guid.NewGuid()}/relay",
            new { });
        Assert.Equal(StatusCodes.Status404NotFound, (int)missing.StatusCode);

        // Unbinding a task with no relay is the same 404.
        var unbind = await env.Http.DeleteAsync(
            $"/engagements/{implant.EngagementId}/tasks/{Guid.NewGuid()}/relay");
        Assert.Equal(StatusCodes.Status404NotFound, (int)unbind.StatusCode);

        // A malformed address is a routing-level refusal.
        var badAddress = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{issuedBody.TaskId}/relay",
            new { BindAddress = "not-an-address" });
        Assert.Equal(StatusCodes.Status400BadRequest, (int)badAddress.StatusCode);
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

    // Reads exactly count bytes; a relay delivering the tool's answer is a
    // byte-exact bridge, and a short read would hide a split delivery.
    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, int count)
    {
        var received = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            using var deadline = new CancellationTokenSource(ReadDeadline);
            var read = await stream.ReadAsync(received.AsMemory(offset), deadline.Token);
            if (read <= 0)
                throw new IOException("the relay closed the socket before the answer arrived");
            offset += read;
        }
        return received;
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
