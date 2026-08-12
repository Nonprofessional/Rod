using System.Net;
using System.Net.Http.Json;
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
    private const string DisplayName = "Auth Operator";
    private const string Password = "p@ssw0rd!";

    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operators:Initial:Handle"] = Handle,
                ["Operators:Initial:DisplayName"] = DisplayName,
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
            Assert.Equal(DisplayName, summary.DisplayName);
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
            Assert.Equal(DisplayName, summary.DisplayName);
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
}
