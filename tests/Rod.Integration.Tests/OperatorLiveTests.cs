using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Live;
using Rod.Operators;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M2.4 acceptance: two operators connected to one engagement see each
/// other's actions live over the SSE event stream. Drives the in-memory
/// TestServer end to end: each operator opens
/// <c>/engagements/{id}/events</c> with a query-param identity, and the live bus
/// fans task-issued and presence events out to every connected session. Live
/// state is best-effort and transient -- the audit trail (architecture.md
/// Sec 11) is the durable record; these tests cover the realtime projection.
/// </summary>
public class OperatorLiveTests
{
    // A host that layers the operator layer (M2.4) onto the transport core, so
    // the SSE endpoint, the live bus, and the presence roster are all wired --
    // the same composition the teamserver host performs.
    private static IHost CreateHost()
    {
        var host = TransportHost.CreateHostBuilder(
                configureServices: services => services.AddRodOperators(),
                mapEndpoints: endpoints => endpoints.MapOperatorEndpoints())
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();
        return host;
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client, Guid ownerId, string handle)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(
                OwnerId: ownerId,
                OwnerHandle: handle,
                OwnerDisplayName: handle,
                Name: $"Operation {handle}"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(body);
        return body!.EngagementId;
    }

    // Opens an SSE stream and returns a reader that surfaces parsed events. The
    // reader runs until the stream is disposed; the caller cancels by disposing.
    private static async Task<SseReader> OpenStreamAsync(HttpClient client, string engagementId, Guid operatorId, string handle)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/engagements/{engagementId}/events?operatorId={operatorId:N}&handle={handle}&displayName={handle}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        return new SseReader(response);
    }

    private static async Task<Guid> EnrollImplantAsync(HttpClient client, string engagementId)
    {
        // Mint a stager token, then enroll a fake implant to task against.
        var mint = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        mint.EnsureSuccessStatusCode();
        var token = await mint.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        Assert.NotNull(token);

        var enroll = await client.PostAsJsonAsync("/implants/enroll",
            new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: token!.Secret, Class: null));
        enroll.EnsureSuccessStatusCode();
        var enrolled = await enroll.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
        Assert.NotNull(enrolled);
        Assert.False(string.IsNullOrWhiteSpace(enrolled!.ImplantId));
        return Guid.Parse(enrolled.ImplantId);
    }

    [Fact]
    public async Task Two_Operators_See_Each_Others_Actions_Live()
    {
        using var host = CreateHost();
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");

        var ownerA = Guid.NewGuid();
        var engagementId = await CreateEngagementAsync(client, ownerA, "alpha");
        var implantId = await EnrollImplantAsync(client, engagementId);

        // Operator A connects first and reads its hello frame.
        await using var streamA = await OpenStreamAsync(client, engagementId, ownerA, "alpha");
        var helloA = await streamA.ReadAsync();
        Assert.Equal("hello", helloA.Event);

        // Operator B connects; A should see B join live.
        var ownerB = Guid.NewGuid();
        await using var streamB = await OpenStreamAsync(client, engagementId, ownerB, "bravo");
        // Drain B's hello frame first so the next event B reads is the live one.
        await streamB.ReadAsync();

        var aSeesBJoin = await streamA.ReadAsync();
        Assert.Equal("OperatorJoined", aSeesBJoin.Event);
        Assert.Contains("bravo", aSeesBJoin.Data);

        // Operator A issues a task over HTTP; B should see it issued live,
        // attributed to A.
        var issued = await client.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new TaskEndpoints.IssueTaskRequest(
                ImplantId: implantId.ToString("N"),
                IssuedBy: ownerA,
                Verb: "shell.exec",
                Arguments: "whoami"));
        issued.EnsureSuccessStatusCode();

        var bSeesTask = await streamB.ReadAsync();
        Assert.Equal("TaskIssued", bSeesTask.Event);
        Assert.Contains("shell.exec", bSeesTask.Data);
        Assert.Contains(ownerA.ToString("N"), bSeesTask.Data);
    }

    [Fact]
    public async Task Operator_Receives_Hello_With_Current_Presence()
    {
        using var host = CreateHost();
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");

        var owner = Guid.NewGuid();
        var engagementId = await CreateEngagementAsync(client, owner, "solo");

        await using var stream = await OpenStreamAsync(client, engagementId, owner, "solo");
        var hello = await stream.ReadAsync();

        Assert.Equal("hello", hello.Event);
        // The connecting operator is joined before hello is sent, so the roster
        // the hello carries already includes them.
        Assert.Contains("solo", hello.Data);
    }

    [Fact]
    public async Task Events_Are_Engagement_Scoped()
    {
        using var host = CreateHost();
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");

        var ownerX = Guid.NewGuid();
        var ownerY = Guid.NewGuid();
        var engagementX = await CreateEngagementAsync(client, ownerX, "x-ray");
        var engagementY = await CreateEngagementAsync(client, ownerY, "yankee");

        // An operator connected to engagement X should never see engagement Y's
        // presence or tasking.
        await using var streamX = await OpenStreamAsync(client, engagementX, ownerX, "x-ray");
        await streamX.ReadAsync(); // hello

        // Activity on Y: a second operator joins Y.
        await OpenStreamAsync(client, engagementY, Guid.NewGuid(), "yankee-2");

        // Assert by negative: X's stream does not surface Y's join within a short
        // window. A presence join on Y publishes only to Y's subscribers, so X
        // stays quiet.
        var next = await streamX.TryReadAsync(TimeSpan.FromMilliseconds(300));
        Assert.Null(next);
    }

    [Fact]
    public async Task Events_Require_Operator_Identity()
    {
        using var host = CreateHost();
        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");

        var owner = Guid.NewGuid();
        var engagementId = await CreateEngagementAsync(client, owner, "gated");

        // Missing operatorId/handle query parameters: the endpoint refuses
        // rather than starting an anonymous session.
        var response = await client.GetAsync($"/engagements/{engagementId}/events");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Parses the SSE wire format (event: / data: lines, blank-line terminated)
    // off a streaming HTTP response. ReadAsync blocks for the next full event;
    // TryReadAsync returns null on timeout so a "nothing arrived" assertion does
    // not hang the test.
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
}
