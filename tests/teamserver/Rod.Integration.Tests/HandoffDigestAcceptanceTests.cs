using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;
using static Rod.Integration.Tests.TestSupport;

namespace Rod.Integration.Tests;

/// <summary>
/// The shift handoff digest acceptance criterion (architecture.md Sec 11.1):
/// an operator requests the digest for the last watch window and gets a single
/// ordered account of sessions, task outcomes, and approvals from the audit
/// trail. The whole watch runs against a real Kestrel teamserver -- a real
/// handshake opening the session, a real task round-tripping over the
/// WebSocket beacon, a real note, a real ROE refusal -- so the digest reads
/// facts the engagement's own paths produced, none the test faked.
/// </summary>
public class HandoffDigestAcceptanceTests
{
    [Fact]
    public async Task TheWatchsFacts_AppearAsOneOrderedAccount()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        await AuthenticatedHost.LoginAsync(env.Http);

        var engagementId = await CreateEngagementAsync(env.Http);
        Assert.True(EngagementId.TryParse(engagementId, out var scope));

        // The watch opens: an implant contacts over the real beacon stream and
        // its handshake opens the session. The enrollment names the deploying
        // operator the way a minted token would, so the session's fact carries
        // the accountable operator.
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), scope, now.AddDays(30), ImplantClass.Implant, now, env.OperatorId);
        await implants.SaveAsync(implant);
        using var beacon = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beacon.ReceiveHandshakeAsync()).Status);

        // Tasking issued and completed: the operator posts the task, the
        // implant answers over the stream.
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        issued.EnsureSuccessStatusCode();
        var request = TaskRequest.Parser.ParseFrom(await beacon.ReceiveSingleFrameAsync());
        await beacon.SendFramesAsync(new[]
        {
            ResultFrame(new TaskResult
            {
                TaskId = request.TaskId,
                Outcome = TaskOutcome.Succeeded,
                Output = "red-team\\operator",
            }),
        });
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 3);

        // An annotation on the implant.
        var noted = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/implants/{implant.Id}/notes",
            new ImplantEndpoints.AddNoteRequest(Text: "IT-WKS-04, Marketing dept"));
        noted.EnsureSuccessStatusCode();

        // The sensitive line's trace today: a verb outside the ROE scope is
        // refused and the refusal lands on the trail.
        var applied = await env.Http.PutAsJsonAsync(
            $"/engagements/{engagementId}/roe",
            new { PermittedVerbs = new[] { "recon.*" }, PermittedImplants = new[] { implant.Id.ToString() } });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{engagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await WaitUntilAsync(async () =>
            (await audit.ListAsync(scope.Value)).Any(e => e.Kind == AuditEventKind.TaskRoeRefused));

        // The returning operator requests the digest for the last watch window.
        var windowFrom = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var windowTo = Uri.EscapeDataString(clock.GetUtcNow().ToString("O"));
        var digest = await env.Http.GetFromJsonAsync<DigestBody>(
            $"/engagements/{engagementId}/handoff-digest?from={windowFrom}&to={windowTo}");

        // A single ordered account: the session, the tasking with its outcome,
        // the annotation, and the refusal -- in the order the watch happened,
        // with the plumbing (dispatch, the ROE update) left out.
        Assert.NotNull(digest);
        var kinds = digest!.Entries.Select(e => e.Kind).ToArray();
        Assert.Equal(
            new[] { "SessionOpened", "TaskIssued", "TaskCompleted", "ImplantNoteAdded", "TaskRoeRefused" },
            kinds);

        // The account is enriched, not raw: the task entry carries its verb
        // and outcome, the session its implant, the acts their operator.
        var session = digest.Entries[0];
        Assert.Equal("operator", session.Operator!.Handle);
        Assert.Equal(implant.Id.Value, session.Implant!.ImplantId);
        var completed = digest.Entries[2];
        Assert.Equal("shell.exec", completed.Task!.Verb);
        Assert.Equal("Succeeded", completed.Task!.Outcome);
        Assert.Equal("whoami", completed.Payload);
        var summary = digest.Summary!;
        Assert.Equal(1, summary.SessionsOpened);
        Assert.Equal(1, summary.TasksIssued);
        Assert.Equal(1, summary.TasksCompleted);
        Assert.Equal(1, summary.NotesAdded);
        Assert.Equal(1, summary.RoeRefusals);
        Assert.True(digest.ChainVerified);

        // The markdown deliverable tells the same account for the next watch.
        var markdown = await env.Http.GetStringAsync(
            $"/engagements/{engagementId}/handoff-digest?from={windowFrom}&to={windowTo}&format=markdown");
        Assert.Contains("# Shift handoff digest: Operation Nightwatch", markdown);
        Assert.Contains("**SessionOpened**", markdown);
        Assert.Contains("**TaskCompleted**", markdown);
        Assert.Contains("**ImplantNoteAdded**", markdown);
        Assert.Contains("**TaskRoeRefused**", markdown);
        Assert.DoesNotContain("**TaskDispatched**", markdown);
        Assert.DoesNotContain("**RoeUpdated**", markdown);
    }

    private static async Task<string> CreateEngagementAsync(HttpClient http)
    {
        var engagement = await http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Nightwatch"));
        engagement.EnsureSuccessStatusCode();
        return (await engagement.Content.ReadFromJsonAsync
            <EngagementEndpoints.EngagementResponse>())!.EngagementId;
    }

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()) };

    // The digest read DTOs (HandoffDigestTests parses the full shape; this
    // proof reads only what the account asserts).
    private sealed class DigestBody
    {
        public string EngagementName { get; set; } = "";
        public bool ChainVerified { get; set; }
        public SummaryBody? Summary { get; set; }
        public List<EntryBody> Entries { get; set; } = [];
    }

    private sealed class SummaryBody
    {
        public int SessionsOpened { get; set; }
        public int TasksIssued { get; set; }
        public int TasksCompleted { get; set; }
        public int NotesAdded { get; set; }
        public int RoeRefusals { get; set; }
    }

    private sealed class EntryBody
    {
        public string Kind { get; set; } = "";
        public string Payload { get; set; } = "";
        public ActorBody? Operator { get; set; }
        public SubjectBody? Implant { get; set; }
        public TaskRefBody? Task { get; set; }
    }

    private sealed class ActorBody
    {
        public string Handle { get; set; } = "";
    }

    private sealed class SubjectBody
    {
        public Guid ImplantId { get; set; }
    }

    private sealed class TaskRefBody
    {
        public string? Verb { get; set; }
        public string? Outcome { get; set; }
    }

    /// <summary>
    /// A real Kestrel teamserver with the plain-HTTP operator API and the
    /// WebSocket beacon riding the same listener family.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public OperatorId OperatorId { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();
            env.OperatorId = AuthenticatedHost.GetOperatorId(env.Host);

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
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
