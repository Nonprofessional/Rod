using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// ROE guardrails over the operator API (architecture.md Sec 9): an operator
/// applies an engagement's rules-of-engagement profile, tasks outside it are
/// refused with 422 at queue time, the refusal lands in the engagement trail
/// naming the violated rule, and applying an empty profile reopens the
/// engagement. Pure server-side scope -- the implant contract is uninvolved.
/// </summary>
public class RoeEndpointsTests
{
    [Fact]
    public async Task TaskOutsideProfile_IsRefusedAtQueueTime_AndAuditedNamingTheRule()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        await AuthenticatedHost.LoginAsync(env.Http);

        // A real engagement record and a Stage-2 implant bound to it, so the
        // gate reads the engagement's scope and the class gate admits both
        // verbs -- the refusal under test can only be the ROE gate's.
        var engagementId = await CreateEngagementAsync(env.Http);
        var implant = Implant.Enroll(
            ImplantId.New(), new EngagementId(engagementId), "key-roe-it",
            clock.GetUtcNow().AddDays(30), ImplantClass.Stage2, clock.GetUtcNow());
        await implants.SaveAsync(implant);

        // Scope: recon only, this implant only.
        var applied = await env.Http.PutAsJsonAsync(
            $"/engagements/{engagementId}/roe",
            new { PermittedVerbs = new[] { "recon.*" }, PermittedImplants = new[] { implant.Id.ToString() } });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        // A class-admissible verb outside the ROE scope -> 422 before queueing.
        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.NotNull(problem);
        Assert.Contains("rules of engagement", problem!.Error);
        Assert.Contains("permitted verbs", problem.Error);

        // The refusal is in the trail naming the violated rule (the AC).
        await WaitUntilAsync(async () =>
            (await audit.ListAsync(engagementId)).Any(e => e.Kind == AuditEventKind.TaskRoeRefused));
        var trail = await audit.ListAsync(engagementId);
        var refusal = trail.Single(e => e.Kind == AuditEventKind.TaskRoeRefused);
        Assert.Equal("shell.exec", refusal.Verb);
        Assert.Contains("permitted verbs", refusal.Outcome);
        // The scope change itself is recorded too.
        Assert.Contains(trail, e => e.Kind == AuditEventKind.RoeUpdated);

        // An in-scope verb on the permitted target still queues.
        var allowed = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "recon.hostenum", Arguments = "" });
        Assert.True(allowed.IsSuccessStatusCode);

        // A target outside the permitted set is refused on the target rule --
        // use the same verb that just queued, so only the target differs.
        var foreign = await env.Http.PutAsJsonAsync(
            $"/engagements/{engagementId}/roe",
            new { PermittedVerbs = new[] { "recon.*" }, PermittedImplants = new[] { "ffffffffffffffffffffffffffffffff" } });
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        var refusedTarget = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "recon.hostenum", Arguments = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedTarget.StatusCode);
        var targetProblem = await refusedTarget.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Contains("permitted targets", targetProblem!.Error);

        // Applying the empty profile reopens the engagement: the previously
        // refused tasking now queues.
        var reopened = await env.Http.PutAsJsonAsync(
            $"/engagements/{engagementId}/roe",
            new { PermittedVerbs = Array.Empty<string>(), PermittedImplants = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        Assert.True(issued.IsSuccessStatusCode);
    }

    private static async Task<Guid> CreateEngagementAsync(HttpClient http)
    {
        var created = await http.PostAsJsonAsync("/engagements", new { Name = "roe-endpoints-test" });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<EngagementBody>();
        return Guid.Parse(body!.EngagementId);
    }

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

    private sealed class EngagementBody
    {
        public string EngagementId { get; set; } = "";
    }

    private sealed class ProblemBody
    {
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// A plain-HTTP operator API host; the beacon endpoint is not exercised --
    /// the ROE gate is queue-time, upstream of any stream.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            var httpPort = GetFreeTcpPort();
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
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
