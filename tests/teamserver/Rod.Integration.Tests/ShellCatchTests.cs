using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.ShellSessions;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.ShellCatch;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: the shellcatch transport (architecture.md Sec 8) catches the
/// shells an operator's one-liners dial home. A connection speaking no Rod
/// protocol is accepted and held as a shell session scoped by its
/// engagement-bound listener; the first output the peer sends is
/// fingerprinted server-side; operator input flows down the socket; the
/// ending is attributed to who ended it (Lost when the peer goes away,
/// Closed when an operator asks). The fingerprint rules and the output
/// log's cursor contract are pinned directly alongside.
/// </summary>
public class ShellCatchTests
{
    [Fact]
    public void Fingerprinter_ReadsTheClassicBanners()
    {
        Assert.Equal(ShellOsGuess.UnixShell, ShellFingerprinter.Guess("user@target:~$ "));
        Assert.Equal(ShellOsGuess.UnixShell, ShellFingerprinter.Guess(
            "Welcome to Ubuntu 22.04 LTS\n\nroot@web01:~# "));
        Assert.Equal(ShellOsGuess.WindowsCmd, ShellFingerprinter.Guess(
            "\nMicrosoft Windows [Version 10.0.19045.4291]\n(c) Microsoft Corporation. All rights reserved.\n\nC:\\Users\\ops>"));
        Assert.Equal(ShellOsGuess.WindowsPowerShell, ShellFingerprinter.Guess(
            "Windows PowerShell\nCopyright (C) Microsoft Corporation. All rights reserved.\n\nPS C:\\Users\\ops>"));
        Assert.Equal(ShellOsGuess.WindowsPowerShell, ShellFingerprinter.Guess("PS C:\\Users\\ops> "));
        Assert.Equal(ShellOsGuess.Unknown, ShellFingerprinter.Guess(""));
        Assert.Equal(ShellOsGuess.Unknown, ShellFingerprinter.Guess("just some text with no rule match"));
    }

    [Fact]
    public async Task OutputLog_CursorsBySequence_AndWaitsForAdvance()
    {
        var at = DateTimeOffset.UnixEpoch;
        var log = new ShellOutputLog();

        Assert.Equal(0, log.LatestSequence);
        Assert.Empty(log.ReadSince(0));

        var first = log.Append("hello", at);
        var second = log.Append("world", at.AddSeconds(1));
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(2, log.LatestSequence);
        Assert.Equal(new[] { second }, log.ReadSince(1), ShellOutputChunkEqualityComparer.Instance);
        Assert.Empty(log.ReadSince(2));

        // A reader at the tip waits, then sees the next append.
        var waiting = log.WaitForAdvanceAsync(2, Timeout());
        Assert.False(waiting.IsCompleted);
        log.Append("more", at.AddSeconds(2));
        var advanced = await waiting;
        Assert.Equal(3, advanced);
    }

