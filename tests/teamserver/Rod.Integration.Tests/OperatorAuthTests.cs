using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Operators;
using Rod.Operators.Auth;
using Rod.Operators.Endpoints;
using Rod.Transport;

namespace Rod.Integration.Tests;

/// <summary>
/// Operator authentication (architecture.md Sec 4, the production-hardening
/// todo): the cookie session is established by verified credentials, not a
/// client-generated id. Drives <c>POST /operators/login</c>,
/// <c>POST /operators/logout</c>, and <c>GET /operators/me</c> through the
/// in-memory TestServer with the operator layer and the operator-auth layer
/// composed onto the transport core -- the same composition the teamserver host
/// performs. The initial operator is seeded from configuration so the test does
/// not depend on the Development fallback.
/// </summary>
public class OperatorAuthTests
{
    private const string Handle = "auth";
    private const string Password = "p@ssw0rd!";

    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operators:Initial:Handle"] = Handle,
                ["Operators:Initial:Password"] = Password,
            })
            .Build();

        IHost host = TransportHost.CreateHostBuilder(
                configureServices: services =>
                {
                    services.AddRodOperators();
                    services.AddRodOperatorAuth(config);
                },
                mapEndpoints: endpoints =>
                {
                    endpoints.MapOperatorEndpoints();
                    endpoints.MapOperatorAuthEndpoints();
                },
                configuration: config)
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = new HttpClient(new CookieHandler(server.CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return (client, host);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string handle, string password)
        => client.PostAsJsonAsync("/operators/login", new LoginRequest(handle, password));

    [Fact]
    public async Task AccountAndCredentialChanges_LandInTheDeploymentTrail()
    {
        // The deployment bucket: account and credential changes bind to no
        // engagement, but they are the highest-privilege actions the
        // platform takes -- "who created that account" must be answerable
        // from a record, not from the server log. The trail under the empty
        // engagement id carries them, admin-only to read.
        var (client, host) = CreateClient();
        using (host)
        {
            await LoginAsync(client, Handle, Password);

            var created = await client.PostAsJsonAsync("/operators",
                new { handle = "newcomer", password = "first-p@ss!" });
            created.EnsureSuccessStatusCode();

            var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
            var newId = createdBody.GetProperty("id").GetString();
            var disabled = await client.PostAsync($"/operators/{newId}:disable", null);
            disabled.EnsureSuccessStatusCode();

            var minted = await client.PostAsync($"/operators/{newId}/tokens", null);
            minted.EnsureSuccessStatusCode();

            var trail = await client.GetFromJsonAsync<JsonElement>("/audit/deployment?limit=50");
            var items = trail.GetProperty("items");
            var verbs = items.EnumerateArray()
                .Where(e => e.GetProperty("verb").GetString()!.StartsWith("operator."))
                .Select(e => e.GetProperty("verb").GetString())
                .ToArray();
            Assert.Contains("operator.created", verbs);
            Assert.Contains("operator.disabled", verbs);
            Assert.Contains("operator.token-minted", verbs);
        }
    }

    [Fact]
    public async Task Login_WithCorrectPassword_ReturnsOperator()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            var response = await LoginAsync(client, Handle, Password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var summary = await response.Content.ReadFromJsonAsync<OperatorAuthSummary>();
            Assert.NotNull(summary);
            Assert.Equal(Handle, summary!.Handle);
            // The seed's display name is derived from its handle.
            Assert.Equal(Handle, summary.DisplayName);
            Assert.Equal("admin", summary.Role);
            Assert.NotEqual(Guid.Empty, summary.Id);
        }
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsUnauthorized()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            var response = await LoginAsync(client, Handle, "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_WithUnknownHandle_IsUnauthorized()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            var response = await LoginAsync(client, "nobody", Password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Me_WithoutSession_IsUnauthorized()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            // Fresh client -- no login, so no cookie.
            var response = await client.GetAsync("/operators/me");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Me_WithSession_ReturnsOperator()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            await LoginAsync(client, Handle, Password);

            var response = await client.GetAsync("/operators/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var summary = await response.Content.ReadFromJsonAsync<OperatorAuthSummary>();
            Assert.NotNull(summary);
            Assert.Equal(Handle, summary!.Handle);
            Assert.Equal(Handle, summary.DisplayName);
        }
    }

    [Fact]
    public async Task Logout_ClearsSession()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            await LoginAsync(client, Handle, Password);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/operators/me")).StatusCode);

            var logout = await client.PostAsync("/operators/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

            // The session is gone: /me is unauthorized again.
            var after = await client.GetAsync("/operators/me");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
    }

    [Fact]
    public async Task Login_EntersCooldown_AfterRepeatedFailures_And429s()
    {
        var (client, host) = CreateClient();
        using (host)
        {
            // Five wrong passwords put the handle into cooldown; the sixth
            // attempt is refused before the credential store is touched.
            for (var i = 0; i < 5; i++)
            {
                var failed = await PostLoginAsync(client, Handle, "wrong-password");
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }

            var refused = await PostLoginAsync(client, Handle, Password);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }
    }

    [Fact]
    public void LoginThrottle_CooldownExpires_AndResetClearsIt()
    {
        var clock = new MutableTimeProvider();
        var throttle = new LoginThrottle(clock);

        for (var i = 0; i < 5; i++)
            throttle.RecordFailure("op");
        Assert.False(throttle.IsAllowed("op"));

        // A stale window starts fresh: allowed again, and one failure within
        // the new window does not re-enter cooldown.
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(throttle.IsAllowed("op"));
        throttle.RecordFailure("op");
        Assert.True(throttle.IsAllowed("op"));

        // A success clears the counter outright.
        throttle.Reset("op");
        Assert.True(throttle.IsAllowed("op"));
    }

    [Fact]
    public void LoginThrottle_AnAddressSprayingManyHandles_EntersItsOwnCooldown()
    {
        // The per-handle axis never notices a spray: each handle fails
        // once. The wider per-address budget must catch that shape, while
        // staying loose enough that a shared NAT's honest failures do not
        // lock everyone out.
        var clock = new MutableTimeProvider();
        var throttle = new LoginThrottle(clock);

        for (var i = 0; i < 20; i++)
        {
            var handle = "spray-" + i;
            Assert.True(throttle.IsAllowed(handle, "10.9.9.9"));
            throttle.RecordFailure(handle, "10.9.9.9");
        }

        // The address is in cooldown; a different address and an untouched
        // handle from it still pass.
        Assert.False(throttle.IsAllowed("fresh-handle", "10.9.9.9"));
        Assert.True(throttle.IsAllowed("fresh-handle", "10.8.8.8"));

        // The window passing lifts the address cooldown like the handle's.
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(throttle.IsAllowed("fresh-handle", "10.9.9.9"));
    }

    [Fact]
    public async Task Login_UpgradesAnOldParameterHash_InPlace()
    {
        // A hash stored under older KDF parameters still verifies, and the
        // successful login upgrades it to the configured strength -- the
        // stored hash converges without a migration step.
        var (client, host) = CreateClient();
        using (host)
        {
            var credentials = host.Services.GetRequiredService<Rod.CoreState.Operators.IOperatorCredentialStore>();
            var operators = host.Services.GetRequiredService<Rod.CoreState.Operators.IOperatorRepository>();
            var op = await operators.FindByHandleAsync(Handle);
            Assert.NotNull(op);

            // Re-store the credential under a weak hasher, the shape of a
            // hash written before the iteration count was raised.
            var weak = new Microsoft.AspNetCore.Identity.PasswordHasher<Rod.CoreState.Operators.Operator>(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.AspNetCore.Identity.PasswordHasherOptions()));
            await credentials.SetHashAsync(op!.Id, weak.HashPassword(op, Password));

            var response = await PostLoginAsync(client, Handle, Password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The stored hash now carries the configured iteration count --
            // the .NET PBKDF2 frame is marker, PRF, then the big-endian
            // iteration count at bytes 5..8 -- not the weak default it was
            // stored under.
            var stored = await credentials.FindHashAsync(op.Id);
            Assert.NotNull(stored);
            var frame = Convert.FromBase64String(stored!);
            Assert.Equal(0x01, frame[0]);
            var iterations = (uint)(frame[5] << 24 | frame[6] << 16 | frame[7] << 8 | frame[8]);
            Assert.Equal(600_000u, iterations);
        }
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string handle, string password)
    {
        var payload = new { handle, password };
        return await client.PostAsJsonAsync("/operators/login", payload);
    }
}

/// <summary>
/// A manually advanced clock for the login throttle unit tests; the throttle
/// windows are minutes long, so the test steps time forward instead of waiting.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
