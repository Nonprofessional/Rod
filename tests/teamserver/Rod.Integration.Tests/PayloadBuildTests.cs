using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: requesting a payload invokes a build unit and returns
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
                    Endpoint: "https://c2.example.test",
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

    [Fact]
    public async Task BuildPayload_RefusesAnEndpointTheImplantCannotDial()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            // The endpoint list is what the baked implant dials: a malformed
            // entry must fail the request, not the build -- a payload that
            // phones nowhere is the silent failure an operator discovers on
            // target.
            var garbage = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    Language: "DotNet",
                    Class: "Stage2",
                    TargetOs: "linux",
                    TargetArch: "amd64",
                    Endpoint: "not a url",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null));
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

            // A malformed fallback entry is refused the same way, even when
            // the primary endpoint is fine.
            var garbageFallback = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                new PayloadEndpoints.BuildPayloadRequest(
                    Language: "DotNet",
                    Class: "Stage2",
                    TargetOs: "linux",
                    TargetArch: "amd64",
                    Endpoint: "https://c2.example.test",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null,
                    FallbackEndpoints: new List<string> { "http://alt.example.test", "666" }));
            Assert.Equal(HttpStatusCode.BadRequest, garbageFallback.StatusCode);
        }
    }

    [DotNetFact]
    public async Task BuiltPayload_IsRetrievableFromItsLocation()
    {
        // The build response's Location resolves to a real download route: the
        // operator retrieves the compiled bytes, engagement-scoped, with a
        // content disposition naming the payload.
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var built = await PostBuildAsync(client, engagementId);
            Assert.NotNull(built);

            var download = await client.GetAsync($"/engagements/{engagementId}/payloads/{built!.ArtifactId}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(built.ContentType, download.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("attachment;", download.Content.Headers.ContentDisposition!.ToString());
            var bytes = await download.Content.ReadAsByteArrayAsync();
            Assert.Equal(built.Size, bytes.Length);
        }
    }

    [DotNetFact]
    public async Task PayloadLibrary_ListsAndDeletes_WithAnAuditFact()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var audit = host.Services.GetRequiredService<IAuditStore>();

            var built = await PostBuildAsync(client, engagementId);

            // The library row carries the build's own metadata -- target,
            // endpoint, and the baked credential's id for revocation -- so the
            // durable store reads like the build that made it.
            var listed = await client.GetFromJsonAsync<PayloadEndpoints.PayloadSummaryResponse[]>(
                $"/engagements/{engagementId}/payloads");
            var row = Assert.Single(listed!);
            Assert.Equal(built!.ArtifactId, row.ArtifactId);
            Assert.Equal("linux/amd64", row.Target);
            Assert.Equal("https://c2.example.test", row.Endpoint);
            Assert.Equal(built.TokenId, row.TokenId);

            // Deleting is the kill switch for hosted bytes: the row and the
            // download are gone, a second delete 404s, and the trail records
            // what was removed by whom.
            var deleted = await client.DeleteAsync($"/engagements/{engagementId}/payloads/{built.ArtifactId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            var emptied = await client.GetFromJsonAsync<PayloadEndpoints.PayloadSummaryResponse[]>(
                $"/engagements/{engagementId}/payloads");
            Assert.NotNull(emptied);
            Assert.Empty(emptied!);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/engagements/{engagementId}/payloads/{built.ArtifactId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.DeleteAsync($"/engagements/{engagementId}/payloads/{built.ArtifactId}")).StatusCode);

            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var deletion = Assert.Single(trail, e => e.Kind == AuditEventKind.PayloadDeleted);
            Assert.Equal("payload.delete", deletion.Verb);
            Assert.Equal(built.Fingerprint, deletion.Outcome);
            Assert.Null(AuditChain.VerifyTrail(trail));
        }
    }

    [Fact]
    public async Task BuiltPayload_IsInvisibleToAnotherEngagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var first = await CreateEngagementAsync(client);
            var second = await CreateEngagementAsync(client);

            var built = await PostBuildAsync(client, first);

            var download = await client.GetAsync($"/engagements/{second}/payloads/{built!.ArtifactId}");
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
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
                Endpoint: "https://c2.example.test",
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
                    Endpoint: "https://c2.example.test",
                    UriPath: "/beacon",
                    SleepSeconds: 30,
                    JitterSeconds: 10,
                    KillDate: null));
            var body = await response.Content.ReadFromJsonAsync<PayloadEndpoints.BuildPayloadResponse>();

            // The build is recorded: the engagement's trail holds a PayloadBuilt
            // event carrying the class and the artifact's fingerprint, and the
            // chain is intact. The trail also carries the engagement's own
            // creation event ( genesis), so it is no longer a single entry.
            var trail = await audit.ListAsync(Guid.Parse(engagementId));
            var evt = Assert.Single(trail, e => e.Kind == AuditEventKind.PayloadBuilt);
            Assert.Equal("payload.build", evt.Verb);
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
