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
/// Operator credential revocation over the API (architecture.md Sec 9 --
/// certificate revocation, operator half): an authenticated operator revokes
/// another's credential, and the revoked operator's next login fails without a
/// server restart -- the login path reads the stored verifier fresh on every
/// attempt. The implant half of revocation is the retirement refusal, pinned
/// by <c>HandshakeServiceTests.Handshake_RefusesRetiredImplant</c>.
/// </summary>
public class OperatorCredentialRevocationTests
{
    private const string Handle = "revoked-op";
    private const string Password = "rev0ked-p@ss";

    [Fact]
    public async Task RevokedCredential_FailsNextLogin_WithoutRestart()
    {
        await using var env = await TestEnv.StartAsync();
        var credentials = env.Host.Services.GetRequiredService<IOperatorCredentialStore>();
        var revokedId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, Handle, "Revoked Op", Password);
        await AuthenticatedHost.LoginAsync(env.Http);

        // The credential works before revocation.
        using (var fresh = env.NewClient())
        {
            await AuthenticatedHost.LoginAsync(fresh, Handle, Password);
            var me = await fresh.GetFromJsonAsync<MeBody>("/operators/me");
            Assert.Equal(Handle, me!.Handle);
        }

        // An authenticated operator revokes it.
        var revoked = await env.Http.PostAsync($"/operators/{revokedId}/credentials:revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        // The next login attempt fails -- no restart, the verifier is simply
        // gone when the attempt reads it.
        using (var after = env.NewClient())
        {
            var login = await after.PostAsJsonAsync(
                "/operators/login", new { handle = Handle, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        // Revocation is idempotent and scoped: the revoking operator's own
        // session still works.
        var again = await env.Http.PostAsync($"/operators/{revokedId}/credentials:revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var meStill = await env.Http.GetFromJsonAsync<MeBody>("/operators/me");
        Assert.Equal(AuthenticatedHost.Handle, meStill!.Handle);

        // Re-provisioning restores login (SetHashAsync is the provisioning
        // path; revocation is not account deletion).
        var hasher = env.Host.Services.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<Operator>>();
        var operators = env.Host.Services.GetRequiredService<IOperatorRepository>();
        var op = await operators.FindAsync(revokedId, CancellationToken.None);
        await credentials.SetHashAsync(revokedId, hasher.HashPassword(op!, "new-p@ss"));
        using (var restored = env.NewClient())
        {
            var login = await restored.PostAsJsonAsync(
                "/operators/login", new { handle = Handle, password = "new-p@ss" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task RevokingAnUnknownOperator_Is404()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);

        var response = await env.Http.PostAsync(
            $"/operators/{Guid.NewGuid()}/credentials:revoke", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class MeBody
    {
        public string Handle { get; set; } = "";
    }

    /// <summary>
    /// A TestServer-backed operator API host; revocation is an auth-surface
    /// concern, no beacon endpoint is involved. NewClient mints a fresh
    /// cookie-persisting client per operator session.
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
