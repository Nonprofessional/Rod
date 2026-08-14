using System.Net;
using System.Net.Http.Headers;
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
/// Roadmap M6.3 acceptance: export a reproducible engagement timeline and report
/// (architecture.md Sec 11). Drives the full operational lifecycle -- engagement
/// created, stager token minted, implant enrolled, session opened, a task's
/// issued/dispatched/completed arc, an artifact attached -- and reads it back as
/// both an enriched timeline and a full report bundle (JSON and Markdown). The
/// exports are reproducible (a content hash stable across reads, moving when the
/// underlying facts move) and engagement-scoped (a foreign engagement is empty or
/// 404). The report is a read-only projection of the event + task + artifact
/// store -- the M6.1/M6.2 evidence rendered into the deliverable.
/// </summary>
public class TimelineAndReportTests
{
    [Fact]
    public async Task TimelineJson_IsEnriched_Ordered_AndScoped()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, owner, implantIdString) = await DriveLifecycleAsync(env);

        var timeline = await env.Http.GetFromJsonAsync<TimelineReport>(
            $"/engagements/{engagementId}/timeline");

        Assert.NotNull(timeline);
        Assert.Equal("Operation Smokeshow", timeline!.EngagementName);
        Assert.NotEmpty(timeline.ContentHash);

        var kinds = timeline.Entries.Select(e => e.Kind).ToArray();
        Assert.Contains("EngagementCreated", kinds);
        Assert.Contains("StagerTokenMinted", kinds);
        Assert.Contains("ImplantEnrolled", kinds);
        Assert.Contains("SessionOpened", kinds);
        Assert.Contains("TaskIssued", kinds);
        Assert.Contains("TaskDispatched", kinds);
        Assert.Contains("TaskCompleted", kinds);

        // Oldest-first: the trail reads in causal order.
        Assert.Equal(timeline.Entries.OrderBy(e => e.At).Select(e => e.EventId),
            timeline.Entries.Select(e => e.EventId));

        // Enrichment: the engagement-creation event attributes to the owner by
        // handle, not bare id; the task-issued event carries the task's verb and
        // the implant's class on its subject.
        var created = Assert.Single(timeline.Entries, e => e.Kind == "EngagementCreated");
        Assert.NotNull(created.Operator);
        Assert.Equal(AuthenticatedHost.Handle, created.Operator!.Handle);
        Assert.Equal(owner.Value, created.Operator.OperatorId);

        var issued = Assert.Single(timeline.Entries, e => e.Kind == "TaskIssued");
        Assert.NotNull(issued.Task);
        Assert.Equal("shell.exec", issued.Task!.Verb);
        Assert.NotNull(issued.Implant);
        Assert.Equal("Stage2", issued.Implant!.Class);

        // Each entry carries its hash-chain link hash -- the tamper-evident
        // anchor rides along on the projection.
        Assert.All(timeline.Entries, e => Assert.False(string.IsNullOrEmpty(e.Hash)));
    }

    [Fact]
    public async Task TimelineMarkdown_IsTextMarkdown_AndContainsTheFacts()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, _, _) = await DriveLifecycleAsync(env);

        var response = await env.Http.GetAsync($"/engagements/{engagementId}/timeline?format=markdown");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Operation Smokeshow", body);
        Assert.Contains("EngagementCreated", body);
        Assert.Contains("shell.exec", body);
        Assert.Contains("Integrity:", body);
    }

    [Fact]
    public async Task ReportJson_BundlesEveryEvidenceSurface()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, owner, implantIdString) = await DriveLifecycleAsync(env);

        var report = await env.Http.GetFromJsonAsync<EngagementReport>(
            $"/engagements/{engagementId}/report");

        Assert.NotNull(report);
        Assert.Equal("Operation Smokeshow", report!.Engagement.Name);
        Assert.Equal(AuthenticatedHost.Handle, report.Engagement.OwnerHandle);
        Assert.Equal(owner.Value, report.Engagement.OwnerId);
        Assert.NotEmpty(report.ContentHash);

        // Operator roster: the owner is present.
        var ownerEntry = Assert.Single(report.Operators, o => o.OperatorId == owner.Value);
        Assert.Equal(AuthenticatedHost.Handle, ownerEntry.Handle);

        // Implant inventory: the enrolled Stage-2 implant.
        var implantEntry = Assert.Single(report.Implants);
        Assert.Equal("Stage2", implantEntry.Class);
        Assert.Null(implantEntry.ParentImplantId);

        // Task history: the completed shell.exec carries its outcome, output, and
        // the artifact id bound to it.
        var task = Assert.Single(report.Tasks);
        Assert.Equal("shell.exec", task.Verb);
        Assert.Equal("Completed", task.Status);
        Assert.Equal("Succeeded", task.Outcome);
        Assert.Equal("red-team\\operator", task.Output);
        Assert.NotEmpty(task.Artifacts);

        // Artifact index: the attached evidence, metadata only (no bytes on the
        // DTO; bytes are fetched on demand through the retrieve endpoint).
        var artifact = Assert.Single(report.Artifacts);
        Assert.Equal("passwd.txt", artifact.Name);
        Assert.Equal("text/plain", artifact.ContentType);
        Assert.Contains(artifact.ArtifactId.ToString("N"), task.Artifacts);

        // The report's timeline section is the same enriched projection as the
        // standalone timeline -- the two exports agree on the trail.
        Assert.Contains(report.Timeline, e => e.Kind == "TaskCompleted");
    }

    [Fact]
    public async Task ReportMarkdown_RendersEverySection()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, _, _) = await DriveLifecycleAsync(env);

        var response = await env.Http.GetAsync($"/engagements/{engagementId}/report?format=markdown");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# Engagement report: Operation Smokeshow", body);
        Assert.Contains("## Operators", body);
        Assert.Contains("## Implants", body);
        Assert.Contains("## Tasks", body);
        Assert.Contains("## Artifacts", body);
        Assert.Contains("## Timeline", body);
        Assert.Contains(AuthenticatedHost.Handle, body);
        Assert.Contains("shell.exec", body);
        Assert.Contains("passwd.txt", body);
    }

    [Fact]
    public async Task ReportContentHash_IsStableAcrossReads_AndMovesWhenFactsChange()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, owner, implantIdString) = await DriveLifecycleAsync(env);

        // Two reads moments apart: the content hash excludes the wall-clock
        // generatedAt, so identical state yields an identical digest. This is the
        // AC's "reproducible".
        var first = await env.Http.GetFromJsonAsync<EngagementReport>(
            $"/engagements/{engagementId}/report");
        await Task.Delay(25);
        var second = await env.Http.GetFromJsonAsync<EngagementReport>(
            $"/engagements/{engagementId}/report");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.ContentHash, second!.ContentHash);
        Assert.NotEqual(first.GeneratedAt, second.GeneratedAt);

        var hashBefore = first.ContentHash;

        // The facts move: retire the implant (a new attributed event lands on the
        // trail, and the implant inventory gains a retirement timestamp). The
        // digest must change -- it covers the timeline, not a constant.
        var retire = await env.Http.PostAsync(
            $"/engagements/{engagementId}/implants/{implantIdString}:retire",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);

        var after = await env.Http.GetFromJsonAsync<EngagementReport>(
            $"/engagements/{engagementId}/report");
        Assert.NotNull(after);
        Assert.NotEqual(hashBefore, after!.ContentHash);
        Assert.Contains(after.Timeline, e => e.Kind == "ImplantRetired");
        Assert.NotNull(Assert.Single(after.Implants).RetiredAt);
    }

    [Fact]
    public async Task TimelineAndReport_Return404_ForUnknownEngagement()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);
        var foreign = Guid.NewGuid();

        var timeline = await env.Http.GetAsync($"/engagements/{foreign}/timeline");
        Assert.Equal(HttpStatusCode.NotFound, timeline.StatusCode);

        var report = await env.Http.GetAsync($"/engagements/{foreign}/report");
        Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);
    }

    [Fact]
    public async Task TimelineAndReport_Return400_ForMalformedEngagementId()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var timeline = await env.Http.GetAsync("/engagements/not-a-guid/timeline");
        Assert.Equal(HttpStatusCode.BadRequest, timeline.StatusCode);

        var report = await env.Http.GetAsync("/engagements/not-a-guid/report");
        Assert.Equal(HttpStatusCode.BadRequest, report.StatusCode);
    }

    // Drives the operational lifecycle far enough to populate every evidence
    // surface the report projects: engagement, stager token, enrollment, session,
    // a shell.exec task to completion, and an artifact attached to that task.
    // Returns the engagement id, the owner operator, and the implant id string
    // (the latter for the retire step in the hash-mutation test).
    private static async Task<(Guid EngagementId, OperatorId Owner, string ImplantId)> DriveLifecycleAsync(
        TestEnv env)
    {
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();

        await AuthenticatedHost.LoginAsync(env.Http);
        var owner = env.OperatorId;
        var created = await env.Http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
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

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId, Verb = "shell.exec", Arguments = "whoami" });
        issued.EnsureSuccessStatusCode();

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

        // Attach an artifact to the completed task so the evidence index and the
        // task's evidence references are both populated.
        var content = "root:x:0:0:root:/root:/bin/bash"u8.ToArray();
        var attached = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks/{request.TaskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "passwd.txt",
                ContentType: "text/plain",
                Content: content));
        attached.EnsureSuccessStatusCode();

        return (engagementId, owner, implantId);
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

    // --- DTO shadows for JSON deserialization. Mirrors the AuditEndpoints test, ---
    // --- which deserializes the wire shape rather than referencing the DTO.    ---

    private sealed class TimelineActor
    {
        public Guid OperatorId { get; set; }
        public string Handle { get; set; } = "";
    }

    private sealed class TimelineSubject
    {
        public Guid ImplantId { get; set; }
        public string Class { get; set; } = "";
    }

    private sealed class TimelineTaskRef
    {
        public Guid TaskId { get; set; }
        public string? Verb { get; set; }
        public string? Outcome { get; set; }
    }

    private sealed class TimelineEntry
    {
        public Guid EventId { get; set; }
        public DateTimeOffset At { get; set; }
        public string Kind { get; set; } = "";
        public string Verb { get; set; } = "";
        public TimelineActor? Operator { get; set; }
        public TimelineSubject? Implant { get; set; }
        public TimelineTaskRef? Task { get; set; }
        public string Payload { get; set; } = "";
        public string? Output { get; set; }
        public string Outcome { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private sealed class TimelineReport
    {
        public Guid EngagementId { get; set; }
        public string EngagementName { get; set; } = "";
        public DateTimeOffset GeneratedAt { get; set; }
        public string ContentHash { get; set; } = "";
        public List<TimelineEntry> Entries { get; set; } = new();
    }

    private sealed class ReportEngagement
    {
        public Guid EngagementId { get; set; }
        public string Name { get; set; } = "";
        public Guid OwnerId { get; set; }
        public string OwnerHandle { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class ReportOperator
    {
        public Guid OperatorId { get; set; }
        public string Handle { get; set; } = "";
    }

    private sealed class ReportImplant
    {
        public Guid ImplantId { get; set; }
        public string Class { get; set; } = "";
        public string? ParentImplantId { get; set; }
        public DateTimeOffset? RetiredAt { get; set; }
    }

    private sealed class ReportTask
    {
        public Guid TaskId { get; set; }
        public string Verb { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Outcome { get; set; }
        public Guid IssuedBy { get; set; }
        public string IssuedByHandle { get; set; } = "";
        public Guid ImplantId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DispatchedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Output { get; set; }
        public List<string> Artifacts { get; set; } = new();
    }

    private sealed class ReportArtifactIndexEntry
    {
        public Guid ArtifactId { get; set; }
        public Guid TaskId { get; set; }
        public string Name { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
    }

    private sealed class EngagementReport
    {
        public ReportEngagement Engagement { get; set; } = new();
        public DateTimeOffset GeneratedAt { get; set; }
        public string ContentHash { get; set; } = "";
        public List<ReportOperator> Operators { get; set; } = new();
        public List<ReportImplant> Implants { get; set; } = new();
        public List<ReportTask> Tasks { get; set; } = new();
        public List<ReportArtifactIndexEntry> Artifacts { get; set; } = new();
        public List<TimelineEntry> Timeline { get; set; } = new();
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the OperationalEventLogTests harness.
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
