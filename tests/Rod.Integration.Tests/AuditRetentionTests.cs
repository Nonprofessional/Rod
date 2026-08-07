using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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
/// Roadmap M6.4 acceptance: the engagement trail survives infrastructure
/// teardown. Drives the full operational lifecycle on a teamserver bound to a
/// durable audit/artifact store, then tears that teamserver down (stops the host
/// -- the in-memory core state, the listeners, the live bus all vanish) and
/// starts a fresh one pointed at the same data directory. The audit trail and
/// the attached artifact must come back whole, the chain must still verify, and
/// a brand-new engagement must start an independent trail (no bleed). This is
/// the M6.4 AC: tear down an engagement's infra; its audit trail remains
/// (architecture.md Sec 11; lifecycle step 10).
/// </summary>
public class AuditRetentionTests
{
    [Fact]
    public async Task AuditTrail_SurvivesInfraTeardown_AndVerifies()
    {
        using var dataDir = new TempDir();

        // --- Host A: the running operation. Durable stores are selected by the
        //     Audit:DataDirectory section, so everything written here lands on
        //     disk. ---
        Guid engagementId;
        OperatorId owner;
        OperatorId taskIssuer;
        string implantId;
        string artifactId;
        byte[] artifactBytes;
        int trailCountAfterA;

        await using (var envA = await TestEnv.StartAsync(dataDir.Path))
        {
            var (engagement, ownerOp, issuer, implant, _) = await DriveLifecycleAsync(envA);
            engagementId = engagement;
            owner = ownerOp;
            taskIssuer = issuer;
            implantId = implant;

            // Attach an artifact so evidence retention is exercised alongside the
            // trail (M6.2 evidence must outlive teardown too).
            (artifactId, artifactBytes) = await AttachArtifactAsync(envA.Http, engagementId, owner);

            // Give the retire + attach audit writes a moment to flush to disk
            // (each append flushes; this is belt-and-braces for the read-back).
            var audit = envA.Host.Services.GetRequiredService<IAuditStore>();
            await WaitUntilAsync(async () =>
            {
                var trail = await audit.ListAsync(engagementId);
                return trail.Any(e => e.Kind == AuditEventKind.ArtifactAttached);
            });

            trailCountAfterA = (await audit.ListAsync(engagementId)).Count;
            Assert.True(trailCountAfterA > 0);
        }
        // Host A is now disposed: its process, listeners, and in-memory core
        // state are gone. Only the durable audit/artifact files remain.

        // --- Host B: a fresh teamserver over the same data directory. Its
        //     in-memory state is empty -- no engagement, no implant -- but the
        //     recovered audit store holds the whole trail from A. ---
        await using var envB = await TestEnv.StartAsync(dataDir.Path);
        var recoveredAudit = envB.Host.Services.GetRequiredService<IAuditStore>();
        var recoveredArtifacts = envB.Host.Services.GetRequiredService<IArtifactStore>();

        // The trail reads back through the per-engagement audit endpoint on the
        // new host, oldest-first, every kind present and correctly attributed.
        var trailResponse = await envB.Http.GetFromJsonAsync<AuditEndpoints.AuditEventEntry[]>(
            $"/engagements/{engagementId}/audit");
        Assert.NotNull(trailResponse);
        Assert.Equal(trailCountAfterA, trailResponse!.Length);

        var byKind = trailResponse.ToDictionary(e => e.Kind);
        Assert.Contains("EngagementCreated", byKind.Keys);
        Assert.Contains("StagerTokenMinted", byKind.Keys);
        Assert.Contains("ImplantEnrolled", byKind.Keys);
        Assert.Contains("SessionOpened", byKind.Keys);
        Assert.Contains("TaskIssued", byKind.Keys);
        Assert.Contains("TaskDispatched", byKind.Keys);
        Assert.Contains("TaskCompleted", byKind.Keys);
        Assert.Contains("ImplantRetired", byKind.Keys);
        Assert.Contains("ArtifactAttached", byKind.Keys);

        // Attribution survived the teardown: every operator-bound event still
        // points at the operator who acted.
        Assert.Equal(owner.Value, byKind["EngagementCreated"].OperatorId);
        Assert.Equal(owner.Value, byKind["ImplantEnrolled"].OperatorId);
        Assert.Equal(taskIssuer.Value, byKind["TaskIssued"].OperatorId);

        // Oldest-first ordering survived the reload.
        Assert.Equal(
            trailResponse.Select(e => e.EventId),
            trailResponse.OrderBy(e => e.At).Select(e => e.EventId));

        // The hash chain still verifies after the teardown -- the reloaded trail
        // is tamper-evident across the restart, not just within one host. This is
        // the core of the M6.4 contract.
        var trail = await recoveredAudit.ListAsync(engagementId);
        Assert.Null(AuditChain.VerifyTrail(trail));

        // The attached artifact is retrievable by id on the new host with its
        // exact bytes -- evidence linked to a task survived alongside the trail.
        var recoveredArtifact = await recoveredArtifacts.FindAsync(Guid.Parse(artifactId));
        Assert.NotNull(recoveredArtifact);
        Assert.Equal(artifactBytes, recoveredArtifact!.Content);

        // No bleed: a brand-new engagement on host B starts an empty, independent
        // trail. The recovered head map keeps engagements isolated across the
        // teardown.
        var foreignTrail = await recoveredAudit.ListAsync(Guid.NewGuid());
        Assert.Empty(foreignTrail);

        var foreignResponse = await envB.Http.GetFromJsonAsync<AuditEndpoints.AuditEventEntry[]>(
            $"/engagements/{Guid.NewGuid()}/audit");
        Assert.Empty(foreignResponse!);
    }

