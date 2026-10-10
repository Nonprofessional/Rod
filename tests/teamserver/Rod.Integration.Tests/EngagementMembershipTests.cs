using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Operators;
using Rod.Transport;
using Rod.Transport.Endpoints;
using static Rod.Integration.Tests.EngagementSetup;

namespace Rod.Integration.Tests;

/// <summary>
/// The engagement membership model over the API (architecture.md Sec 3):
/// the owner grants, re-tiers, and removes the operators who may see and
/// work an engagement. Mutations are the owner's alone (a member who is not
/// the owner is refused with 403), a stranger resolves to the same 404 a
/// missing engagement renders -- even a globally privileged one, because
/// access derives from membership, not from any global grant -- and every
/// change lands in the engagement's own audit trail and beats one live
/// event. The report's crew roster carries the owner beside the granted
/// memberships, role-tagged.
/// </summary>
public class EngagementMembershipTests
{
    private const string MemberHandle = "alice";
    private const string MemberPassword = "member-p@ss";

    private sealed record MemberRow(
        string OperatorId,
        string Handle,
        string DisplayName,
        string Role,
        DateTimeOffset AddedAt);

    private sealed record AddBody(string Handle, string Role);

    [Fact]
    public async Task Membership_Lifecycle_InvitesReTiersRemoves_AndRecordsTheTrail()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.RegisterOperatorAsync(env.Host, MemberHandle, "Alice Aaron", MemberPassword);
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http, "Operation Crew");

        // The fresh engagement's crew is the owner alone.
        var empty = await env.Http.GetFromJsonAsync<MemberRow[]>($"/engagements/{engagementId}/members");
        var owner = Assert.Single(empty!);
        Assert.Equal(AuthenticatedHost.Handle, owner.Handle);
        Assert.Equal("owner", owner.Role);

        // The invite lands as reader; the roster carries the granted tier.
        var invite = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/members",
            new AddBody(MemberHandle, "reader"));
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var added = await invite.Content.ReadFromJsonAsync<MemberRow>();
        Assert.Equal("reader", added!.Role);

        var roster = await env.Http.GetFromJsonAsync<MemberRow[]>($"/engagements/{engagementId}/members");
        Assert.Equal(2, roster!.Length);

        // The re-tier lands and renders; the removal empties the tier.
        var reTier = await env.Http.PutAsJsonAsync(
            $"/engagements/{engagementId}/members/{added.OperatorId}",
            new { role = "writer" });
        Assert.Equal(HttpStatusCode.OK, reTier.StatusCode);
        Assert.Equal("writer", (await reTier.Content.ReadFromJsonAsync<MemberRow>())!.Role);

        var removed = await env.Http.DeleteAsync(
            $"/engagements/{engagementId}/members/{added.OperatorId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Single((await env.Http.GetFromJsonAsync<MemberRow[]>($"/engagements/{engagementId}/members"))!);

        // Every change is a fact in the engagement's own trail -- membership
        // is engagement state, not account state.
        var audit = await env.Http.GetFromJsonAsync<AuditEndpoints.AuditListResponse>(
            $"/engagements/{engagementId}/audit");
        Assert.NotNull(audit);
        var kinds = audit!.Items.Select(e => e.Kind).ToArray();
        Assert.Contains(nameof(Rod.Audit.AuditEventKind.EngagementMemberAdded), kinds);
        Assert.Contains(nameof(Rod.Audit.AuditEventKind.EngagementMemberRoleChanged), kinds);
        Assert.Contains(nameof(Rod.Audit.AuditEventKind.EngagementMemberRemoved), kinds);
    }

    [Fact]
    public async Task Membership_MutationsAreOwnerOnly_AndStrangersResolveTo404()
    {
        await using var env = await TestEnv.StartAsync();
        var outsiderId = await AuthenticatedHost.RegisterOperatorAsync(
            env.Host, "outsider", "Outsider", MemberPassword);
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http, "Operation Walled");

        // A stranger renders the same 404 a missing engagement renders -- even
        // one holding every global privilege the current account model has:
        // access derives from membership, never from a global grant.
        using var outsider = env.NewClient();
        await AuthenticatedHost.LoginAsync(outsider, "outsider", MemberPassword);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/engagements/{engagementId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody("carol", "writer"))).StatusCode);

        // Once granted the reader tier, the roster is the member's own
        // surface -- but every mutation stays the owner's alone.
        var invite = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/members", new AddBody("outsider", "reader"));
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await outsider.GetAsync($"/engagements/{engagementId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody("carol", "writer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PutAsJsonAsync(
                $"/engagements/{engagementId}/members/{outsiderId}",
                new { role = "writer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.DeleteAsync($"/engagements/{engagementId}/members/{outsiderId}")).StatusCode);
    }

    [Fact]
    public async Task Membership_GuardsTheOwnerAndRefusesBadInput()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.RegisterOperatorAsync(
            env.Host, MemberHandle, "Alice Aaron", MemberPassword);
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http, "Operation Anchored");
        var ownerId = AuthenticatedHost.GetOperatorId(env.Host);

        // The owner is not a membership: no grant, no re-tier, no removal.
        Assert.Equal(HttpStatusCode.Conflict,
            (await env.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody(AuthenticatedHost.Handle, "writer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await env.Http.PutAsJsonAsync(
                $"/engagements/{engagementId}/members/{ownerId}",
                new { role = "reader" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await env.Http.DeleteAsync($"/engagements/{engagementId}/members/{ownerId}")).StatusCode);

        // Input rules: unknown handles and unknown roles refuse named, a
        // duplicate grant is a conflict, and a removal of a non-member is a
        // 404 rather than a quiet success.
        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody("ghost", "writer"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await env.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody(MemberHandle, "root"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await env.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody(MemberHandle, "reader"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await env.Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/members",
                new AddBody(MemberHandle, "writer"))).StatusCode);
        // Removing an operator who holds no membership is a 404, not a quiet
        // success -- a fat-fingered id must not read as a removal.
        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Http.DeleteAsync(
                $"/engagements/{engagementId}/members/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Report_RosterCarriesTheCrew_RoleTagged()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.RegisterOperatorAsync(env.Host, MemberHandle, "Alice Aaron", MemberPassword);
        await AuthenticatedHost.RegisterOperatorAsync(env.Host, "bob", "Bob Baker", MemberPassword);
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http, "Operation Ledger");

        await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/members", new AddBody(MemberHandle, "writer"));
        await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/members", new AddBody("bob", "reader"));

        var report = await env.Http.GetFromJsonAsync<ReportBody>($"/engagements/{engagementId}/report");
        Assert.NotNull(report);
        var crew = report!.Operators
            .OrderBy(o => o.Handle)
            .ToArray();
        Assert.Equal(3, crew.Length);
        Assert.Equal(("alice", "writer"), (crew[0].Handle, crew[0].Role));
        Assert.Equal(("bob", "reader"), (crew[1].Handle, crew[1].Role));
        Assert.Equal((AuthenticatedHost.Handle, "owner"), (crew[2].Handle, crew[2].Role));
    }

    private sealed record ReportBody(ReportEngagementBody Engagement, ReportOperatorBody[] Operators);
    private sealed record ReportEngagementBody(string EngagementId, string Name);
    private sealed record ReportOperatorBody(string OperatorId, string Handle, string Role);

    /// <summary>
    /// The auth-surface shape the operator-session suites use: the in-memory
    /// TestServer with the operator layers composed, a cookie-persisting
    /// client for the acting operator, and fresh clients per additional
    /// operator session.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
                .Build();
            await env.Host.StartAsync();

            env.Http = AuthenticatedHost.CreateClient(env.Host);
            return env;
        }

        public HttpClient NewClient() => AuthenticatedHost.CreateClient(Host);

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
