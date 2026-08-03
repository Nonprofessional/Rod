using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M3.1 acceptance: requesting a payload invokes a build unit and returns
/// an artifact, fingerprinted and recorded. Drives the full slice end-to-end
/// through the in-memory TestServer -- the operator POSTs a build request, the
/// build pipeline invokes the language's build unit (the real Go unit from M3.2
/// for the Go slot), and the operator gets back a fingerprinted artifact while a
/// PayloadBuilt audit event is appended to the engagement's hash-chained trail.
/// The build service is audit-agnostic by design; the transport endpoint composes
/// the recording (architecture.md Sec 6, Sec 11), the same way the beacon stream
/// records task completion.
/// </summary>
public class PayloadBuildTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        IHost host = TransportHost.CreateHostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return (client, host);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: Guid.NewGuid(),
            OwnerHandle: "cneale",
            OwnerDisplayName: "Casey Neale",
            Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    [GoFact]
    public async Task BuildPayload_InvokesBuildUnit_ReturnsFingerprintedArtifact()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    RequestedBy: Guid.NewGuid(),
                    Language: "Go",
                    Class: "Stage2",
                    TargetOs: "linux",
                    TargetArch: "amd64",
                    Endpoint: "http://c2.example.test",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();
            Assert.NotNull(body);
            Assert.False(string.IsNullOrWhiteSpace(body!.ArtifactId));
            Assert.Equal("Stage2", body.Class);
            Assert.Equal("Go", body.Language);
            Assert.False(string.IsNullOrWhiteSpace(body.ContentType));
            Assert.True(body.Size > 0);
            // SHA-256 lowercase hex, 64 chars.
            Assert.Equal(64, body.Fingerprint.Length);
            Assert.True(body.Fingerprint.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'));
            // The Location is the new artifact's URI (relative in TestServer).
            Assert.EndsWith($"/payloads/{body.ArtifactId}", response.Headers.Location!.ToString());
        }
    }

    [GoFact]
    public async Task TwoBuilds_WithIdenticalRequest_ProduceDifferentArtifacts()
    {
        // Per-implant material is generated at request time, so two builds of the
        // same request never share a key and never share a fingerprint
        // (architecture.md Sec 6/Sec 5.1).
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var operatorId = Guid.NewGuid();

            var first = await PostBuildAsync(client, engagementId, operatorId);
            var second = await PostBuildAsync(client, engagementId, operatorId);

            Assert.NotEqual(first!.ArtifactId, second!.ArtifactId);
            Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        }
    }

    private static async Task<PayloadEndpoints.BuildPayloadResponse?> PostBuildAsync(
        HttpClient client, string engagementId, Guid operatorId)
    {
        var response = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                RequestedBy: operatorId,
                Language: "Go",
                Class: "Stage2",
                TargetOs: "linux",
                TargetArch: "amd64",
                Endpoint: "http://c2.example.test",
                UriPath: "/beacon",
                SleepSeconds: 30,
                JitterSeconds: 10,
                KillDate: null));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();
    }

    [GoFact]
    public async Task BuildPayload_RecordsPayloadBuiltAuditEvent_OnTheChain()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var audit = host.Services.GetRequiredService<IAuditStore>();

            var operatorId = Guid.NewGuid();
            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    RequestedBy: operatorId,
                    Language: "Go",
                    Class: "Stage2",
                    TargetOs: "linux",
                    TargetArch: "amd64",
                    Endpoint: "http://c2.example.test",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null));
            var body = await response.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();

            // The build is recorded: the engagement's trail holds one PayloadBuilt
            // event carrying the class and the artifact's fingerprint, and the
            // chain is intact.
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var evt = Assert.Single(trail);
            Assert.Equal(AuditEventKind.PayloadBuilt, evt.Kind);
            Assert.Equal("Stage2", evt.Verb);
            Assert.Equal(body!.Fingerprint, evt.Outcome);
            Assert.Null(AuditChain.VerifyTrail(trail));
        }
    }

    [Fact]
    public async Task BuildPayload_Returns400_ForMalformedEngagementId()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.PostAsJsonAsync(
                "/engagements/not-a-guid/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    RequestedBy: Guid.NewGuid(),
                    Language: null,
                    Class: null,
                    TargetOs: null,
                    TargetArch: null,
                    Endpoint: null,
                    UriPath: null,
                    SleepSeconds: null,
                    JitterSeconds: null,
                    KillDate: null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task BuildPayload_Returns400_ForUnknownLanguage()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    RequestedBy: Guid.NewGuid(),
                    Language: "Rust", // not a registered build language
                    Class: null,
                    TargetOs: null,
                    TargetArch: null,
                    Endpoint: null,
                    UriPath: null,
                    SleepSeconds: null,
                    JitterSeconds: null,
                    KillDate: null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
