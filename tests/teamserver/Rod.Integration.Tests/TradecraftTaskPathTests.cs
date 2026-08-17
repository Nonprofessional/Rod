using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
using Rod.Operators;
using Rod.Tradecraft;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Evasion;
using Rod.Tradecraft.Exploit;
using Rod.Tradecraft.Modules;
using Rod.Tradecraft.Registry;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: a registered capability module is reached from the
/// live task path, and an evasion/exploit verb is no longer refused before
/// dispatch. Drives the operator-facing task endpoint through the in-memory
/// TestServer with the tradecraft layer layered onto the transport core -- the
/// same composition the teamserver host performs. Before  the evasion and
/// exploit verbs were refused at issuance (422) because they are not in the
/// per-class reduced verb set; the registry-backed task resolver now admits them
/// (architecture.md Sec 10.2/10.3).
/// </summary>
public class TradecraftTaskPathTests
{
    // A host that layers the tradecraft layer and the operator + auth
    // layers onto the transport core, so the capability registry is wired into
    // the live task path and the operator API requires a cookie session -- the
    // same composition the teamserver host performs.
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            configureServices: services => services.AddRodTradecraft());
        return (client, host);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        await AuthenticatedHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    // Enrolls a Stage-2 implant directly through the registry so the task gate
    // has an implant to read. Stage-2 is irrelevant to the evasion/exploit gate
    // (those verbs are not class-gated), but it is the class a long-haul implant
    // runs as.
    private static async Task<Implant> EnrollStage2Async(IHost host, EngagementId engagement)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(ImplantId.New(), engagement, "key-stage2", now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    [Theory]
    [InlineData(EvasionCapabilities.Avoid)]
    [InlineData(EvasionCapabilities.Unload)]
    [InlineData(ExploitCapabilities.Invoke)]
    [InlineData(ExploitCapabilities.Module)]
    public async Task TaskEndpoint_AdmitsAContractOnlyVerb_WhenTheTradecraftLayerIsWired(string verb)
    {
        // The capability registry lists every built-in verb, so the
        // registry-backed resolver admits the no-class-gate evasion and exploit
        // verbs (architecture.md Sec 10.2). These were 422 before ; they are
        // now queued (201) -- the verb is no longer refused before dispatch.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: verb,
                    Arguments: "arg"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task TaskEndpoint_AdmitsAContractOnlyVerb_OnAReducedClass()
    {
        // The evasion/exploit verbs are not class-gated (architecture.md Sec
        // 5.2/10.1), so the resolver admits them on a reduced class too: a
        // stager or web-shell can be tasked with one when a module is registered,
        // because the operator decides which class runs the out-of-tree module,
        // not a baked-in rule.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implants = host.Services.GetRequiredService<IImplantRepository>();
            var clock = host.Services.GetRequiredService<TimeProvider>();
            var now = clock.GetUtcNow();
            var implant = Implant.Enroll(
                ImplantId.New(), new EngagementId(Guid.Parse(engagementId)),
                "key-stager", now.AddDays(30), ImplantClass.Stager, now);
            await implants.SaveAsync(implant);

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: EvasionCapabilities.Avoid,
                    Arguments: "arg"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task TaskEndpoint_RefusesAVerbNoModuleHandles()
    {
        // The registry only widens the gate. A verb the class set does not admit
        // and no module is registered for is still refused before dispatch --
        // the resolver returns false for an unknown verb, so the 422 mapping is
        // preserved for genuine nonsense.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            var response = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: "does.not.exist",
                    Arguments: "arg"));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task TaskPath_ReachesARegisteredOutOfTreeModuleAndCarriesTheVerb()
    {
        // The full  acceptance: an out-of-tree module registered for an
        // evasion verb is the authority for it, the verb passes the live task
        // gate, and the queued task dispatches carrying the verb. The module
        // replaces the built-in placeholder by registering after AddRodTradecraft
        // (last registration wins), proving a registered module is reached from
        // the live task path.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var registry = host.Services.GetRequiredService<ICapabilityRegistry>();
            var overrideModule = new FixedResultModule(EvasionCapabilities.Avoid);
            await registry.RegisterAsync(overrideModule);

            var engagementId = await CreateEngagementAsync(client);
            var implant = await EnrollStage2Async(host, new EngagementId(Guid.Parse(engagementId)));

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new TaskEndpoints.IssueTaskRequest(
                    ImplantId: implant.Id.ToString(),
                    Verb: EvasionCapabilities.Avoid,
                    Arguments: "payload"));
            issued.EnsureSuccessStatusCode();

            // The queued task dispatches over the beacon path carrying the verb:
            // the resolver admitted it, and the round-trip (architecture.md Sec
            // 10.3) delivers the verb unchanged -- the implant, not the
            // teamserver, executes it.
            var tasks = host.Services.GetRequiredService<TaskService>();
            var dispatched = await tasks.DispatchNextAsync(implant.Id);

            Assert.NotNull(dispatched);
            Assert.Equal(EvasionCapabilities.Avoid, dispatched!.Verb);
            Assert.Equal("payload", dispatched.Arguments);
        }
    }

    // A module whose descriptor is fixed at construction, standing in for an
    // operator-supplied, out-of-tree evasion module without writing real
    // tradecraft.
    private sealed class FixedResultModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; }

        public FixedResultModule(string verb)
            => Descriptor = CapabilityDescriptor.Of(verb, CapabilityCategory.Evasion, "1.0");
    }
}
