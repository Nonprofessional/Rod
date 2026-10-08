using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using static Rod.Integration.Tests.EngagementSetup;
// The domain task entity shares its name with the BCL type; this file's
// signatures speak only the BCL one, and the entity is reached through the
// repository's own types.
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the topology projection (architecture.md Sec 11.2). The
/// engagement's network picture assembles at read time from the three
/// sources that already hold it -- the fleet's enrollment hostnames (grouped,
/// with pivot links from recorded parentage) and the completed recon task
/// outputs parsed against the documented JSON-lines grammar
/// (extending/tradecraft.md). Nothing is stored: a host store would be a
/// second truth to keep consistent, and the projection owes the picture to
/// the trail and the task history alone.
/// </summary>
public sealed class TopologyEndpointTests
{
    private static async Task<string> EnrollWithHostnameAsync(
        HttpClient client, string secret, string hostname, string? parentImplantId = null)
    {
        var response = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(
                DeployTokenSecret: secret, Hostname: hostname, ParentImplantId: parentImplantId));
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        return enrolled!.ImplantId!;
    }

    [Fact]
    public async Task Topology_GroupsHosts_PinsPivots_AndCarriesLabels()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var parentSecret = await MintDeployTokenAsync(client, engagementId);
            var parent = await EnrollWithHostnameAsync(client, parentSecret, "jump01");

            // The child derivation: the pivot link the picture draws.
            var childSecret = await MintDeployTokenAsync(client, engagementId);
            var child = await EnrollWithHostnameAsync(client, childSecret, "web01", parentImplantId: parent);

            // A label on the parent's host -- the trail's marker vocabulary
            // rendering on the graph.
            await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/hosts/jump01/labels",
                new HostEndpoints.SetHostLabelRequest(Label: "dmz"));

            var topology = await client.GetFromJsonAsync<TopologyEndpoints.TopologyResponse>(
                $"/engagements/{engagementId}/topology");
            Assert.NotNull(topology);
            Assert.True(topology!.ChainVerified);

            var jump = Assert.Single(topology.Hosts, h => h.Host == "jump01");
            Assert.Equal("enrolled", jump.Kind);
            Assert.Equal(new[] { parent }, jump.ImplantIds);
            Assert.Equal(new[] { "dmz" }, jump.Labels);

            var web = Assert.Single(topology.Hosts, h => h.Host == "web01");
            Assert.Equal("enrolled", web.Kind);
            Assert.Equal(new[] { child }, web.ImplantIds);

            // The pivot edge runs outward: the parent derived the child.
            var link = Assert.Single(topology.Links);
            Assert.Equal("pivot", link.Kind);
            Assert.Equal(parent, link.FromImplantId);
            Assert.Equal(child, link.ToImplantId);
            Assert.Equal("jump01", link.FromHost);
            Assert.Equal("web01", link.ToHost);
        }
    }

    [Fact]
    public async Task Topology_ParsesReconObservations_ByTheDocumentedGrammar()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollWithHostnameAsync(client, secret, "jump01");

            // A portscan whose output speaks the grammar, with one line that
            // does not parse and one that carries no host -- neither is a
            // finding, and neither errors the view.
            await CompleteTaskAsync(client, host, engagementId, implantId, "recon.portscan", string.Join('\n',
                "{\"host\":\"10.0.0.5\",\"port\":445,\"state\":\"open\",\"service\":\"smb\"}",
                "not json at all",
                "{\"port\":22}",
                "{\"host\":\"10.0.0.6\",\"port\":22,\"state\":\"open\"}"));
            await CompleteTaskAsync(client, host, engagementId, implantId, "recon.hostenum", string.Join('\n',
                "{\"host\":\"db01\",\"addresses\":[\"10.0.0.9\"],\"os\":\"linux\",\"arch\":\"x86_64\"}"));

            var topology = await client.GetFromJsonAsync<TopologyEndpoints.TopologyResponse>(
                $"/engagements/{engagementId}/topology");
            Assert.NotNull(topology);

            // Every observation is attributed to the task that produced it.
            Assert.Equal(3, topology!.Observations.Length);
            var smb = Assert.Single(topology.Observations, o =>
                o.Host == "10.0.0.5" && o.Port == 445);
            Assert.Equal("open", smb.State);
            Assert.Equal("smb", smb.Service);
            Assert.Equal("recon.portscan", smb.Verb);
            Assert.Equal(implantId, smb.ImplantId);

            var db01 = Assert.Single(topology.Observations, o => o.Host == "db01");
            Assert.Equal("10.0.0.9", db01.Address);
            Assert.Equal("linux", db01.Os);

            // Hosts only recon has seen join the picture with their observed
            // attributes, distinct from the enrolled one.
            var observed = Assert.Single(topology.Hosts, h => h.Host == "db01");
            Assert.Equal("observed", observed.Kind);
            Assert.Equal("linux", observed.Os);
            Assert.Equal("x86_64", observed.Arch);
            Assert.Empty(observed.ImplantIds);
            Assert.Contains(topology.Hosts, h => h.Host == "jump01" && h.Kind == "enrolled");
        }
    }

    [Fact]
    public async Task Topology_StandsAnUnattributedImplantAlone_AndScopesByEngagement()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var secret = await MintDeployTokenAsync(client, engagementId);
            var implantId = await EnrollAsync(client, secret);

            // An implant that reported no hostname cannot be grouped; it
            // stands as its own node keyed by its id rather than pinned to an
            // invented host.
            var topology = await client.GetFromJsonAsync<TopologyEndpoints.TopologyResponse>(
                $"/engagements/{engagementId}/topology");
            var alone = Assert.Single(topology!.Hosts);
            Assert.Equal(implantId, alone.Host);
            Assert.Equal("enrolled", alone.Kind);
            Assert.Equal(new[] { implantId }, alone.ImplantIds);

            // The scoped-read ladder.
            var foreign = await client.GetAsync($"/engagements/{Guid.NewGuid()}/topology");
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            var malformed = await client.GetAsync($"/engagements/not-a-guid/topology");
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
    }

    // Issues a task over the API and completes it directly through the
    // repository -- the shape a beacon's captured result leaves behind, which
    // is what the projection reads.
    private static async Task CompleteTaskAsync(
        HttpClient client,
        IHost host,
        string engagementId,
        string implantId,
        string verb,
        string output)
    {
        var issued = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new TaskEndpoints.IssueTaskRequest(ImplantId: implantId, Verb: verb, Arguments: "operand"));
        issued.EnsureSuccessStatusCode();
        var task = await issued.Content.ReadFromJsonAsync<TaskEndpoints.TaskResponse>();

        var tasks = host.Services.GetRequiredService<ITaskRepository>();
        var stored = await tasks.FindAsync(new TaskId(Guid.Parse(task!.TaskId)));
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var at = clock.GetUtcNow();
        stored!.MarkDispatched(at);
        stored.Complete(output, TaskOutcome.Succeeded, at.AddSeconds(1));
        await tasks.SaveAsync(stored);
    }
}