    [Fact]
    public async Task ShellCatchListener_HoldsAFingerprintedShell_AndMarksItLostWhenThePeerLeaves()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var port = TestSupport.GetFreeTcpPort();
        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-shellcatch",
                Transport: "shellcatch",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"10.0.0.5:{port}"));
        created.EnsureSuccessStatusCode();
        var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
        Assert.NotNull(listener);
        Assert.Equal("running", listener!.State);

        var hub = env.Host.Services.GetRequiredService<ShellCatchHub>();
        var sessions = env.Host.Services.GetRequiredService<IShellSessionRegistry>();

        // The one-liner's shape: connect, speak shell -- no handshake, no
        // protocol, just the banner a Unix shell prints.
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        await peer.GetStream().WriteAsync("user@target:~$ "u8.ToArray());

        var shell = await WaitForAsync(() => Task.FromResult(hub.List().SingleOrDefault()),
            found => found is not null, "the listener to catch the shell");
        var sessionId = shell!.Session.Id;

        // The banner is fingerprinted server-side and lands on the output
        // log the console reads.
        await WaitForAsync(
            () => sessions.FindAsync(sessionId),
            session => session?.Os == ShellOsGuess.UnixShell,
            "the banner to be fingerprinted");
        var caught = hub.Find(sessionId);
        Assert.NotNull(caught);
        Assert.Contains("user@target:~$", string.Concat(caught!.Output.ReadSince(0).Select(c => c.Text)));

        // The session registry scopes the catch to the engagement the
        // listener belongs to.
        var listed = await sessions.ListByEngagementAsync(
            new EngagementId(Guid.Parse(engagementId)));
        Assert.Contains(listed, s => s.Id == sessionId);
        Assert.Equal(ShellSessionStatus.Live, listed.Single(s => s.Id == sessionId).Status);

        // The peer going away ends the shell as Lost, and the hub holds
        // live sockets only.
        peer.Close();
        await WaitForAsync(
            () => sessions.FindAsync(sessionId),
            session => session?.Status == ShellSessionStatus.Lost,
            "the lost shell to be swept");
        Assert.Null(hub.Find(sessionId));
    }

    [Fact]
    public async Task OperatorClose_MarksTheShellClosed_InsteadOfLost()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var port = TestSupport.GetFreeTcpPort();
        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-shellcatch",
                Transport: "shellcatch",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"10.0.0.5:{port}"));
        created.EnsureSuccessStatusCode();

        var hub = env.Host.Services.GetRequiredService<ShellCatchHub>();
        var sessions = env.Host.Services.GetRequiredService<IShellSessionRegistry>();

        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        var shell = await WaitForAsync(() => Task.FromResult(hub.List().SingleOrDefault()),
            found => found is not null, "the listener to catch the shell");

        shell!.CloseByOperator();
        await WaitForAsync(
            () => sessions.FindAsync(shell.Session.Id),
            session => session?.Status == ShellSessionStatus.Closed,
            "the closed shell to be recorded");
    }

    [Fact]
    public async Task OperatorInput_FlowsDownTheSocket()
    {
        await using var env = await TestEnv.StartAsync();
        await AuthenticatedHost.LoginAsync(env.Http);
        var engagementId = await CreateEngagementAsync(env.Http);

        var port = TestSupport.GetFreeTcpPort();
        var created = await env.Http.PostAsJsonAsync($"/engagements/{engagementId}/listeners",
            new ListenerEndpoints.CreateListenerRequest(
                Name: "runtime-shellcatch",
                Transport: "shellcatch",
                BindAddress: $"127.0.0.1:{port}",
                PublicEndpoint: $"10.0.0.5:{port}"));
        created.EnsureSuccessStatusCode();

        var hub = env.Host.Services.GetRequiredService<ShellCatchHub>();

        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        var shell = await WaitForAsync(() => Task.FromResult(hub.List().SingleOrDefault()),
            found => found is not null, "the listener to catch the shell");

        Assert.True(await shell!.WriteInputAsync("id", Timeout()));

        var buffer = new byte[64];
        var read = await peer.GetStream().ReadAsync(buffer, Timeout());
        Assert.Equal("id\n", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Shellcatch"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe, Func<T, bool> done, string expectation, TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var value = await probe();
            if (done(value))
                return value;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {expectation}.");
    }

    // The read/write deadlines for the socket round trips; one shared shape
    // keeps the waits honest without sprinkling raw timeouts through the
    // tests.
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private sealed class ShellOutputChunkEqualityComparer : IEqualityComparer<ShellOutputChunk>
    {
        public static readonly ShellOutputChunkEqualityComparer Instance = new();

        public bool Equals(ShellOutputChunk? x, ShellOutputChunk? y) => x == y;
        public int GetHashCode(ShellOutputChunk obj) => obj.GetHashCode();
    }

    /// <summary>
    /// A real teamserver with the operator API and no startup stream
    /// listener -- the shellcatch listener under test is created at runtime
    /// through the engagement-scoped API, the tier it belongs to. Mirrors
    /// the stream listener harness.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public Microsoft.Extensions.Hosting.IHost Host { get; private set; } = null!;
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