    // Drives the full M6.1 lifecycle (mirrors OperationalEventLogTests) so the
    // reloaded trail has every kind to verify against. Returns the engagement id,
    // the owner, the task issuer, and the enrolled implant id.
    private static async Task<(Guid EngagementId, OperatorId Owner, OperatorId TaskIssuer, string ImplantId, string TaskId)>
        DriveLifecycleAsync(TestEnv env)
    {
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();

        var owner = OperatorId.New();
        var created = await env.Http.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: owner.Value,
            OwnerHandle: "cneale",
            OwnerDisplayName: "Cecil Neale",
            Name: "Operation Smokeshow"));
        created.EnsureSuccessStatusCode();
        var engagement = await created.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        var engagementId = Guid.Parse(engagement!.EngagementId);

        var minted = await env.Http.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        minted.EnsureSuccessStatusCode();
        var token = await minted.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();

        var (implantId, leafCert, leafKey) = await EnrollImplantAsync(env.Http, token!.Secret, ca);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implantId, 1, 0));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        var taskIssuer = OperatorId.New();
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId, IssuedBy = taskIssuer.Value, Verb = "shell.exec", Arguments = "whoami" });
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

        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        await WaitUntilAsync(async () => (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 3);

        await call.RequestStream.CompleteAsync();

        // Retire the implant (M4.4): the ImplantRetired event joins the trail.
        var retire = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/implants/{implantId}:retire",
            new ImplantEndpoints.RetireImplantRequest(RetiredBy: owner.Value));
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);

        return (engagementId, owner, taskIssuer, implantId, issuedBody!.TaskId);
    }

    // Attaches a single artifact to the lifecycle's task and returns its id and
    // bytes so the retention test can verify the evidence came back intact.
    private static async Task<(string ArtifactId, byte[] Bytes)> AttachArtifactAsync(
        HttpClient http, Guid engagementId, OperatorId attacher)
    {
        var taskId = await FirstTaskIdAsync(http, engagementId);
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var attach = await http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                AttachedBy: attacher.Value,
                Name: "loot.bin",
                ContentType: "application/octet-stream",
                Content: bytes));
        attach.EnsureSuccessStatusCode();
        var attached = await attach.Content.ReadFromJsonAsync<ArtifactEndpoints.ArtifactResponse>();

        return (attached!.ArtifactId, bytes);
    }

    // The lifecycle's first task id, read off the audit trail's TaskIssued event.
    private static async Task<Guid> FirstTaskIdAsync(HttpClient http, Guid engagementId)
    {
        var trail = await http.GetFromJsonAsync<AuditEndpoints.AuditEventEntry[]>(
            $"/engagements/{engagementId}/audit");
        var issued = Assert.Single(trail!, e => e.Kind == "TaskIssued");
        return issued.TaskId;
    }

    private static async Task<(string ImplantId, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        HttpClient http, string secret, IImplantCertificateAuthority ca)
    {
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "rod-retention-test-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path))
                System.IO.Directory.Delete(Path, recursive: true);
        }
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint and a plain-HTTP
    /// operator API, bound to a durable audit/artifact store at <paramref name="dataDirectory"/>.
    /// Two instances over the same directory exercise teardown-and-restart.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync(string dataDirectory)
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            // The Audit:DataDirectory section selects the file-backed stores
            // (roadmap M6.4). In-memory config mirrors what appsettings.json
            // supplies for the real host, so the test does not depend on a file.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Audit:DataDirectory"] = dataDirectory,
                })
                .Build();

            env.Host = TransportHost.CreateHostBuilder(configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}") };
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
