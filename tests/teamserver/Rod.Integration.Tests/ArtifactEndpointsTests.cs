using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: artifacts are first-class objects linked to tasks
/// (architecture.md Sec 11). An operator attaches an artifact to a task, lists a
/// task's artifacts, and retrieves one back, all scoped by engagement, and each
/// attachment is recorded as an attributed, hash-chained event on the
/// engagement trail. The evidence and the tasking that gathered it stay bound.
/// The attaching operator is the logged-in operator; the request carries no
/// identity, and the audit attributes the attachment to that operator.
/// </summary>
public class ArtifactEndpointsTests
{
    [Fact]
    public async Task Attach_List_Retrieve_RoundTrips_AndIsAudited()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, implantId) = await SeedEngagementAndImplantAsync(env);
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();

        await AuthenticatedHost.LoginAsync(env.Http);

        // A task exists in the engagement to attach evidence to. It is queued,
        // not completed -- artifacts bind to the task that gathered them, and
        // need not wait for the result.
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId.ToString(), Verb = "file.pull", Arguments = "/etc/passwd" });
        issued.EnsureSuccessStatusCode();
        var taskId = (await issued.Content.ReadFromJsonAsync<TaskIssuedBody>())!.TaskId;

        var content = "root:x:0:0:root:/root:/bin/bash"u8.ToArray();
        var attached = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "passwd.txt",
                ContentType: "text/plain",
                Content: content));
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        var attachedBody = await attached.Content.ReadFromJsonAsync<ArtifactBody>();
        Assert.NotNull(attachedBody);
        Assert.Equal("passwd.txt", attachedBody!.Name);
        Assert.Equal("text/plain", attachedBody.ContentType);
        Assert.Equal(content.Length, attachedBody.Size);
        Assert.Equal(taskId, attachedBody.TaskId);
        // The attachment is attributed to the authenticated operator.
        Assert.Equal(env.OperatorId.Value, attachedBody.OperatorId);

        // A second artifact on the same task, stored later, so the list order is
        // observable.
        var later = new byte[] { 0x00, 0x01, 0x02 };
        await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "shadow.bin",
                ContentType: null,
                Content: later));

        // List the task's artifacts: one page, two records, oldest-first,
        // metadata only (no bytes), and the walk is exhausted.
        var list = await env.Http.GetFromJsonAsync<ArtifactEndpoints.ArtifactListResponse>(
            $"/engagements/{engagementId}/tasks/{taskId}/artifacts");
        Assert.NotNull(list);
        Assert.Null(list!.NextCursor);
        Assert.Equal(2, list.Items.Length);
        Assert.True(list.Items[0].StoredAt <= list.Items[1].StoredAt);
        Assert.Equal("passwd.txt", list.Items[0].Name);
        Assert.Equal("shadow.bin", list.Items[1].Name);
        // A null content type falls back to the opaque default on the wire.
        Assert.Equal("application/octet-stream", list.Items[1].ContentType);

        // Retrieve returns the stored bytes, by content type, by name -- the raw
        // evidence, not a JSON projection.
        var retrieved = await env.Http.GetAsync(
            $"/engagements/{engagementId}/artifacts/{attachedBody.ArtifactId}");
        retrieved.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", retrieved.Content.Headers.ContentType?.MediaType);
        Assert.Equal("passwd.txt", retrieved.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(content, await retrieved.Content.ReadAsByteArrayAsync());

        // The attachment landed as an attributed, immutable event on the trail,
        // bound to the task and named for the attaching operator. Two artifacts
        // were attached, so two events exist; assert the first by its outcome.
        await WaitUntilAsync(async () =>
            (await audit.ListAsync(engagementId)).Count(e => e.Kind == AuditEventKind.ArtifactAttached) == 2);
        var trail = await audit.ListAsync(engagementId);
        var artifactEvent = Assert.Single(trail, e =>
            e.Kind == AuditEventKind.ArtifactAttached && e.Outcome == attachedBody.ArtifactId);
        // The audit attributes the attachment to the authenticated operator, not
        // any client-supplied identity.
        Assert.Equal(env.OperatorId.Value, artifactEvent.OperatorId);
        Assert.Equal(Guid.Parse(taskId), artifactEvent.TaskId);
        Assert.Equal("passwd.txt;text/plain", artifactEvent.Payload);
    }

    [Fact]
    public async Task Retrieve_Returns404_ForArtifactInAnotherEngagement()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementA, implantA) = await SeedEngagementAndImplantAsync(env);
        var (engagementB, _) = await SeedEngagementAndImplantAsync(env);

        await AuthenticatedHost.LoginAsync(env.Http);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementA}/tasks",
            new { ImplantId = implantA.ToString(), Verb = "file.pull" });
        var taskId = (await issued.Content.ReadFromJsonAsync<TaskIssuedBody>())!.TaskId;

        var attached = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementA}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "secret.txt",
                ContentType: "text/plain",
                Content: "top-secret"u8.ToArray()));
        var artifactId = (await attached.Content.ReadFromJsonAsync<ArtifactBody>())!.ArtifactId;

        // Cross-engagement isolation: the artifact is reachable from its own
        // engagement, invisible from another -- engagement scoping is the
        // retrieval guard, by construction.
        var own = await env.Http.GetAsync($"/engagements/{engagementA}/artifacts/{artifactId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var foreign = await env.Http.GetAsync($"/engagements/{engagementB}/artifacts/{artifactId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Attach_Returns404_ForForeignTask()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementA, implantA) = await SeedEngagementAndImplantAsync(env);
        var (_, _) = await SeedEngagementAndImplantAsync(env);

        await AuthenticatedHost.LoginAsync(env.Http);

        // A task that belongs to engagement A, addressed against its own implant.
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementA}/tasks",
            new { ImplantId = implantA.ToString(), Verb = "file.pull" });
        var taskId = (await issued.Content.ReadFromJsonAsync<TaskIssuedBody>())!.TaskId;

        // The same task id read through a different engagement is a 404, so an
        // artifact can never be attached across engagement boundaries.
        var foreignEngagement = Guid.NewGuid();
        var response = await env.Http.PostAsJsonAsync(
            $"/engagements/{foreignEngagement}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "x",
                ContentType: null,
                Content: new byte[] { 1 }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Attach_Returns400_ForEmptyContent_OrMalformedId()
    {
        await using var env = await TestEnv.StartAsync();
        var (engagementId, implantId) = await SeedEngagementAndImplantAsync(env);

        await AuthenticatedHost.LoginAsync(env.Http);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implantId.ToString(), Verb = "file.pull" });
        var taskId = (await issued.Content.ReadFromJsonAsync<TaskIssuedBody>())!.TaskId;

        // The attaching operator always comes from the session; the remaining 400
        // cases are empty content and malformed ids.
        var noContent = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks/{taskId}/artifacts",
            new ArtifactEndpoints.AttachArtifactRequest(
                Name: "x",
                ContentType: null,
                Content: Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.BadRequest, noContent.StatusCode);

        // Malformed ids.
        var badEngagement = await env.Http.GetAsync($"/engagements/not-a-guid/artifacts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, badEngagement.StatusCode);
    }

    private static async Task<(Guid EngagementId, ImplantId ImplantId)> SeedEngagementAndImplantAsync(TestEnv env)
    {
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        var engagementId = EngagementId.New();
        var implantId = ImplantId.New();
        var implant = Implant.Enroll(
            implantId,
            engagementId,
            key: "key-" + implantId,
            killDate: now.AddDays(30),
            @class: ImplantClass.Stage2,
            now);
        await implants.SaveAsync(implant);
        return (engagementId.Value, implantId);
    }

    // Polls until condition is true or the timeout elapses. The audit append runs
    // on the HTTP handler thread; the readback here is a separate call and may
    // need a beat to observe it.
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

    private sealed class ArtifactBody
    {
        public string ArtifactId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public Guid? OperatorId { get; set; }
        public string Name { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset StoredAt { get; set; }
    }

    /// <summary>
    /// A real Kestrel teamserver with the operator HTTP API bound. Artifacts are
    /// operator-facing, so the mTLS implant endpoint is not exercised here; the
    /// harness mirrors the other operator-API tests minus the beacon wiring. The
    /// operator + auth layers are composed so the API requires a cookie session.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public OperatorId OperatorId { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
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
