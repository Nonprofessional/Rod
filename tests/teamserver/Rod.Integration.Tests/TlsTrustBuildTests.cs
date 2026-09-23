using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;

namespace Rod.Integration.Tests;

/// <summary>
/// The TLS trust posture's home and gates (architecture.md Sec 9): the
/// listener records whose certificate its front presents -- the engagement
/// CA, or a real-domain chain an operator-run edge terminates -- and every
/// build against it inherits the posture as the roots it bakes. The build
/// request's old knob may only agree with the front (the fact moved to the
/// listener, where the certificate is deployed); public rides an https dial
/// alone, and the walk's other TLS fronts (carriers, fallbacks) must share
/// the posture -- the artifact bakes one root set, and a front presenting
/// the other certificate is a dial the walk cannot verify.
/// </summary>
public class TlsTrustBuildTests
{
    /// <summary>
    /// A real-socket host: the operator API rides a loopback Kestrel port
    /// while runtime listener creates bind through the endpoint reloader,
    /// which a TestServer host cannot do -- https fronts need real sockets.
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

    private static async Task<string> CreateEngagementAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/engagements", new { Name = name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EngagementBody>();
        return body!.EngagementId;
    }

    private sealed class EngagementBody
    {
        public string EngagementId { get; set; } = "";
    }

    private static async Task<string> CreateListenerAsync(
        HttpClient client, string engagementId, string transport, string bindAddress, string publicEndpoint,
        string? trust = null)
    {
        var created = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: $"trust-{transport}",
                Transport: transport,
                BindAddress: bindAddress,
                PublicEndpoint: publicEndpoint,
                Trust: trust));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        return listener!.Id;
    }

    [Fact]
    public async Task TheRequestKnobMayOnlyAgreeWithTheFront()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http, "trust-knob");

        // A socket front to build against: pinned by construction (no TLS
        // posture of its own to carry).
        var front = await CreateListenerAsync(
            env.Http, engagementId, "tcp",
            $"127.0.0.1:{TestSupport.GetFreeTcpPort()}", $"127.0.0.1:{TestSupport.GetFreeTcpPort()}");

        // A contradiction is the moved knob: the front presents one
        // certificate, the request asked for the other roots.
        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new { ListenerId = front, Trust = "public" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("set the posture on the listener, not the build", text);

        // A typo still names itself.
        var misspelled = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new { ListenerId = front, Trust = "bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, misspelled.StatusCode);
        text = await misspelled.Content.ReadAsStringAsync();
        Assert.Contains("Trust must be 'pinned' or 'public'.", text);
    }

    [Fact]
    public async Task TheListenerGate_PublicNeedsAnHttpsDial_AndIsEchoedBack()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http, "trust-listener");

        // A cleartext front whose derived dial is http:// has no handshake a
        // public chain could ride: the posture is refused with the fix.
        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "cleartext",
                Transport: "http",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "edge.example.test",
                Trust: "public"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Trust 'public' rides an https dial", text);

        // The https front carries the posture, and the record says so.
        var accepted = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "real-domain",
                Transport: "https",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "https://real.example.test",
                Trust: "public"));
        accepted.EnsureSuccessStatusCode();
        var listener = await accepted.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.Equal("public", listener!.TrustPosture);

        // The pinned default needs no spelling: an https listener created
        // without the field reads pinned.
        var pinned = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "ca-front",
                Transport: "https",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "https://ca.example.test"));
        pinned.EnsureSuccessStatusCode();
        var pinnedListener = await pinned.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.Equal("pinned", pinnedListener!.TrustPosture);
    }

    [Fact]
    public async Task ABuildInheritsTheFrontsPosture()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http, "trust-inherit");

        // The real-domain front: an https listener whose certificate posture
        // is public.
        var front = await CreateListenerAsync(
            env.Http, engagementId, "https",
            $"127.0.0.1:{TestSupport.GetFreeTcpPort()}", "https://real.example.test", trust: "public");

        // The request names no posture -- the front's is inherited, and the
        // library's build snapshot records the public departure (a null Trust
        // is the pinned default every other build test already covers).
        var built = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new { ListenerId = front });
        built.EnsureSuccessStatusCode();
        var listed = await env.Http.GetFromJsonAsync<PayloadEndpoints.PayloadSummaryResponse[]>(
            $"/engagements/{engagementId}/payloads");
        var row = Assert.Single(listed!);
        Assert.NotNull(row.Build);
        Assert.Equal("public", row.Build!.Trust);
    }

    [Fact]
    public async Task CarriersAndFallbacksMustShareTheFrontsPosture()
    {
        await using var env = await TestEnv.StartAsync();
        var engagementId = await CreateEngagementAsync(env.Http, "trust-walk");

        // A pinned https front to build against, and a public one the walk
        // must not silently pick up.
        var pinnedFront = await CreateListenerAsync(
            env.Http, engagementId, "https",
            $"127.0.0.1:{TestSupport.GetFreeTcpPort()}", "https://ca.example.test");
        var publicListener = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "real-domain",
                Transport: "https",
                BindAddress: $"127.0.0.1:{TestSupport.GetFreeTcpPort()}",
                PublicEndpoint: "https://real.example.test",
                Trust: "public"));
        publicListener.EnsureSuccessStatusCode();
        var publicFront = await publicListener.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();

        // A carrier presenting the other certificate: the artifact bakes one
        // root set, so the beacon could not handshake.
        var refusedCarrier = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new { ListenerId = pinnedFront, BeaconListenerId = publicFront!.Id });
        Assert.Equal(HttpStatusCode.BadRequest, refusedCarrier.StatusCode);
        var text = await refusedCarrier.Content.ReadAsStringAsync();
        Assert.Contains("name a carrier that shares the front's posture", text);

        // A fallback naming the public front: a dead entry the family check
        // alone would have passed.
        var refusedFallback = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/payloads",
            new { ListenerId = pinnedFront, FallbackEndpoints = new[] { "https://real.example.test" } });
        Assert.Equal(HttpStatusCode.BadRequest, refusedFallback.StatusCode);
        text = await refusedFallback.Content.ReadAsStringAsync();
        Assert.Contains("pick fallbacks that share the front's certificate posture", text);
    }
}
