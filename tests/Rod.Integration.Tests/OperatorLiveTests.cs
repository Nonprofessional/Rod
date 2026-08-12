using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M2.4 acceptance: two operators connected to one engagement see each
/// other's actions live over the SSE event stream. Drives the in-memory
/// TestServer end to end: each operator opens <c>/engagements/{id}/events</c>
/// under their own cookie session, and the live bus fans task-issued and
/// presence events out to every connected session. Live state is best-effort and
/// transient -- the audit trail (architecture.md Sec 11) is the durable record;
/// these tests cover the realtime projection.
/// </summary>
public class OperatorLiveTests
{
    private const string OperatorPassword = "p@ssw0rd!";

    // A host that layers the operator and operator-auth layers onto the transport
    // core, the same composition the teamserver host performs. The seed account is
    // created implicitly; each test provisions the named operators it needs.
    private static IHost CreateHost()
    {
        var (_, host, _) = AuthenticatedHost.Create();
        return host;
    }

    // Registers a named operator and returns its id, so a test can attribute
    // assertions to a specific account independently of the client session.
    private static Task<OperatorId> RegisterAsync(IHost host, string handle, string displayName)
        => AuthenticatedHost.RegisterOperatorAsync(host, handle, displayName, OperatorPassword);

    // A cookie-persisting client logged in as the named operator. Each operator
    // gets its own client so the sessions (and their SSE streams) stay distinct.
    private static async Task<HttpClient> OperatorClientAsync(IHost host, string handle)
    {
        var client = AuthenticatedHost.CreateClient(host);
        await AuthenticatedHost.LoginAsync(client, handle, OperatorPassword);
        return client;
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: name));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(body);
        return body!.EngagementId;
    }

    // Opens an SSE stream and returns a reader that surfaces parsed events. The
    // reader runs until the stream is disposed; the caller cancels by disposing.
    private static async Task<SseReader> OpenStreamAsync(HttpClient client, string engagementId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/engagements/{engagementId}/events");
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
        var ownerA = await RegisterAsync(host, "alpha", "Alpha Operator");
        await RegisterAsync(host, "bravo", "Bravo Operator");
        using var clientA = await OperatorClientAsync(host, "alpha");
        using var clientB = await OperatorClientAsync(host, "bravo");

        var engagementId = await CreateEngagementAsync(clientA, "Operation alpha");
        var implantId = await EnrollImplantAsync(clientA, engagementId);

        // Operator A connects first and reads its hello frame.
        await using var streamA = await OpenStreamAsync(clientA, engagementId);
        var helloA = await streamA.ReadAsync();
        Assert.Equal("hello", helloA.Event);

        // Operator B connects; A should see B join live.
        await using var streamB = await OpenStreamAsync(clientB, engagementId);
        // Drain B's hello frame first so the next event B reads is the live one.
        await streamB.ReadAsync();

        var aSeesBJoin = await streamA.ReadAsync();
        Assert.Equal("OperatorJoined", aSeesBJoin.Event);
        Assert.Contains("bravo", aSeesBJoin.Data);

        // Operator A issues a task over HTTP; B should see it issued live,
        // attributed to A.
        var issued = await clientA.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new TaskEndpoints.IssueTaskRequest(
                ImplantId: implantId.ToString("N"),
                Verb: "shell.exec",
                Arguments: "whoami"));
        issued.EnsureSuccessStatusCode();

        var bSeesTask = await streamB.ReadAsync();
        Assert.Equal("TaskIssued", bSeesTask.Event);
        Assert.Contains("shell.exec", bSeesTask.Data);
        Assert.Contains(ownerA.Value.ToString("N"), bSeesTask.Data);
    }

    [Fact]
    public async Task Operator_Receives_Hello_With_Current_Presence()
    {
        using var host = CreateHost();
        await RegisterAsync(host, "solo", "Solo Operator");
        using var client = await OperatorClientAsync(host, "solo");

        var engagementId = await CreateEngagementAsync(client, "Operation solo");

        await using var stream = await OpenStreamAsync(client, engagementId);
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
        await RegisterAsync(host, "x-ray", "X-Ray Operator");
        await RegisterAsync(host, "yankee", "Yankee Operator");
        await RegisterAsync(host, "yankee-2", "Yankee Two Operator");
        using var clientX = await OperatorClientAsync(host, "x-ray");
        using var clientY = await OperatorClientAsync(host, "yankee");
        using var clientY2 = await OperatorClientAsync(host, "yankee-2");

        var engagementX = await CreateEngagementAsync(clientX, "Operation x-ray");
        var engagementY = await CreateEngagementAsync(clientY, "Operation yankee");

        // An operator connected to engagement X should never see engagement Y's
        // presence or tasking.
        await using var streamX = await OpenStreamAsync(clientX, engagementX);
        await streamX.ReadAsync(); // hello

        // Activity on Y: a second operator joins Y.
        await using var streamY2 = await OpenStreamAsync(clientY2, engagementY);
        await streamY2.ReadAsync(); // hello

        // Assert by negative: X's stream does not surface Y's join within a short
        // window. A presence join on Y publishes only to Y's subscribers, so X
        // stays quiet.
        var next = await streamX.TryReadAsync(TimeSpan.FromMilliseconds(300));
        Assert.Null(next);
    }

    [Fact]
    public async Task Events_Require_Authentication()
    {
        using var host = CreateHost();
        await RegisterAsync(host, "gated", "Gated Operator");
        using var owner = await OperatorClientAsync(host, "gated");

        var engagementId = await CreateEngagementAsync(owner, "Operation gated");

        // An anonymous request -- no cookie session -- is refused before the
        // handler runs: the events route requires an authenticated operator, so
        // the middleware answers 401 rather than starting an anonymous stream.
        using var anonymous = AuthenticatedHost.CreateClient(host);
        var response = await anonymous.GetAsync($"/engagements/{engagementId}/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
