using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Operators;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// The account off switch over the API (architecture.md Sec 4.5): a disabled
/// operator authenticates nowhere -- login fails indistinguishably from an
/// unknown handle, live cookie sessions reject at their next request, and
/// API tokens refuse -- while its scopes ride along untouched, so an enable
/// restores exactly the reach the account had. The lockout guard scope
/// removal keeps applies verbatim: disabling the last standing task holder
/// is refused, and a disabled task holder no longer counts as standing in
/// either guard.
/// </summary>
public class OperatorDisableTests
{
    private const string Handle = "alice";
    private const string Password = "disable-p@ss";

    private sealed record AccountBody(
        Guid Id,
        string Handle,
        string DisplayName,
        string[] Scopes,
        DateTimeOffset CreatedAt,
        bool HasCredential,
        bool Disabled);

    private sealed record MintedTokenBody(string TokenId, string Token, DateTimeOffset CreatedAt);

    private sealed record StatusBody(string OperatorId, bool Disabled);

    [Fact]
    public async Task Disable_EndsEveryAuthenticationPath_AndEnableRestoresThem()
    {
        await using var env = await TestEnv.StartAsync();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Alice Aaron", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // Establish both authenticated states first: a live cookie session
        // and an API token riding its own bearer path.
        using var session = env.NewClient();
        await AuthenticatedHost.LoginAsync(session, Handle, Password);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/operators/me")).StatusCode);

        var mint = await env.Http.PostAsync($"/operators/{targetId}/tokens", content: null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<MintedTokenBody>();
        Assert.NotNull(minted);
        using var bearer = env.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted!.Token);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/operators/me")).StatusCode);

        // The switch: cookie, token, and password login all refuse at their
        // next use, and the roster names the account disabled with its
        // scopes intact.
        var disable = await env.Http.PostAsync($"/operators/{targetId}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        var disabledBody = await disable.Content.ReadFromJsonAsync<StatusBody>();
        Assert.True(disabledBody!.Disabled);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/operators/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/operators/me")).StatusCode);
        using (var stale = env.NewClient())
        {
            var oldPassword = await stale.PostAsJsonAsync("/operators/login",
                new { handle = Handle, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        var roster = await env.Http.GetFromJsonAsync<AccountBody[]>("/operators");
        var row = Assert.Single(roster!, o => o.Handle == Handle);
        Assert.True(row.Disabled);
        Assert.Equal(new[] { "read", "task", "approve" }, row.Scopes);

        // The restore twin: every path reopens with the scopes the account
        // kept through the disable.
        var enable = await env.Http.PostAsync($"/operators/{targetId}:enable", content: null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.False((await enable.Content.ReadFromJsonAsync<StatusBody>())!.Disabled);

        using var relogged = env.NewClient();
        await AuthenticatedHost.LoginAsync(relogged, Handle, Password);
        Assert.Equal(HttpStatusCode.OK, (await relogged.GetAsync("/operators/me")).StatusCode);
    }

    [Fact]
    public async Task Disable_RefusesTheLastStandingTaskHolder_AndIsIdempotent()
    {
        await using var env = await TestEnv.StartAsync();
        var seeded = AuthenticatedHost.GetOperatorId(env.Host);
        await AuthenticatedHost.LoginAsync(env.Http);

        // The seeded operator is the only standing task holder (the
        // automation row holds no scopes): disabling it is the lockout.
        var refused = await env.Http.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // A second holder stands: provision Alice, and she disables the
        // seed from her own session -- the seeded session dies with the
        // disable, at its very next request.
        var second = await env.Http.PostAsJsonAsync("/operators", new { handle = Handle, password = Password });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondId = (await second.Content.ReadFromJsonAsync<AccountBody>())!.Id;

        using var alice = env.NewClient();
        await AuthenticatedHost.LoginAsync(alice, Handle, Password);
        var disabled = await alice.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        // Idempotent on repeat -- and enabling a not-disabled account is
        // the same no-op shape.
        var again = await alice.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var enableNoop = await alice.PostAsync($"/operators/{secondId}:enable", content: null);
        Assert.Equal(HttpStatusCode.OK, enableNoop.StatusCode);

        // Alice is the last standing holder now: same refusal, other side
        // -- her own disable included.
        var lastRefusal = await alice.PostAsync($"/operators/{secondId}:disable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, lastRefusal.StatusCode);
    }

    [Fact]
    public async Task Disable_IsActingScoped_AndRefusesUnknownAccounts()
    {
        await using var env = await TestEnv.StartAsync();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Alice Aaron", Password);
        var viewerId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "viewer", "Viewer", Password);
        await AuthenticatedHost.LoginAsync(env.Http);
        await env.Http.PutAsJsonAsync($"/operators/{viewerId}/scopes", new { scopes = new[] { "read" } });

        // Anonymous requests refuse at the session; a read-only operator
        // cannot flip the switch for anyone.
        using var anonymous = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/operators/{targetId}:disable", content: null)).StatusCode);

        using var viewer = env.NewClient();
        await AuthenticatedHost.LoginAsync(viewer, "viewer", Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"/operators/{targetId}:disable", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"/operators/{targetId}:enable", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Http.PostAsync("/operators/00000000-0000-0000-0000-000000000000:disable", content: null))
            .StatusCode);
    }

    [Fact]
    public async Task ScopeRemoval_GuardIgnoresDisabledTaskHolders()
    {
        await using var env = await TestEnv.StartAsync();
        var seeded = AuthenticatedHost.GetOperatorId(env.Host);
        var alice = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Alice Aaron", Password);
        var bob = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "bob", "Bob Baker", Password);
        var carol = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "carol", "Carol Chen", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // Baseline: with several holders standing, a demotion lands.
        var demoted = await env.Http.PutAsJsonAsync($"/operators/{bob}/scopes",
            new { scopes = new[] { "read" } });
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);

        // Take every holder besides Carol out of play: Bob was demoted
        // above, Alice is disabled, and Carol herself disables the seed.
        // Alice's task scope stands on paper but authenticates nowhere -- it
        // must not count as keeping a lockout honest.
        var disableAlice = await env.Http.PostAsync($"/operators/{alice}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disableAlice.StatusCode);

        using var carolClient = env.NewClient();
        await AuthenticatedHost.LoginAsync(carolClient, "carol", Password);
        var disableSeeded = await carolClient.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disableSeeded.StatusCode);

        // Carol is now the last standing task holder: her own demotion is
        // the lockout and is refused -- precisely because the disabled Alice
        // and seed do not count. Had they counted, this demotion would sail
        // through and leave nobody able to task, or to enable them back.
        var lastStand = await carolClient.PutAsJsonAsync($"/operators/{carol}/scopes",
            new { scopes = new[] { "read" } });
        Assert.Equal(HttpStatusCode.Conflict, lastStand.StatusCode);
    }

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
