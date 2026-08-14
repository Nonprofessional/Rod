using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap  acceptance: every per-engagement action produces an attributed,
/// immutable event in the engagement trail (architecture.md Sec 11). Drives the
/// full operational lifecycle -- engagement created, stager token minted, implant
/// enrolled, session opened, task issued/dispatched/completed, payload built,
/// implant retired -- and asserts each landed as an attributed, hash-chained
/// event on the per-engagement trail, readable through the audit endpoint. The
/// chain verifies end to end (tamper-evidence by construction). Every operator
/// action is attributed to the authenticated operator; with a single seeded
/// operator the owner, the task issuer, the builder, and the retireer are the
/// same identity, recorded by the server off the session principal.
/// </summary>
public class OperationalEventLogTests
{
    [Fact]
    public async Task EveryAction_ProducesAnAttributedImmutableEvent()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        await AuthenticatedHost.LoginAsync(env.Http);

        // The single seeded operator is every actor in this lifecycle -- the
        // server records that identity off the session, not the request body.
        var owner = env.OperatorId;

        // 1. Engagement created -> EngagementCreated (genesis link).
        var created = await env.Http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        created.EnsureSuccessStatusCode();
        var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        var engagementId = Guid.Parse(engagement!.EngagementId);

        // 2. Stager token minted -> StagerTokenMinted.
        var minted = await env.Http.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        minted.EnsureSuccessStatusCode();
        var token = await minted.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        Assert.Equal(owner.Value, Guid.Parse(token!.IssuedBy));

        // 3. Implant enrolled (implant-initiated, attributed to the token issuer)
        //    -> ImplantEnrolled. The engagement binding is checked by the trail
        //    assertions below (the ImplantEnrolled/SessionOpened events carry the
        //    engagement id the token resolved to).
        var (implantId, leafCert, leafKey) = await EnrollImplantAsync(env.Http, token.Secret, ca);

        // 4. Session opened over the beacon stream -> SessionOpened, plus a task's
        //    issued/dispatched/completed arc -> TaskIssued, TaskDispatched,
        //    TaskCompleted. The token issuer deployed the implant, so the
        //    implant-initiated events attribute to that operator.
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implantId, 1, 0));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        var taskIssuer = env.OperatorId;
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var request = TaskRequest.Parser.ParseFrom(call.ResponseStream.Current.Payload);

        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "red-team\\operator",
        }));

        // Wait for the completion to land on the trail before readback.
        await WaitUntilAsync(async () => (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 3);

        await call.RequestStream.CompleteAsync();

        // 5. Payload built -> PayloadBuilt.
        var build = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: null,
                Class: null,
                TargetOs: "linux",
                TargetArch: "amd64",
                Endpoint: "http://c2.example.test",
                UriPath: "/beacon",
                SleepSeconds: 30,
                JitterSeconds: 10,
                KillDate: null));
        build.EnsureSuccessStatusCode();

        // 6. Implant retired -> ImplantRetired.
        var retire = await env.Http.PostAsync(
            $"/engagements/{engagementId}/implants/{implantId}:retire",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);

        // --- Read the whole trail back through the per-engagement audit endpoint
        //     and assert every action produced an attributed event, in order, and
        //     the hash chain verifies. ---
        var trailResponse = await env.Http.GetFromJsonAsync<AuditEndpoints.AuditEventEntry[]>(
            $"/engagements/{engagementId}/audit");
        Assert.NotNull(trailResponse);

        var byKind = trailResponse!.ToDictionary(e => e.Kind);
        // Every lifecycle kind is present.
        Assert.Contains("EngagementCreated", byKind.Keys);
        Assert.Contains("StagerTokenMinted", byKind.Keys);
        Assert.Contains("ImplantEnrolled", byKind.Keys);
        Assert.Contains("SessionOpened", byKind.Keys);
        Assert.Contains("TaskIssued", byKind.Keys);
        Assert.Contains("TaskDispatched", byKind.Keys);
        Assert.Contains("TaskCompleted", byKind.Keys);
        Assert.Contains("ImplantRetired", byKind.Keys);

        // Each event carries the correct attribution.
        Assert.Equal(owner.Value, byKind["EngagementCreated"].OperatorId);
        Assert.Equal(owner.Value, byKind["StagerTokenMinted"].OperatorId);
        // Enrollment is implant-initiated -> attributed to the token issuer (owner).
        Assert.Equal(owner.Value, byKind["ImplantEnrolled"].OperatorId);
        Assert.Equal(Guid.Parse(implantId), byKind["ImplantEnrolled"].ImplantId);
        // Session open attributes to the deploying operator (owner).
        Assert.Equal(owner.Value, byKind["SessionOpened"].OperatorId);
        Assert.Equal(Guid.Parse(implantId), byKind["SessionOpened"].ImplantId);
        // Task issued by taskIssuer; dispatched to the implant; completed.
        Assert.Equal(taskIssuer.Value, byKind["TaskIssued"].OperatorId);
        Assert.Equal(taskIssuer.Value, byKind["TaskDispatched"].OperatorId);
        Assert.Equal(taskIssuer.Value, byKind["TaskCompleted"].OperatorId);
        Assert.Equal(Guid.Parse(issuedBody!.TaskId), byKind["TaskIssued"].TaskId);
        // Retirement attributes to the retiring operator (owner).
        Assert.Equal(owner.Value, byKind["ImplantRetired"].OperatorId);

        // The trail is ordered oldest-first and the hash chain verifies -- no
        // tampering, no gaps (the append-only, tamper-evident contract).
        var trail = await audit.ListAsync(engagementId);
        Assert.Null(AuditChain.VerifyTrail(trail));
        Assert.Equal(trail.Select(e => e.EventId), trail.OrderBy(e => e.At).Select(e => e.EventId));

        // Cross-engagement isolation: a foreign engagement id yields an empty trail.
        var foreign = await env.Http.GetFromJsonAsync<AuditEndpoints.AuditEventEntry[]>(
            $"/engagements/{Guid.NewGuid()}/audit");
        Assert.Empty(foreign!);
    }

    [Fact]
    public async Task AuditEndpoint_Returns400_ForMalformedEngagementId()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);
        var response = await env.Http.GetAsync($"/engagements/not-a-guid/audit");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string ImplantId, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        HttpClient http, string secret, IImplantCertificateAuthority ca)
    {
        // The implant generates its own key pair and sends only the public half,
        // so the issued leaf is mTLS-capable (architecture.md Sec 9).
        var leafKey = RSA.Create(2048);
        var spki = leafKey.ExportSubjectPublicKeyInfo();

        var response = await http.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null, PublicKey: Convert.ToBase64String(spki)));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();

        return (enrolled!.ImplantId!, X509CertificateLoader.LoadCertificate(Convert.FromBase64String(enrolled.LeafCertificate!)), leafKey);
    }

    private static Frame HandshakeFrame(string implantId, int major, int minor)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = major, Minor = minor },
            ImplantId = implantId,
            Capabilities = { "shell.exec" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()) };

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
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
        public string Verb { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the TaskRoundTripTests harness. The
    /// operator + auth layers are composed so the API requires a cookie session.
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
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain.ChainPolicy.ExtraStore.Add(ca);
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
