using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// Operator provisioning over the API (architecture.md Sec 3 and Sec 9):
/// the management path the bootstrap seed stood in for, and the deployment
/// admin's alone (Sec 4.5). <c>POST /operators</c> registers a member
/// account with its initial password in one step (the new handle can log in
/// the moment the response returns), <c>GET /operators</c> lists the roster,
/// and <c>PUT /operators/{id}/credentials</c> re-provisions a password -- a
/// new credential generation that ends the target's live cookie sessions at
/// their next request.
/// </summary>
public class OperatorProvisioningTests
{
    private const string Handle = "alice";
    private const string Password = "initial-p@ss";

    private sealed record AccountBody(
        Guid Id,
        string Handle,
        DateTimeOffset CreatedAt,
        bool HasCredential,
        bool Disabled,
        string Role);

    private sealed record CreateBody(string Handle, string Password);

    [Fact]
    public async Task Create_RegistersALoginableAccount_AndListsInTheRoster()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var response = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody(Handle, Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("/operators/", response.Headers.Location?.ToString() ?? string.Empty);

        var created = await response.Content.ReadFromJsonAsync<AccountBody>();
        Assert.NotNull(created);
        Assert.Equal(Handle, created!.Handle);
        Assert.True(created.HasCredential);
        Assert.False(created.Disabled);

        // The roster carries both accounts, ordered by handle, and the
        // account is loginable straight away.
        var roster = await env.Http.GetFromJsonAsync<AccountBody[]>("/operators");
        Assert.NotNull(roster);
        var handles = roster!.Select(o => o.Handle).ToArray();
        Assert.Contains(Handle, handles);
        Assert.Contains(AuthenticatedHost.Handle, handles);
        Assert.Equal(handles.Order(StringComparer.Ordinal).ToArray(), handles);

        using var session = env.NewClient();
        await AuthenticatedHost.LoginAsync(session, Handle, Password);
        var me = await session.GetFromJsonAsync<AccountBody>("/operators/me");
        Assert.Equal(Handle, me!.Handle);
    }

    [Fact]
    public async Task Create_LeavesAFreshAccountAbleToCreateEngagements()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var created = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody("bob", Password));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // A fresh account holds no permission and needs none to start work:
        // it creates its own engagement and becomes that engagement's owner.
        using var session = env.NewClient();
        await AuthenticatedHost.LoginAsync(session, "bob", Password);
        var engagement = await session.PostAsJsonAsync("/engagements",
            new { name = "Operation Newcomer" });
        Assert.Equal(HttpStatusCode.Created, engagement.StatusCode);
    }

    [Fact]
    public async Task Create_RefusesDuplicatesAndInvalidInput()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var first = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody(Handle, Password));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A handle is one account: the second claim is a conflict, not a
        // second row.
        var duplicate = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody(Handle, "another-p@ss"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // A short password is refused before anything is stored, as are the
        // missing fields.
        var shortPassword = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody("dave", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var missingHandle = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody(" ", Password));
        Assert.Equal(HttpStatusCode.BadRequest, missingHandle.StatusCode);

        var missingPassword = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody("dave", ""));
        Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);

        // None of the refused attempts left an account behind.
        var roster = await env.Http.GetFromJsonAsync<AccountBody[]>("/operators");
        Assert.DoesNotContain(roster!, o => o.Handle == "dave");
    }

    [Fact]
    public async Task Provisioning_IsAdminOnly_WhileAnonymousReadsRefuse()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.RegisterOperatorAsync(env.Host, "colleague", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // Anonymous requests refuse at the session; a member is
        // authenticated and still forbidden -- provisioning hands out
        // accounts, which is the deployment admin's alone (Sec 4.5).
        using var anonymous = env.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/operators", new CreateBody("eve", Password))).StatusCode);

        using var colleague = env.NewClient();
        await AuthenticatedHost.LoginAsync(colleague, "colleague", Password);
        var refused = await colleague.PostAsJsonAsync("/operators",
            new CreateBody("eve", Password));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The admin creates the same account without friction.
        var provision = await env.Http.PostAsJsonAsync("/operators",
            new CreateBody("eve", Password));
        Assert.Equal(HttpStatusCode.Created, provision.StatusCode);
    }

    [Fact]
    public async Task SetCredential_ReplacesThePassword_AndEndsTheTargetLiveSessions()
    {
        await using var env = await TestEnv.StartAsync();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        using var session = env.NewClient();
        await AuthenticatedHost.LoginAsync(session, Handle, Password);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/operators/me")).StatusCode);

        // The reset is a new credential generation: the target's live cookie
        // fails its stamp check at the next request, and the new password
        // logs in from then on.
        const string rotated = "rotated-p@ss2";
        var reset = await env.Http.PutAsJsonAsync($"/operators/{targetId}/credentials",
            new { password = rotated });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/operators/me")).StatusCode);

        using var relogged = env.NewClient();
        await AuthenticatedHost.LoginAsync(relogged, Handle, rotated);
        Assert.Equal(HttpStatusCode.OK, (await relogged.GetAsync("/operators/me")).StatusCode);

        // The old password is gone, and the input rules match provisioning.
        using (var stale = env.NewClient())
        {
            var oldPassword = await stale.PostAsJsonAsync("/operators/login",
                new { handle = Handle, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        var shortPassword = await env.Http.PutAsJsonAsync($"/operators/{targetId}/credentials",
            new { password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var unknown = await env.Http.PutAsJsonAsync("/operators/00000000-0000-0000-0000-000000000000/credentials",
            new { password = rotated });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
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
