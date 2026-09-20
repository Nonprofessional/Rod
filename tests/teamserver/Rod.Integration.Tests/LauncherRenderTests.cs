using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Staging;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the standalone launcher render (architecture.md Sec 8):
/// the same paste-ready stage-2 fetch one-liners the shell console's upgrade
/// produces, without a caught shell to grow from. The render resolves the
/// engagement's web front and newest payload (or the ones the operator
/// named), mints the deployment credential under the requested policy, and
/// answers with every downloader family -- the credential present in each
/// command exactly once.
/// </summary>
public class LauncherRenderTests
{
    [Fact]
    public async Task Render_AnswersThePreferredFrontAndNewestPayload()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var engagement = new EngagementId(Guid.Parse(engagementId));

        var port = TestSupport.GetFreeTcpPort();
        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-http",
                Transport: "http",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: "http://stage.example.test"));
        created.EnsureSuccessStatusCode();

        // Two builds, so "newest stands in" is a real choice and not the only
        // row in the store.
        var payloads = env.Host.Services.GetRequiredService<IPayloadStore>();
        var older = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await payloads.SaveAsync(Payload(older, engagement, now.AddMinutes(-5)));
        await payloads.SaveAsync(Payload(newest, engagement, now));

        var rendered = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/launchers", new { });
        rendered.EnsureSuccessStatusCode();
        var body = await rendered.Content.ReadFromJsonAsync<LauncherRenderDto>();
        Assert.NotNull(body);
        Assert.Equal(newest.ToString("N"), body!.PayloadId);
        Assert.Equal($"http://stage.example.test/implants/stage2/{newest:N}", body.Url);
        Assert.False(string.IsNullOrEmpty(body.TokenSecret));
        Assert.Contains(body.Launchers, l => l.Id == "unix-curl" && l.Command.Contains(body.Url));
        Assert.Contains(body.Launchers, l => l.Id == "unix-wget");
        Assert.Contains(body.Launchers, l => l.Id == "windows-powershell");
        Assert.All(body.Launchers, l => Assert.Contains(body.TokenSecret, l.Command));
    }

    [Fact]
    public async Task Render_HonorsTheNamedChoiceAndCredentialPolicy()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http);
        var engagement = new EngagementId(Guid.Parse(engagementId));

        // Two fronts, so the named listener resolution is a real choice: the
        // named redirector wins over the https front the preference would
        // otherwise pick.
        var httpsPort = TestSupport.GetFreeTcpPort();
        var frontPort = TestSupport.GetFreeTcpPort();
        var fronts = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-https",
                Transport: "https",
                BindAddress: $"127.0.0.1:{httpsPort}",
                PublicEndpoint: "https://hardened.example.test"));
        fronts.EnsureSuccessStatusCode();
        var redirector = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "front-http",
                Transport: "http",
                BindAddress: $"127.0.0.1:{frontPort}",
                PublicEndpoint: "http://front.example.test"));
        redirector.EnsureSuccessStatusCode();
        var frontListener = await redirector.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();

        var payloads = env.Host.Services.GetRequiredService<IPayloadStore>();
        var named = Guid.NewGuid();
        await payloads.SaveAsync(Payload(named, engagement, DateTimeOffset.UtcNow));

        // The wide credential: unlimited uses, a two-hour window -- the
        // many-host deployment posture.
        var rendered = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/launchers",
            new
            {
                PayloadId = named.ToString(),
                ListenerId = frontListener!.Id,
                MaxUses = 0,
                LifetimeMinutes = 120,
            });
        rendered.EnsureSuccessStatusCode();
        var body = await rendered.Content.ReadFromJsonAsync<LauncherRenderDto>();
        Assert.NotNull(body);
        Assert.Equal($"http://front.example.test/implants/stage2/{named:N}", body!.Url);
        Assert.True(body.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));

        // The minted credential verifies against the token store: it is a
        // real stager token under the requested policy, not a render-only
        // string.
        var tokens = env.Host.Services.GetRequiredService<IStagerTokenService>();
        var verified = await tokens.VerifyAsync(body.TokenSecret, DateTimeOffset.UtcNow);
        Assert.Equal(engagement, verified.EngagementId);

        // A nonsensical policy is refused rather than silently narrowed.
        var badPolicy = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/launchers",
            new { MaxUses = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, badPolicy.StatusCode);

        // A listener name that matches no web front of this engagement is
        // refused too.
        var badListener = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/launchers",
            new { ListenerId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, badListener.StatusCode);
    }

    private static PayloadRecord Payload(Guid id, EngagementId engagement, DateTimeOffset builtAt)
        => new(
            id, engagement.Value, "Stage2", "dotnet", "application/octet-stream",
            "sha256:test", [1, 2, 3], 3, builtAt, Target: "linux-x64");

    private sealed record LauncherRenderDto(
        string PayloadId,
        string Url,
        string TokenSecret,
        DateTimeOffset TokenExpiresAt,
        IReadOnlyList<LauncherDto> Launchers);

    private sealed record LauncherDto(string Id, string Os, string Command);

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Launchers"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    /// <summary>
    /// A real teamserver with the operator API and no startup stream
    /// listener -- the launcher route is engagement-scoped API surface, so
    /// the harness mirrors the shellcatch one.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            var httpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodListeners(new List<ListenerConfig>())
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(httpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
