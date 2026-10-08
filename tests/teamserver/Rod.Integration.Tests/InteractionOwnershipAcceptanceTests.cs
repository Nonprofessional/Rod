using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.Transport;
using Rod.V1;
using static Rod.Integration.Tests.TestSupport;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: operator roles and interaction ownership (architecture.md
/// Sec 4.5). Two operators on one engagement see each other's claim on an
/// interactive shell, the second operator's input is refused while the claim
/// holds, and an operator without the tasking scope cannot issue tasks.
/// Drives the full slice through a real Kestrel endpoint with a
/// contract-faithful fake implant over the WebSocket beacon, three operator
/// sessions (the holder, the peer, and a read-only operator), and the SSE
/// stream a console holds -- the claim's whole life: taken by the first
/// input, seen by the peer, refused and named, released, retaken, and
/// reaped by its holder's disconnect.
/// </summary>
public class InteractionOwnershipAcceptanceTests
{
    private const string Password = "p@ssw0rd!";

    [Fact]
    public async Task ClaimIsVisible_ExclusiveWhileHeld_AndReleasedOnDisconnect()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var aliceId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "alice", "Alice Op", Password);
        var bobId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "bob", "Bob Op", Password);
        var carolId = await AuthenticatedHost.RegisterOperatorAsync(env.Host, "carol", "Carol Op", Password);

        using var alice = await LoginAsync(env, "alice");
        using var bob = await LoginAsync(env, "bob");
        using var carol = await LoginAsync(env, "carol");

        // Carol holds the viewing scope only -- narrowed through the
        // assignment route by a task-holding operator.
        var demoted = await alice.PutAsJsonAsync(
            $"/operators/{carolId}/scopes", new { scopes = new[] { "read" } });
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);

        var implant = await EnrollImplantAsync(implants, clock);
        var engagement = implant.EngagementId.ToString();

        // The live channel: an interactive shell on a connected implant.
        using var beacon = await WsBeaconClient.ConnectAsync(
            env.HttpPort, implant.Id.ToString(), new[] { "shell.interact" });
        Assert.Equal(HandshakeStatus.Ok, (await beacon.ReceiveHandshakeAsync()).Status);

        // An operator without the tasking scope cannot issue tasks -- the
        // surface refuses her acting outright, while the viewing surface
        // still answers.
        var carolIssue = await carol.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.interact" });
        Assert.Equal(HttpStatusCode.Forbidden, carolIssue.StatusCode);
        var carolRead = await carol.GetAsync($"/engagements/{engagement}/tasks");
        Assert.Equal(HttpStatusCode.OK, carolRead.StatusCode);

        // Alice opens the shell and types: the first input takes the claim.
        var issued = await alice.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.interact" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);
        var taskId = issuedBody!.TaskId;

        var request = await NextTaskRequestAsync(beacon, taskId);
        await beacon.SendFramesAsync(new[] { OutputFrame(taskId, "$ ") });
        var aliceInput = await alice.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("id\n") });
        Assert.Equal(HttpStatusCode.OK, aliceInput.StatusCode);
        Assert.Equal("id\n", Encoding.UTF8.GetString((await NextChannelInputAsync(beacon, taskId)).Data.Span));

        // The peer sees the claim: the listing names the holder, and a late
        // joiner's hello carries it -- two operators on one engagement see
        // each other's claim.
        var claims = await bob.GetFromJsonAsync<ClaimBody[]>($"/engagements/{engagement}/claims");
        var seen = Assert.Single(claims!);
        Assert.Equal("channel", seen.Kind);
        Assert.Equal(taskId, seen.SurfaceId);
        Assert.Equal(aliceId.ToString(), seen.OperatorId);

        await using var bobStream = await OpenStreamAsync(bob, engagement);
        var helloBob = await bobStream.ReadAsync();
        Assert.Equal("hello", helloBob.Event);
        Assert.Contains(taskId, helloBob.Data);
        Assert.Contains(aliceId.ToString(), helloBob.Data);

        // The second operator's input is refused while the claim holds, the
        // holder named in the refusal -- and so is an explicit grab.
        var bobInput = await bob.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("whoami\n") });
        Assert.Equal(HttpStatusCode.Conflict, bobInput.StatusCode);
        Assert.Contains("alice", await bobInput.Content.ReadAsStringAsync());

        var bobAcquire = await bob.PostAsJsonAsync(
            $"/engagements/{engagement}/claims", new { kind = "channel", taskId });
        Assert.Equal(HttpStatusCode.Conflict, bobAcquire.StatusCode);
        Assert.Contains("alice", await bobAcquire.Content.ReadAsStringAsync());

        // The holder keeps typing; the refusal was never hers.
        var aliceAgain = await alice.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("uname -a\n") });
        Assert.Equal(HttpStatusCode.OK, aliceAgain.StatusCode);
        Assert.Equal("uname -a\n", Encoding.UTF8.GetString((await NextChannelInputAsync(beacon, taskId)).Data.Span));

        // The holder releases; the peer's input takes the surface right
        // after -- the claim is a lock, not a wall.
        var released = await alice.DeleteAsync($"/engagements/{engagement}/claims/channel/{taskId}");
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        var bobTakes = await bob.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("whoami\n") });
        Assert.Equal(HttpStatusCode.OK, bobTakes.StatusCode);
        Assert.Equal("whoami\n", Encoding.UTF8.GetString((await NextChannelInputAsync(beacon, taskId)).Data.Span));

        // And the lock turns: alice's input is the refused one now.
        var aliceRefused = await alice.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("id\n") });
        Assert.Equal(HttpStatusCode.Conflict, aliceRefused.StatusCode);
        Assert.Contains("bob", await aliceRefused.Content.ReadAsStringAsync());

        // The disconnect is the reaper. Alice connects her own stream after
        // bob holds (her hello shows his claim), then bob's stream ends and
        // the release publishes to her -- the claim was only as durable as
        // its holder's connection.
        await using var aliceStream = await OpenStreamAsync(alice, engagement);
        var helloAlice = await aliceStream.ReadAsync();
        Assert.Equal("hello", helloAlice.Event);
        Assert.Contains(bobId.ToString(), helloAlice.Data);

        await bobStream.DisposeAsync();
        var reap = await aliceStream.ReadAsync();
        Assert.Equal("ClaimReleased", reap.Event);
        Assert.Contains(taskId, reap.Data);

        // The surface is free again.
        var aliceAfter = await alice.PostAsJsonAsync(
            $"/engagements/{engagement}/tasks/{taskId}/input",
            new { Data = Encoding.UTF8.GetBytes("exit\n") });
        Assert.Equal(HttpStatusCode.OK, aliceAfter.StatusCode);
        Assert.Equal("exit\n", Encoding.UTF8.GetString((await NextChannelInputAsync(beacon, taskId)).Data.Span));
    }

    private static async Task<HttpClient> LoginAsync(TestEnv env, string handle)
    {
        var client = new HttpClient(new CookieHandler(new HttpClientHandler()))
        {
            BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
        };
        await AuthenticatedHost.LoginAsync(client, handle, Password);
        return client;
    }

    // Reads downstream frames until the TaskRequest for taskId arrives.
    private static async Task<TaskRequest> NextTaskRequestAsync(WsBeaconClient beacon, string taskId)
    {
        while (true)
        {
            var frame = await beacon.ReceiveFrameAsync();
            if (frame.Kind != FrameKind.Unspecified)
                continue;
            var request = TaskRequest.Parser.ParseFrom(frame.Payload);
            if (request.TaskId == taskId)
                return request;
        }
    }

    // Reads downstream frames until the ChannelInput for taskId arrives.
    private static async Task<ChannelInput> NextChannelInputAsync(WsBeaconClient beacon, string taskId)
    {
        while (true)
        {
            var frame = await beacon.ReceiveFrameAsync();
            if (frame.Kind != FrameKind.ChannelInput)
                continue;
            var input = ChannelInput.Parser.ParseFrom(frame.Payload);
            if (input.TaskId == taskId)
                return input;
        }
    }

    private static Frame OutputFrame(string taskId, string text)
        => new()
        {
            Payload = ByteString.CopyFrom(new ChannelOutput
            {
                TaskId = taskId,
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes(text)),
            }.ToByteArray()),
            Kind = FrameKind.ChannelOutput,
        };

    private static async Task<Implant> EnrollImplantAsync(IImplantRepository implants, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Implant, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    // Opens an SSE stream and returns a reader that surfaces parsed events;
    // disposing the reader ends the connection (the disconnect the claim's
    // reaper rides).
    private static async Task<SseReader> OpenStreamAsync(HttpClient client, string engagementId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/engagements/{engagementId}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new SseReader(response);
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class ClaimBody
    {
        public string Kind { get; set; } = "";
        public string SurfaceId { get; set; } = "";
        public string OperatorId { get; set; } = "";
        public DateTimeOffset AcquiredAt { get; set; }
    }

    private sealed class SseReader : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        public SseReader(HttpResponseMessage response)
        {
            _response = response;
            _stream = response.Content.ReadAsStream();
            _reader = new StreamReader(_stream);
        }

        public async Task<SseEvent> ReadAsync(CancellationToken cancellationToken = default)
        {
            var ev = await TryReadAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return ev ?? throw new TimeoutException("No SSE event arrived within the timeout.");
        }

        public async Task<SseEvent?> TryReadAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            string? eventName = null;
            var data = new StringBuilder();

            try
            {
                while (true)
                {
                    var line = await _reader.ReadLineAsync(cts.Token);
                    if (line is null)
                        return null; // stream ended

                    if (line.StartsWith("event:"))
                        eventName = line["event:".Length..].Trim();
                    else if (line.StartsWith("data:"))
                        data.Append(line["data:".Length..].TrimStart());
                    else if (line.Length == 0)
                        return new SseEvent(eventName ?? string.Empty, data.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _stream.Dispose();
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SseEvent(string Event, string Data);

    /// <summary>
    /// A real Kestrel teamserver with the operator layers composed -- the
    /// WebSocket beacon and the SSE stream both need real sockets here.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
