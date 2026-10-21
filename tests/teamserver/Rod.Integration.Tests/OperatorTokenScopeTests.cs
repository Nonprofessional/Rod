using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Operators.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the API token's scope floor (architecture.md Sec 15.3,
/// ADR-006): a token authenticates as its operator, but the scope it was
/// minted at caps the credential -- read reads, operate acts inside
/// engagements, admin additionally mutates the deployment's management
/// surface. A signed-in operator's cookie session is untouched: the
/// deployment role and the engagement memberships keep governing as they did.
/// </summary>
public class OperatorTokenScopeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AReadToken_ReadsButCannotAct()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);
        var token = await MintAsync(client, operatorId.ToString(), scope: null);

        Assert.Equal("read", token.Scope);

        var api = BearerClient(host, token.Token);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/engagements")).StatusCode);

        var denied = await api.PostAsJsonAsync("/engagements",
            new { name = "Operation Denied" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task AnOperateToken_ActsInsideEngagementsButNotOnTheDeployment()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);
        var token = await MintAsync(client, operatorId.ToString(), "operate");

        var api = BearerClient(host, token.Token);
        var created = await api.PostAsJsonAsync("/engagements",
            new { name = "Operation Scoped" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The deployment's management surface (operator accounts) takes admin.
        var provision = await api.PostAsJsonAsync("/operators",
            new { handle = "scoped-member", password = "n0t-a-short-one" });
        Assert.Equal(HttpStatusCode.Forbidden, provision.StatusCode);
    }

    [Fact]
    public async Task AnAdminToken_ReachestheManagementSurface()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);
        var token = await MintAsync(client, operatorId.ToString(), "admin");

        var api = BearerClient(host, token.Token);
        // The seeded operator is the bootstrap admin, so the admin-scoped
        // token passes both the scope floor and the deployment-role policy.
        var roster = await api.GetAsync("/operators");
        Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
    }

    [Fact]
    public async Task TheCookieSession_IsUnscopedAndUnaffected()
    {
        var (client, _, _) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var created = await client.PostAsJsonAsync("/engagements",
            new { name = "Operation Session" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task TheMint_ReceivesItsScopeExplicitly()
    {
        var (client, _, operatorId) = AuthenticatedHost.Create();
        await AuthenticatedHost.LoginAsync(client);

        var unknown = await client.PostAsJsonAsync($"/operators/{operatorId}/tokens",
            new { scope = "root" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        await MintAsync(client, operatorId.ToString(), "operate");
        var listed = await client.GetFromJsonAsync<TokenRow[]>($"/operators/{operatorId}/tokens");
        Assert.NotNull(listed);
        Assert.Contains(listed!, row => row.Scope == "operate");
    }

    private static async Task<MintedToken> MintAsync(
        HttpClient client, string operatorId, string? scope)
    {
        var response = await client.PostAsJsonAsync($"/operators/{operatorId}/tokens",
            scope is null ? null : new { scope });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MintedToken>(Json)
            ?? throw new InvalidOperationException("The mint response was empty.");
    }

    private static HttpClient BearerClient(IHost host, string secret)
    {
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private sealed record MintedToken(string TokenId, string Scope, string Token);

    private sealed record TokenRow(string TokenId, string Scope);
}
