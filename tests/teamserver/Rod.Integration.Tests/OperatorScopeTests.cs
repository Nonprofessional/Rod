using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Operators;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// Operator scopes over the API (architecture.md Sec 4.5): the assignment
/// route's guards (the acting scope required of the caller, the read-beneath-
/// acting validation, the last-task-holder lockout) and the live-cookie
/// delivery -- a scope change reaches the target's session at its next
/// request, demotion and promotion both, with no re-login.
/// </summary>
public class OperatorScopeTests
{
    private const string Handle = "scoped-op";
    private const string Password = "sc0ped-p@ss";

    private sealed record SummaryBody(Guid Id, string Handle, string DisplayName, string[] Scopes);

    [Fact]
    public async Task LoginAndMe_CarryTheScopeSet()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var me = await env.Http.GetFromJsonAsync<SummaryBody>("/operators/me");
        Assert.NotNull(me);
        Assert.Equal(new[] { "read", "task", "approve" }, me!.Scopes);
    }

    [Fact]
    public async Task Assignment_ValidatesTheSet_AndGatesOnTheActingScope()
    {
        await using var env = await TestEnv.StartAsync();
        var operators = env.Host.Services.GetRequiredService<IOperatorRepository>();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Scoped Op", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // An unknown scope name is a client error.
        var unknown = await env.Http.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "root" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Acting without reading is refused: the set is incoherent, not just
        // narrow.
        var incoherent = await env.Http.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "task" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, incoherent.StatusCode);

        // The coherent narrowing lands.
        var narrowed = await env.Http.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read" } });
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        var narrowedBody = await narrowed.Content.ReadFromJsonAsync<SummaryBody>();
        Assert.Equal(new[] { "read" }, narrowedBody!.Scopes);

        // A read-only operator cannot widen themselves -- the route requires
        // the acting scope of its caller.
        using (var readOnly = env.NewClient())
        {
            await AuthenticatedHost.LoginAsync(readOnly, Handle, Password);
            var selfWiden = await readOnly.PutAsJsonAsync(
                $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "task" } });
            Assert.Equal(HttpStatusCode.Forbidden, selfWiden.StatusCode);
        }

        // The store carries the narrowed set (the API is not the only reader).
        var stored = await operators.FindAsync(targetId, CancellationToken.None);
        Assert.Equal(OperatorScope.Read, stored!.Scopes);
    }

    [Fact]
    public async Task Assignment_RefusesToRemoveTheLastTaskHolder()
    {
        await using var env = await TestEnv.StartAsync();
        var operators = env.Host.Services.GetRequiredService<IOperatorRepository>();
        // The seeded operator is the only loginable task holder -- the
        // synthetic automation row holds no scopes and never counts.
        var seeded = (await operators.ListAsync()).Single(o => o.Handle == AuthenticatedHost.Handle);
        Assert.DoesNotContain((await operators.ListAsync()), o => o.Scopes.HasFlag(OperatorScope.Task) && o.Handle != AuthenticatedHost.Handle);
        await AuthenticatedHost.LoginAsync(env.Http);

        // The seeded operator is the only task holder: narrowing it away is
        // the lockout, refused.
        var refused = await env.Http.PutAsJsonAsync(
            $"/operators/{seeded.Id}/scopes", new { scopes = new[] { "read" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // A second task holder stands, and the narrowing lands.
        var second = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Scoped Op", Password);
        var demoted = await env.Http.PutAsJsonAsync(
            $"/operators/{seeded.Id}/scopes", new { scopes = new[] { "read", "approve" } });
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);

        // The newcomer is the last task holder now: same refusal, other side.
        using (var holder = env.NewClient())
        {
            await AuthenticatedHost.LoginAsync(holder, Handle, Password);
            var lastRemoval = await holder.PutAsJsonAsync(
                $"/operators/{second}/scopes", new { scopes = new[] { "read" } });
            Assert.Equal(HttpStatusCode.Conflict, lastRemoval.StatusCode);
        }
    }

    [Fact]
    public async Task ScopeChange_ReachesTheLiveCookieAtTheNextRequest()
    {
        await using var env = await TestEnv.StartAsync();
        var targetId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Scoped Op", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        using var session = env.NewClient();
        await AuthenticatedHost.LoginAsync(session, Handle, Password);

        // Full scopes: the acting-scoped route answers.
        var before = await session.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "task", "approve" } });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Demoted through the other session: the target's next request on the
        // old cookie carries the new set -- no re-login, no stale elevation.
        await env.Http.PutAsJsonAsync($"/operators/{targetId}/scopes", new { scopes = new[] { "read" } });
        var after = await session.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "task" } });
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);

        var me = await session.GetFromJsonAsync<SummaryBody>("/operators/me");
        Assert.Equal(new[] { "read" }, me!.Scopes);

        // Promoted back: the same cookie picks the wider set up again.
        await env.Http.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "task", "approve" } });
        var restored = await session.PutAsJsonAsync(
            $"/operators/{targetId}/scopes", new { scopes = new[] { "read", "task", "approve" } });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    /// <summary>
    /// The auth-surface shape every operator-session suite uses: the
    /// in-memory TestServer with the operator layers composed, a cookie-
    /// persisting client for the acting operator, and fresh clients per
    /// additional operator session.
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
