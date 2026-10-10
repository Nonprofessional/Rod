using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// The account off switch over the API (architecture.md Sec 3 and Sec 9): a
/// disabled operator authenticates nowhere -- login fails indistinguishably
/// from an unknown handle, live cookie sessions reject at their next request,
/// and API tokens refuse -- until enabled again, whereupon the account picks
/// up exactly the memberships it kept through the disable. The one guard is
/// the lockout: disabling the last enabled account would leave nobody able
/// to enable it back.
/// </summary>
public class OperatorDisableTests
{
    private const string Handle = "alice";
    private const string Password = "disable-p@ss";

    private sealed record AccountBody(
        Guid Id,
        string Handle,
        string DisplayName,
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
        // next use, and the roster names the account disabled.
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

        // The restore twin: every path reopens. What the account may reach
        // was never the flag's business -- its memberships waited untouched.
        var enable = await env.Http.PostAsync($"/operators/{targetId}:enable", content: null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.False((await enable.Content.ReadFromJsonAsync<StatusBody>())!.Disabled);

        using var relogged = env.NewClient();
        await AuthenticatedHost.LoginAsync(relogged, Handle, Password);
        Assert.Equal(HttpStatusCode.OK, (await relogged.GetAsync("/operators/me")).StatusCode);
    }

    [Fact]
    public async Task Disable_RefusesTheLastEnabledAccount_AndIsIdempotent()
    {
        await using var env = await TestEnv.StartAsync();
        var seeded = AuthenticatedHost.GetOperatorId(env.Host);
        await AuthenticatedHost.LoginAsync(env.Http);

        // The seeded operator is the only enabled account besides the
        // credential-less automation row: disabling it is the lockout.
        var refused = await env.Http.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // A second account stands: Alice disables the seed from her own
        // session (the seeded session dies with the disable, at its very
        // next request), and both directions are idempotent on repeat.
        var second = await env.Http.PostAsJsonAsync("/operators",
            new { handle = Handle, password = Password });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondId = (await second.Content.ReadFromJsonAsync<AccountBody>())!.Id;

        using var alice = env.NewClient();
        await AuthenticatedHost.LoginAsync(alice, Handle, Password);
        var disabled = await alice.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var again = await alice.PostAsync($"/operators/{seeded}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Alice is the last enabled account now: same refusal, other side --
        // her own disable included. Enabling a not-disabled account is the
        // idempotent no-op.
        var lastRefusal = await alice.PostAsync($"/operators/{secondId}:disable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, lastRefusal.StatusCode);
        var enableNoop = await alice.PostAsync($"/operators/{secondId}:enable", content: null);
        Assert.Equal(HttpStatusCode.OK, enableNoop.StatusCode);
    }

    [Fact]
    public async Task Disable_IsTrustedOperator_AndRefusesUnknownAccounts()
    {
        await using var env = await TestEnv.StartAsync();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Alice Aaron", Password);
        await AuthenticatedHost.RegisterOperatorAsync(env.Host, "colleague", "Colleague", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // Anonymous requests refuse at the session; any authenticated
        // operator may flip the switch -- accounts are identity, not
        // permission, and the lockout guard is the last-account line.
        using var anonymous = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/operators/{targetId}:disable", content: null)).StatusCode);

        using var colleague = env.NewClient();
        await AuthenticatedHost.LoginAsync(colleague, "colleague", Password);
        var disabled = await colleague.PostAsync($"/operators/{targetId}:disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await env.Http.PostAsync("/operators/00000000-0000-0000-0000-000000000000:disable", content: null))
            .StatusCode);
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
