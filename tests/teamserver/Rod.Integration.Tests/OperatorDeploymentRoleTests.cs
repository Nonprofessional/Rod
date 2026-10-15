using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Operators;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// The deployment-role split of the account machinery (architecture.md
/// Sec 4.5): the bootstrap-seeded account is admin, every console-created
/// account is a member, and the two draw different lines. A member draws 403
/// on every other-operator account action -- provisioning included -- and
/// keeps self-service over its own password and tokens; the admin's actions
/// go through. The role rides both credential shapes: the cookie session and
/// the bearer token stamp the same claim at login.
/// </summary>
public class OperatorDeploymentRoleTests
{
    private const string MemberHandle = "bob";
    private const string MemberPassword = "member-p@ss";
    private const string TargetHandle = "colleague";
    private const string TargetPassword = "target-p@ss";

    private sealed record AccountBody(
        Guid Id,
        string Handle,
        DateTimeOffset CreatedAt,
        bool HasCredential,
        bool Disabled,
        string Role);

    private sealed record MeBody(Guid Id, string Handle, string Role);

    private sealed record MintedTokenBody(string TokenId, string Token, DateTimeOffset CreatedAt);

    private sealed record TokenRowBody(string TokenId, DateTimeOffset CreatedAt);

    [Fact]
    public async Task ConsoleCreatedAccounts_ComeUpAsMembers_AndTheRosterNamesBothRoles()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        // The console (POST /operators) is the member factory: the created
        // account states its role in the response.
        var created = await env.Http.PostAsJsonAsync("/operators",
            new { handle = MemberHandle, password = MemberPassword });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<AccountBody>();
        Assert.Equal("member", account!.Role);

        // The roster names both roles: the bootstrap seed is the admin.
        var roster = await env.Http.GetFromJsonAsync<AccountBody[]>("/operators");
        Assert.Equal("admin", Assert.Single(roster!, o => o.Handle == AuthenticatedHost.Handle).Role);
        Assert.Equal("member", Assert.Single(roster!, o => o.Handle == MemberHandle).Role);

