using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Rod.Operators.Automation;
using Rod.Persistence;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// The automation acceptance criterion (architecture.md Sec 10.4): a rule
/// that issues shell.exec on one implant every interval survives a
/// teamserver restart, its firings attribute to the synthetic automation
/// operator in the audit trail, and it is cancelable from the operator API.
/// Host A writes against a real Postgres, is torn down, and host B -- a
/// whole new process over the same database -- carries the schedule, fires
/// again, and stops when disabled. Skips when Docker is absent, the same
/// posture as the core-state durability suite.
/// </summary>
[Collection("postgres")]
public sealed class AutomationDurabilityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public AutomationDurabilityTests(PostgresFixture postgres)
        => _postgres = postgres;

    [Fact]
    public async Task AScheduledRule_SurvivesRestart_AttributesToAutomation_AndCancels()
    {
        Assert.True(_postgres.IsAvailable, "Postgres is not available; skipping.");

        string engagementId;
        string implantId;
        string ruleId;
        await using (var hostA = await TestEnv.StartAsync(_postgres))
        {
            (engagementId, implantId) = await SetupEngagementAsync(hostA.Http);
            ruleId = await CreateWatchRuleAsync(hostA.Http, engagementId, implantId);

            // The rule fires under host A (5 s cadence, 5 s engine tick:
            // the first firing lands within ~11 s).
            var firstFiring = await WaitUntilAsync(async () =>
                (await ListShellTasksAsync(hostA.Http, engagementId)).Length >= 1);
            Assert.True(firstFiring, "The rule never fired under host A.");

            var audit = await hostA.Http.GetFromJsonAsync<AuditListBody>(
                $"/engagements/{engagementId}/audit?limit=200");
            Assert.Contains(audit!.Items, e => e.Kind == "AutomationRuleFired");
            Assert.Contains(audit!.Items, e =>
                e.Kind == "TaskIssued" && e.OperatorHandle == "automation");

            // Tear the process down: listeners, engine, in-memory state.
            await hostA.DisposeAsync();
        }

        // Host B: a fresh teamserver over the same database. The rule comes
        // back with its schedule and fires again -- the AC's "survives a
        // teamserver restart".
        await using var hostB = await TestEnv.StartAsync(_postgres);
        var rules = await hostB.Http.GetFromJsonAsync<RuleListBody>(
            $"/engagements/{engagementId}/automation-rules");
        var restored = Assert.Single(rules!.Rules);
        Assert.Equal(ruleId, restored.Id);
        Assert.True(restored.Enabled);
        Assert.True(restored.FireCount >= 1);
        Assert.NotNull(restored.NextFireAt);

        var baseline = (await ListShellTasksAsync(hostB.Http, engagementId)).Length;
        var firedAgain = await WaitUntilAsync(async () =>
            (await ListShellTasksAsync(hostB.Http, engagementId)).Length > baseline);
        Assert.True(firedAgain, "The restored rule never fired under host B.");

        // The cancel, from the operator API: disable, then one full cadence
        // plus a tick of silence proves nothing fires after it.
        var disable = await hostB.Http.PostAsync(
            $"/engagements/{engagementId}/automation-rules/{ruleId}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var afterCancel = (await ListShellTasksAsync(hostB.Http, engagementId)).Length;
        await Task.Delay(TimeSpan.FromSeconds(12));
        var still = (await ListShellTasksAsync(hostB.Http, engagementId)).Length;
        Assert.Equal(afterCancel, still);
    }

    private static async Task<(string EngagementId, string ImplantId)> SetupEngagementAsync(HttpClient http)
    {
        var engagement = await http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Watchtower"));
        engagement.EnsureSuccessStatusCode();
        var engagementId = (await engagement.Content.ReadFromJsonAsync
            <EngagementEndpoints.EngagementResponse>())!.EngagementId;

        var token = await http.PostAsync($"/engagements/{engagementId}/deploy-tokens", content: null);
        token.EnsureSuccessStatusCode();
        var secret = (await token.Content.ReadFromJsonAsync<EngagementEndpoints.DeployTokenResponse>())!.Secret;
        var enroll = await http.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(DeployTokenSecret: secret, Class: null, PublicKey: null));
        enroll.EnsureSuccessStatusCode();
        var implantId = (await enroll.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>())!.ImplantId!;
        return (engagementId, implantId);
    }

    private static async Task<string> CreateWatchRuleAsync(HttpClient http, string engagementId, string implantId)
    {
        var create = await http.PostAsJsonAsync(
            $"/engagements/{engagementId}/automation-rules",
            new
            {
                Name = "overnight-screenshots",
                Trigger = new { Kind = "interval", IntervalSeconds = 5 },
                TargetImplantId = implantId,
                Verb = "shell.exec",
                Arguments = "uptime",
            });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<RuleBody>())!.Id;
    }

    private static async Task<TaskRow[]> ListShellTasksAsync(HttpClient http, string engagementId)
    {
        var tasks = await http.GetFromJsonAsync<TaskListBody>(
            $"/engagements/{engagementId}/tasks?limit=200");
        return tasks!.Items.Where(t => t.Verb == "shell.exec").ToArray();
    }

    /// <summary>Polls every half-second for up to 30 s -- a cadence plus ticks.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var elapsed = 0; elapsed < TimeSpan.FromSeconds(30).TotalMilliseconds; elapsed += 500)
        {
            if (await condition())
                return true;
            await Task.Delay(500);
        }
        return await condition();
    }

    // The Postgres-backed host pair the core-state durability suite uses:
    // schema applied before start, proxy-bypassing client, cookie session.
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync(PostgresFixture postgres)
        {
            var env = new TestEnv();
            var httpPort = TestSupport.GetFreeTcpPort();
            var config = AuthenticatedHost.BuildConfig(
                extend: dict => dict["ConnectionStrings:Postgres"] = postgres.ConnectionString);

            env.Host = TransportHost.CreateHostBuilder(
                    configuration: config,
                    configureServices: services => AuthenticatedHost.ComposeServices(
                        services, config, extra: s => s.AddRodPersistence(config)),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints))
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();

            // The host does not auto-migrate; the test creates the schema the
            // way an operator would, with one bounded retry for a container
            // that loses its first fresh connection.
            try
            {
                await EnsureSchemaAsync(env.Host);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await EnsureSchemaAsync(env.Host);
            }
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler { UseProxy = false }))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        private static async Task EnsureSchemaAsync(IHost host)
        {
            var factory = host.Services.GetRequiredService<IDbContextFactory<RodPersistenceDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private sealed class RuleBody
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; }
        public int FireCount { get; set; }
        public DateTimeOffset? NextFireAt { get; set; }
    }

    private sealed class RuleListBody
    {
        public RuleBody[] Rules { get; set; } = [];
    }

    private sealed class TaskListBody
    {
        public TaskRow[] Items { get; set; } = [];
    }

    private sealed class TaskRow
    {
        public string TaskId { get; set; } = "";
        public string Verb { get; set; } = "";
    }

    private sealed class AuditListBody
    {
        public AuditEntry[] Items { get; set; } = [];
    }

    private sealed class AuditEntry
    {
        public string Kind { get; set; } = "";
        public string OperatorHandle { get; set; } = "";
    }
}
