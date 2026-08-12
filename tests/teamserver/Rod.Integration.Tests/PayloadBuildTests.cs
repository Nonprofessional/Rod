using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M3.1 acceptance: requesting a payload invokes a build unit and returns
/// an artifact, fingerprinted and recorded. Drives the full slice end-to-end
/// through the in-memory TestServer -- the operator POSTs a build request, the
/// build pipeline invokes the in-tree .NET build unit (ADR 0009; the real
/// reference unit for the .NET slot), and the operator gets back a fingerprinted
/// artifact while a PayloadBuilt audit event is appended to the engagement's
/// hash-chained trail.
/// The build service is audit-agnostic by design; the transport endpoint composes
/// the recording (architecture.md Sec 6, Sec 11), the same way the beacon stream
/// records task completion. The requesting operator is the logged-in operator,
/// recorded by the server off the session principal, so the audit attributes the
/// build to that identity regardless of the request body.
/// </summary>
public class PayloadBuildTests
{
    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    [DotNetFact]
    public async Task BuildPayload_InvokesBuildUnit_ReturnsFingerprintedArtifact()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    Language: "DotNet",
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
            Assert.Equal("DotNet", body.Language);
            Assert.False(string.IsNullOrWhiteSpace(body.ContentType));
            Assert.True(body.Size > 0);
            // SHA-256 lowercase hex, 64 chars.
            Assert.Equal(64, body.Fingerprint.Length);
            Assert.True(body.Fingerprint.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'));
            // The Location is the new artifact's URI (relative in TestServer).
            Assert.EndsWith($"/payloads/{body.ArtifactId}", response.Headers.Location!.ToString());
        }
    }

    [DotNetFact]
    public async Task TwoBuilds_WithIdenticalRequest_ProduceDifferentArtifacts()
    {
        // Per-implant material is generated at request time, so two builds of the
        // same request never share a key and never share a fingerprint
        // (architecture.md Sec 6/Sec 5.1).
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var first = await PostBuildAsync(client, engagementId);
            var second = await PostBuildAsync(client, engagementId);

            Assert.NotEqual(first!.ArtifactId, second!.ArtifactId);
            Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        }
    }

    private static async Task<PayloadEndpoints.BuildPayloadResponse?> PostBuildAsync(
        HttpClient client, string engagementId)
    {
        var response = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new PayloadEndpoints.BuildPayloadRequest(
                Language: "DotNet",
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

    [DotNetFact]
    public async Task BuildPayload_RecordsPayloadBuiltAuditEvent_OnTheChain()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var audit = host.Services.GetRequiredService<IAuditStore>();

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    Language: "DotNet",
                    Class: "Stage2",
                    TargetOs: "linux",
                    TargetArch: "amd64",
                    Endpoint: "http://c2.example.test",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null));
            var body = await response.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();

            // The build is recorded: the engagement's trail holds a PayloadBuilt
            // event carrying the class and the artifact's fingerprint, and the
            // chain is intact. The trail also carries the engagement's own
            // creation event (M6.1 genesis), so it is no longer a single entry.
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var evt = Assert.Single(trail, e => e.Kind == AuditEventKind.PayloadBuilt);
            Assert.Equal("Stage2", evt.Verb);
            Assert.Equal(body!.Fingerprint, evt.Outcome);
            // The audit attributes the build to the authenticated operator, not any
            // client-supplied identity.
            Assert.Equal(operatorId.Value, evt.OperatorId);
            Assert.Null(AuditChain.VerifyTrail(trail));
        }
    }

    [Fact]
    public async Task BuildPayload_Returns400_ForMalformedEngagementId()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            var response = await client.PostAsJsonAsync(
                "/engagements/not-a-guid/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
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
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
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