        // The member reads its own summary with the same role.
        using var member = env.NewClient();
        await AuthenticatedHost.LoginAsync(member, MemberHandle, MemberPassword);
        var me = await member.GetFromJsonAsync<MeBody>("/operators/me");
        Assert.Equal("member", me!.Role);
    }

    [Fact]
    public async Task Members_Draw403OnEveryOtherOperatorAction_WhileTheAdminDoesNot()
    {
        await using var env = await TestEnv.StartAsync();
        var adminId = AuthenticatedHost.GetOperatorId(env.Host);
        await AuthenticatedHost.LoginAsync(env.Http);

        // A console-created member, and a second member as the action
        // target: the same route set with someone else's id in it.
        var created = await env.Http.PostAsJsonAsync("/operators",
            new { handle = MemberHandle, password = MemberPassword });
        var memberId = (await created.Content.ReadFromJsonAsync<AccountBody>())!.Id;
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(
            env.Host, TargetHandle, TargetPassword);

        using var member = env.NewClient();
        await AuthenticatedHost.LoginAsync(member, MemberHandle, MemberPassword);

        // Provisioning is management: refused for the member.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/operators",
                new { handle = "eve", password = "eve-p@ssword" })).StatusCode);

        // The deployment-wide roster is management too: the member reads its
        // own summary (/operators/me) and the engagement rosters it belongs
        // to, never the account list.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync("/operators")).StatusCode);

        // Every other-operator account action draws 403 -- on the target and
        // on the admin alike; the member's own disable is refused too (the
        // switch is not self-service).
        foreach (var id in new[] { targetId.Value.ToString(), adminId.Value.ToString() })
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PutAsJsonAsync($"/operators/{id}/credentials",
                    new { password = "takeover-p@ss" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PostAsync($"/operators/{id}/credentials:revoke", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PostAsync($"/operators/{id}:disable", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PostAsync($"/operators/{id}:enable", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PostAsync($"/operators/{id}/tokens", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.GetAsync($"/operators/{id}/tokens")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await member.PostAsync($"/operators/{id}/tokens/{Guid.NewGuid()}:revoke", content: null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/operators/{memberId}:disable", content: null)).StatusCode);

        // The admin performs the same actions without friction.
        Assert.Equal(HttpStatusCode.OK, (await env.Http.GetAsync("/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PutAsJsonAsync($"/operators/{targetId}/credentials",
                new { password = "rotated-p@ss" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PostAsync($"/operators/{targetId}/credentials:revoke", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PostAsync($"/operators/{targetId}:disable", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PostAsync($"/operators/{targetId}:enable", content: null)).StatusCode);
        var minted = await env.Http.PostAsync($"/operators/{targetId}/tokens", content: null);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var token = (await minted.Content.ReadFromJsonAsync<MintedTokenBody>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.GetAsync($"/operators/{targetId}/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PostAsync($"/operators/{targetId}/tokens/{token.TokenId}:revoke", content: null)).StatusCode);

        // The role rides the bearer path too: the member's own token (minted
        // as self-service below) authenticates and still draws 403 where the
        // claim is required.
        var own = await member.PostAsync($"/operators/{memberId}/tokens", content: null);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownToken = (await own.Content.ReadFromJsonAsync<MintedTokenBody>())!;
        using var bearer = env.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownToken.Token);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/operators/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bearer.PostAsJsonAsync("/operators",
                new { handle = "eve", password = "eve-p@ssword" })).StatusCode);
    }

    [Fact]
    public async Task Members_KeepSelfService_OverTheirOwnPasswordAndTokens()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/operators",
            new { handle = MemberHandle, password = MemberPassword });
        var memberId = (await created.Content.ReadFromJsonAsync<AccountBody>())!.Id;

        using var member = env.NewClient();
        await AuthenticatedHost.LoginAsync(member, MemberHandle, MemberPassword);

        // Password self-service proves possession: neither a bare session nor
        // a wrong guess may rotate the credential behind it, and a refused
        // attempt leaves the session standing.
        const string rotated = "rotated-p@ss";
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync($"/operators/{memberId}/credentials",
                new { password = rotated })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync($"/operators/{memberId}/credentials",
                new { password = rotated, currentPassword = "wrong-p@ss" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/operators/me")).StatusCode);

        // With the current password stated, the change lands: it ends the
        // caller's own session at its next request (a new credential
        // generation), and the new password logs straight back in.
        Assert.Equal(HttpStatusCode.OK,
            (await member.PutAsJsonAsync($"/operators/{memberId}/credentials",
                new { password = rotated, currentPassword = MemberPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/operators/me")).StatusCode);
        await AuthenticatedHost.LoginAsync(member, MemberHandle, rotated);

        // Token self-service: mint, list, revoke -- on the member's own
        // account, with the secret shown once at mint.
        var minted = await member.PostAsync($"/operators/{memberId}/tokens", content: null);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var token = (await minted.Content.ReadFromJsonAsync<MintedTokenBody>())!;
        var listed = await member.GetFromJsonAsync<TokenRowBody[]>($"/operators/{memberId}/tokens");
        Assert.Contains(listed!, row => row.TokenId == token.TokenId);
        Assert.Equal(HttpStatusCode.OK,
            (await member.PostAsync($"/operators/{memberId}/tokens/{token.TokenId}:revoke", content: null)).StatusCode);
        listed = await member.GetFromJsonAsync<TokenRowBody[]>($"/operators/{memberId}/tokens");
        Assert.DoesNotContain(listed!, row => row.TokenId == token.TokenId);

        // Credential self-revocation: the account exists but no password
        // verifies, so the next login refuses until an admin re-provisions.
        Assert.Equal(HttpStatusCode.OK,
            (await member.PostAsync($"/operators/{memberId}/credentials:revoke", content: null)).StatusCode);
        using var stale = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.PostAsJsonAsync("/operators/login",
                new { handle = MemberHandle, password = rotated })).StatusCode);
    }

    [Fact]
    public async Task Members_Draw403OnFleetAndSettingsWrites_WhileReadsStayOpen()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/operators",
            new { handle = MemberHandle, password = MemberPassword });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var member = env.NewClient();
        await AuthenticatedHost.LoginAsync(member, MemberHandle, MemberPassword);

        // The reads are deployment state every operator may see: the fleet
        // roster, the credential ledger, both settings.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/redirectors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/redirectors/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/settings/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/settings/build")).StatusCode);

        // The writes are the admin's alone. The claim gate answers before
        // any handler runs, so even an unknown id or a blank body draws 403.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/redirectors/credentials", new { name = "edge" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/redirectors/credentials/{Guid.NewGuid()}:revoke", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/redirectors/{Guid.NewGuid()}:retire", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/settings/sessions",
                new { thresholdMinutes = 10, sweepIntervalMinutes = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/settings/build", new { rustTargetDir = "" })).StatusCode);

        // The admin performs the same writes: a minted credential, its
        // revocation, and both settings applied live.
        var minted = await env.Http.PostAsJsonAsync("/redirectors/credentials", new { name = "edge" });
        Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        var credentialId = (await minted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent,
            (await env.Http.PostAsync($"/redirectors/credentials/{credentialId}:revoke", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PutAsJsonAsync("/settings/sessions",
                new { thresholdMinutes = 10, sweepIntervalMinutes = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await env.Http.PutAsJsonAsync("/settings/build", new { rustTargetDir = "" })).StatusCode);
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
