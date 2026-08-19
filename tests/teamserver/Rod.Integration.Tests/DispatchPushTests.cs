using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Tasks;
using Rod.Transport;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for push dispatch (architecture.md Sec 10.3): the beacon
/// writer parks on the per-implant dispatch wake instead of polling the
/// queue, so an open, idle session costs nothing while nothing is queued, and
/// a task issued over HTTP while the stream is open is pushed downstream
/// immediately. A counting stand-in for the task repository observes the
/// claim rate: under the retired 25 ms poll an idle second logged ~40
/// claims, so the idle window here must log none.
/// </summary>
public class DispatchPushTests
{
    [Fact]
    public async Task IdleSession_ClaimsNothing_AndAnIssuedTaskIsPushed()
    {
        var tasks = new CountingTaskRepository(new InMemoryTaskRepository());
        await using var env = await TestEnv.StartAsync(tasks);
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

        // Open the beacon stream and complete the handshake first.
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // Let the writer's opening claim land, then watch an idle window. The
        // writer claims once per session and parks on the wake; the retired
        // poll would have logged ~24 claims in this window.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        var idle = tasks.Claims;

        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(idle, tasks.Claims);

        // A task issued over HTTP while the stream is open is pushed
        // downstream by the wake, not by a poll tick: the frame arrives with
        // nothing queued before it.
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        issued.EnsureSuccessStatusCode();

        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await call.ResponseStream.MoveNext(readDeadline.Token));
        var request = TaskRequest.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        Assert.Equal("shell.exec", request.Verb);
        Assert.Equal("whoami", request.Arguments);

        await call.RequestStream.CompleteAsync();
    }

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
    }

    private static Frame HandshakeFrame(ImplantId implant)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
            Capabilities = { "shell.exec" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    /// <summary>
    /// Delegates to the in-memory repository and counts dispatch claims -- the
    /// observable that separates push dispatch from a poll: a poll claims on a
    /// timer whether or not anything was queued, push claims only when the
    /// wake fires.
    /// </summary>
    private sealed class CountingTaskRepository : ITaskRepository
    {
        private readonly ITaskRepository _inner;
        private int _claims;

        public CountingTaskRepository(ITaskRepository inner) => _inner = inner;

        public int Claims => Volatile.Read(ref _claims);

        public async System.Threading.Tasks.Task<Rod.CoreState.Tasks.Task?> ClaimNextPendingAsync(
            ImplantId implant, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claims);
            return await _inner.ClaimNextPendingAsync(implant, at, cancellationToken);
        }

        public System.Threading.Tasks.Task SaveAsync(
            Rod.CoreState.Tasks.Task task, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(task, cancellationToken);

        public System.Threading.Tasks.Task<Rod.CoreState.Tasks.Task?> FindAsync(
            TaskId id, CancellationToken cancellationToken = default)
            => _inner.FindAsync(id, cancellationToken);

        public System.Threading.Tasks.Task<IReadOnlyList<Rod.CoreState.Tasks.Task>> ListByImplantAsync(
            ImplantId implant, CancellationToken cancellationToken = default)
            => _inner.ListByImplantAsync(implant, cancellationToken);

        public System.Threading.Tasks.Task<IReadOnlyList<Rod.CoreState.Tasks.Task>> ListByEngagementAsync(
            EngagementId engagement, CancellationToken cancellationToken = default)
            => _inner.ListByEngagementAsync(engagement, cancellationToken);

        public System.Threading.Tasks.Task<TaskPage> ListByEngagementPageAsync(
            EngagementId engagement, int limit, string? cursor, CancellationToken cancellationToken = default)
            => _inner.ListByEngagementPageAsync(engagement, limit, cursor, cancellationToken);

        public System.Threading.Tasks.Task<TaskPage> ListByImplantPageAsync(
            ImplantId implant, int limit, string? cursor, CancellationToken cancellationToken = default)
            => _inner.ListByImplantPageAsync(implant, limit, cursor, cancellationToken);

        public System.Threading.Tasks.Task<Rod.CoreState.Tasks.Task?> NextPendingAsync(
            ImplantId implant, CancellationToken cancellationToken = default)
            => _inner.NextPendingAsync(implant, cancellationToken);

        public System.Threading.Tasks.Task<ulong> NextNonceAsync(
            ImplantId implant, CancellationToken cancellationToken = default)
            => _inner.NextNonceAsync(implant, cancellationToken);
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the task round-trip harness, with the
    /// counting task repository layered in so the test can read the claim
    /// count.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync(ITaskRepository tasks)
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(
                        services, config, extra: s => s.AddSingleton<ITaskRepository>(tasks)),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

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
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
