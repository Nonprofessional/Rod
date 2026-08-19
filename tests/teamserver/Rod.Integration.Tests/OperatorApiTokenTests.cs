using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Operators;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// Operator API tokens over the API (architecture.md Sec 9 -- the identity
/// model's API tokens): a token minted per operator authenticates the operator
/// API through a bearer header alongside the cookie session, and a revoked
/// token is refused on its next request -- the same immediate-effect,
/// no-restart revocation shape the password credential keeps. A token is
/// independent of the password: rotating one credential never silently
/// invalidates the other.
/// </summary>
public class OperatorApiTokenTests
{
    [Fact]
    public async Task MintedToken_AuthenticatesTheApi_AndRevocationRefusesIt()
    {
        await using var env = await TestEnv.StartAsync();
        var operatorId = AuthenticatedHost.GetOperatorId(env.Host);

        // Mint through the authenticated API. The secret is shown exactly once.
        await AuthenticatedHost.LoginAsync(env.Http);
        var minted = await env.Http.PostAsync($"/operators/{operatorId}/tokens", content: null);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var token = await minted.Content.ReadFromJsonAsync<MintedBody>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrEmpty(token!.Token));

        // An operator-API call authenticated by the minted token alone -- no
        // cookie, a fresh client carrying only the bearer header -- succeeds and
        // resolves to the same operator.
        using var bearer = env.NewClient();
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        var me = await bearer.GetAsync("/operators/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadFromJsonAsync<MeBody>();
        Assert.Equal(operatorId.Value, meBody!.Id);

        // The listing carries the token's identity without the secret.
        var listed = await bearer.GetFromJsonAsync<TokenBody[]>($"/operators/{operatorId}/tokens");
        Assert.NotNull(listed);
        Assert.Contains(listed!, t => t.TokenId == token.TokenId);

        // Revoke, then the same call is refused: the digest is read fresh per
        // request, so revocation takes effect immediately.
        var revoked = await env.Http.PostAsync(
            $"/operators/{operatorId}/tokens/{token.TokenId}:revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var refused = await bearer.GetAsync("/operators/me");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // The cookie session never depended on the token: it still works.
        var cookieStill = await env.Http.GetFromJsonAsync<MeBody>("/operators/me");
        Assert.Equal(operatorId.Value, cookieStill!.Id);
    }

    [Fact]
    public async Task Tokens_AreIndependentOfThePasswordCredential()
    {
        // Revoking the password ends cookie sessions (the session stamp) but
        // leaves minted tokens working, and revoking a token leaves cookie
        // sessions working: each credential revokes through its own route, so
        // rotating one never silently invalidates the other.
        await using var env = await TestEnv.StartAsync();
        var operatorId = AuthenticatedHost.GetOperatorId(env.Host);

        await AuthenticatedHost.LoginAsync(env.Http);
        var minted = await env.Http.PostAsync($"/operators/{operatorId}/tokens", content: null);
        var token = await minted.Content.ReadFromJsonAsync<MintedBody>();

        // Revoke the password credential: the live cookie session ends at its
        // next request...
        var revoked = await env.Http.PostAsync($"/operators/{operatorId}/credentials:revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Http.GetAsync("/operators/me")).StatusCode);

        // ...but the minted token still authenticates.
        using var bearer = env.NewClient();
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.Token);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/operators/me")).StatusCode);
    }

    private sealed class MintedBody
    {
        public string TokenId { get; set; } = "";
        public string Token { get; set; } = "";
    }

    private sealed class TokenBody
    {
        public string TokenId { get; set; } = "";
    }

    private sealed class MeBody
    {
        public Guid Id { get; set; }
    }

    /// <summary>
    /// A TestServer-backed operator API host; token authentication is an
    /// auth-surface concern, no beacon endpoint is involved. NewClient mints a
    /// fresh cookie-persisting client per operator session.
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
