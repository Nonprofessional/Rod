using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Capabilities;
using Rod.Capabilities.Modules;
using Rod.Capabilities.Registry;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using static Rod.Integration.Tests.EngagementSetup;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the capability-contract trilogy (todo's Phase P0,
/// architecture.md Sec 15.2, ADR-003): a module the framework never
/// anticipated -- a verb namespace no class table carries, no built-in
/// placeholder, nothing in the UI's vocabulary -- reaches full interaction
/// with zero framework changes. The module registers exactly the way a
/// config-listed external assembly does (last registration wins over
/// whatever the registry holds); from there the catalog renders its form
/// from the declared parameter schema, the task issues over the ordinary
/// operator route, and the completed result's structured products land in
/// the generic panels -- the topology projection for enumeration output,
/// the credentials projection for credential output, the loot
/// classification for file output -- each keyed on the declared output
/// kind, never on the verb's name.
/// </summary>
public class ContractTrilogyTests
{
    // The sweep namespace: nothing in the framework, the class tables, or
    // the built-in registry speaks it. Any behavior it exhibits, it brought
    // with it.
    private const string DiscoverVerb = "sweep.discover";
    private const string DumpVerb = "sweep.dumpcreds";

    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create(
            configureServices: services => services.AddRodCapabilities(),
            mapEndpoints: endpoints => endpoints.MapCapabilityEndpoints());
        AuthenticatedHost.LoginAsync(client).GetAwaiter().GetResult();
        return (client, host);
    }

    // The novel module: two verbs, one per structured output kind the test
    // proves. The schema mirrors what an operator's module would declare for
    // a host-and-range sweep; the credential verb takes no arguments.
    private sealed class SweepModule : ICapabilityModule
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Of(DiscoverVerb, CapabilityCategory.Recon, "0.3",
            parameters: new[]
            {
                new VerbParameter("host", "Host", VerbParameterKind.String, Required: true, Placeholder: "10.0.0.7"),
                new VerbParameter("range", "Port range", VerbParameterKind.String, Required: true, Placeholder: "1-1024"),
            },
            output: new VerbOutput(VerbOutputKind.Enumeration, "One JSON finding per line."));

        public IReadOnlyList<CapabilityDescriptor> Extra { get; } =
        [
            CapabilityDescriptor.Of(DumpVerb, CapabilityCategory.Collect, "0.3",
                output: new VerbOutput(VerbOutputKind.Credential, "One JSON row per finding.")),
            CapabilityDescriptor.Of("sweep.exfil", CapabilityCategory.Exfil, "0.3",
                output: new VerbOutput(VerbOutputKind.File)),
        ];
    }

    private static async Task<(string EngagementId, string ImplantId)> EngageAsync(HttpClient client, IHost host)
    {
        var engagementId = await CreateEngagementAsync(client);
        var implant = await EnrollImplantAsync(host, new EngagementId(Guid.Parse(engagementId)));
        return (engagementId, implant.Id.ToString());
    }

    private static async Task<string> IssueAndCompleteAsync(
        HttpClient client,
        IHost host,
        string engagementId,
        string implantId,
        string verb,
        string arguments,
        string output)
    {
        var issued = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new TaskEndpoints.IssueTaskRequest(ImplantId: implantId, Verb: verb, Arguments: arguments));
        issued.EnsureSuccessStatusCode();
        var task = await issued.Content.ReadFromJsonAsync<TaskEndpoints.TaskResponse>();

        // Drive the completion through the use case, not the repository: the
        // result capture is where a credential-kind task's harvest lands
        // (architecture.md Sec 15.3), so a repository-direct completion would
        // skip the products the read projections assert on. The dispatch mark
        // stands in for the beacon claim this test never opens.
        var repository = host.Services.GetRequiredService<ITaskRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var stored = await repository.FindAsync(new TaskId(Guid.Parse(task!.TaskId)));
        stored!.MarkDispatched(clock.GetUtcNow());
        await repository.SaveAsync(stored);
        var service = host.Services.GetRequiredService<Rod.CoreState.Application.TaskService>();
        await service.RecordResultAsync(
            new TaskId(Guid.Parse(task!.TaskId)), output, TaskOutcome.Succeeded);
        return task.TaskId;
    }

    [Fact]
    public async Task ANovelModule_ReachesFullInteraction_WithZeroFrameworkChanges()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            // The module arrives the way every external module arrives:
            // registered against the registry (what the config-listed loader
            // does at startup), replacing nothing because nothing held its
            // verbs.
            var registry = host.Services.GetRequiredService<ICapabilityRegistry>();
            var sweep = new SweepModule();
            await registry.RegisterAsync(new FixedModule(sweep.Descriptor));
            await registry.RegisterAsync(new FixedModule(sweep.Extra[0]));
            await registry.RegisterAsync(new FixedModule(sweep.Extra[1]));

            var (engagementId, implantId) = await EngageAsync(client, host);

            // Contract 1 -- the catalog carries the schema the UI renders the
            // form from: the fields in declaration order, their kinds, and
            // the output declaration, for verbs the framework never met.
            var descriptors = await client.GetFromJsonAsync<Rod.Capabilities.Endpoints.CapabilityEndpoints.CapabilityDescriptorResponse[]>("/capabilities");
            var discover = Assert.Single(descriptors!, d => d.Verb == DiscoverVerb);
            Assert.NotNull(discover.Parameters);
            Assert.Equal(2, discover.Parameters!.Count);
            Assert.Equal("host", discover.Parameters[0].Name);
            Assert.Equal("String", discover.Parameters[0].Kind);
            Assert.True(discover.Parameters[0].Required);
            Assert.Equal("range", discover.Parameters[1].Name);
            Assert.NotNull(discover.Output);
            Assert.Equal("Enumeration", discover.Output!.Kind);

            // The task issues over the ordinary operator route with arguments
            // built the way the schema-driven form builds them (space-joined
            // tokens in declaration order): the gate widened at registration,
            // the wire stayed the opaque string.
            var findings = string.Join('\n',
                "{\"host\":\"10.0.0.7\",\"port\":443,\"state\":\"open\",\"service\":\"https\"}",
                "{\"host\":\"10.0.0.8\",\"addresses\":[\"10.0.0.8\"],\"os\":\"linux\",\"arch\":\"x86_64\"}");
            await IssueAndCompleteAsync(client, host, engagementId, implantId, DiscoverVerb, "10.0.0.7 1-1024", findings);

            // Contract 2 -- the enumeration output lands in the topology
            // panel: the projection parsed it from the declared kind, not
            // from a verb name it was never told about.
            var topology = await client.GetFromJsonAsync<TopologyEndpoints.TopologyResponse>(
                $"/engagements/{engagementId}/topology");
            Assert.NotNull(topology);
            var https = Assert.Single(topology!.Observations, o => o.Host == "10.0.0.7" && o.Port == 443);
            Assert.Equal("https", https.Service);
            Assert.Equal(DiscoverVerb, https.Verb);
            var observed = Assert.Single(topology.Hosts, h => h.Host == "10.0.0.8");
            Assert.Equal("observed", observed.Kind);
            Assert.Equal("linux", observed.Os);

            // The credential output lands in the credentials projection: rows
            // parsed against the documented grammar, masked secrets carried,
            // attribution to the task.
            var creds = string.Join('\n',
                "{\"kind\":\"ssh\",\"principal\":\"root\",\"secret\":\"hunted-secret\",\"source\":\"~/.ssh/id_ed25519\",\"host\":\"10.0.0.8\"}",
                "{\"kind\":\"browser\",\"principal\":\"operator@example\",\"source\":\"chrome login data\"}",
                "not json at all");
            await IssueAndCompleteAsync(client, host, engagementId, implantId, DumpVerb, "", creds);

            var credentials = await client.GetFromJsonAsync<CredentialEndpoints.CredentialListResponse>(
                $"/engagements/{engagementId}/credentials");
            Assert.NotNull(credentials);
            Assert.Equal(2, credentials!.Items.Count);
            var root = Assert.Single(credentials.Items, c => c.Principal == "root");
            Assert.Equal("ssh", root.Kind);
            Assert.Equal("hunted-secret", root.Secret);
            Assert.Equal(DumpVerb, root.Verb);
            Assert.NotNull(root.TaskId);

            // And the file-typed output classifies its artifact as file loot,
            // the declared kind standing in for the verb-name table the
            // classification used to carry.
            var artifactTask = await IssueAndCompleteAsync(client, host, engagementId, implantId, "sweep.exfil", "/tmp/loot.zip", "staged 3 files");
            var attached = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{artifactTask}/artifacts",
                new ArtifactEndpoints.AttachArtifactRequest(
                    Name: "loot.zip", ContentType: "application/zip", Content: [1, 2, 3]));
            attached.EnsureSuccessStatusCode();

            var loot = await client.GetFromJsonAsync<ArtifactEndpoints.LootListResponse>(
                $"/engagements/{engagementId}/loot?kind=file");
            Assert.NotNull(loot);
            var row = Assert.Single(loot!.Items, i => i.Name == "loot.zip");
            Assert.Equal("file", row.Kind);
            Assert.Equal("sweep.exfil", row.Verb);
        }
    }

    [Fact]
    public async Task AVerbWithNoDeclaration_RendersPlainOutput_AndDegradesHonestly()
    {
        // The degradation contract: a credential-typed verb whose output does
        // not parse earns no rows -- the panel stays empty, the transcript
        // keeps the record, nothing errors.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var registry = host.Services.GetRequiredService<ICapabilityRegistry>();
            await registry.RegisterAsync(new FixedModule(
                CapabilityDescriptor.Of(DumpVerb, CapabilityCategory.Collect, "0.3",
                    output: new VerbOutput(VerbOutputKind.Credential))));

            var (engagementId, implantId) = await EngageAsync(client, host);
            await IssueAndCompleteAsync(client, host, engagementId, implantId, DumpVerb, "", "prose the grammar cannot read");

            var credentials = await client.GetFromJsonAsync<CredentialEndpoints.CredentialListResponse>(
                $"/engagements/{engagementId}/credentials");
            Assert.NotNull(credentials);
            Assert.Empty(credentials!.Items);
        }
    }

    // The one-descriptor module the registry consumes; a real module with
    // several verbs registers once per verb the way the placeholder load and
    // the config-listed loader both do.
    private sealed class FixedModule : ICapabilityModule
    {
        public FixedModule(CapabilityDescriptor descriptor) => Descriptor = descriptor;

        public CapabilityDescriptor Descriptor { get; }
    }
}
