using System.Net.Http.Json;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: an operator types into a live shell on a connected implant
/// (architecture.md Sec 10.3, the streaming task shape). Drives the full slice
/// through a real Kestrel endpoint with a contract-faithful fake implant over
/// the WebSocket beacon: the operator issues <c>shell.interact</c>, the
/// TaskRequest opens the channel, the implant's output chunks land on the
/// task's transcript as they stream, the operator's input posts flow back down
/// as ChannelInput frames, and the final TaskResult closes the task with the
/// whole session as its record. Also checks the input route's refusals: a
/// one-shot task takes no live input, and a channel with no live stream cannot
/// accept any.
/// </summary>
public class InteractiveShellRoundTripTests
{
    [Fact]
    public async Task ShellInteract_StreamsBothWays_AndCompletesWithTheTranscript()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var implant = await EnrollImplantAsync(implants, clock);

        using var beacon = await WsBeaconClient.ConnectAsync(
            env.HttpPort, implant.Id.ToString(), new[] { "shell.interact" });
        Assert.Equal(HandshakeStatus.Ok, (await beacon.ReceiveHandshakeAsync()).Status);

        // The operator opens the interactive shell like any other task.
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.interact" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);

        // The channel opens: the TaskRequest arrives downstream and the
        // implant starts streaming what the shell prints.
        var request = await NextTaskRequestAsync(beacon, issuedBody!.TaskId);
        Assert.Equal("shell.interact", request.Verb);
        await beacon.SendFramesAsync(new[] { OutputFrame(request.TaskId, "$ ") });

        // The operator reads the prompt off the task while the channel runs --
        // the transcript is live, not a completion-time capture.
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Output == "$ ");

        // The operator types. The input post is accepted and the bytes arrive
        // on the channel downstream, framed and in order.
        var sent = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("echo hi\n") });
        sent.EnsureSuccessStatusCode();
        var input = await NextChannelInputAsync(beacon, request.TaskId);
        Assert.Equal("echo hi\n", Encoding.UTF8.GetString(input.Data.Span));

        // The shell answers, and the answer lands on the transcript too.
        await beacon.SendFramesAsync(new[] { OutputFrame(request.TaskId, "hi\n") });
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Output == "$ hi\n");

        // The operator closes stdin: eof rides the same route.
        var closed = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Eof = true });
        closed.EnsureSuccessStatusCode();
        var eof = await NextChannelInputAsync(beacon, request.TaskId);
        Assert.True(eof.Eof);

        // The shell exits and the implant reports the task like any other:
        // one final TaskResult whose output joins the transcript.
        await beacon.SendFramesAsync(new[]
        {
            ResultFrame(new TaskResult
            {
                TaskId = request.TaskId,
                Outcome = TaskOutcome.Succeeded,
                Output = "shell exited",
            }),
        });

        // The task's attributed arc: issued, dispatched, two input posts, and
        // the completion carrying the whole transcript.
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 5);

        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}");
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("$ hi\nshell exited", fetched.Output);
        Assert.Equal("Succeeded", fetched.Outcome);
        Assert.Equal(
            new[] { "TaskIssued", "TaskDispatched", "ChannelInput", "ChannelInput", "TaskCompleted" },
            fetched.Audit.Select(e => e.Kind).ToArray());
        // The completion's output is the whole transcript -- the record of an
        // interactive session is the session, not a summary.
        Assert.Equal("$ hi\nshell exited", fetched.Audit[^1].Output);
    }

    [Fact]
    public async Task InputRoute_RefusesOneShotTasksAndDeadChannels()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var implant = await EnrollImplantAsync(implants, clock);

        await AuthenticatedHost.LoginAsync(env.Http);

        // A queued channel task (no beacon stream ever opened) has no live
        // channel: well-formed, refused with a conflict.
        var queued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.interact" });
        queued.EnsureSuccessStatusCode();
        var queuedBody = await queued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        var queuedInput = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{queuedBody!.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("hi\n") });
        Assert.Equal(StatusCodes.Status409Conflict, (int)queuedInput.StatusCode);

        // A one-shot task, even dispatched on a live stream, takes no input.
        using var beacon = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beacon.ReceiveHandshakeAsync()).Status);

        var oneshot = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        oneshot.EnsureSuccessStatusCode();
        var oneshotBody = await oneshot.Content.ReadFromJsonAsync<TaskIssuedBody>();
        var oneshotRequest = await NextTaskRequestAsync(beacon, oneshotBody!.TaskId);

        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("hi\n") });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)refused.StatusCode);

        // A completed one-shot task is refused on the verb first -- it never
        // was a channel -- same 422 as before, not a liveness conflict.
        await beacon.SendFramesAsync(new[]
        {
            ResultFrame(new TaskResult
            {
                TaskId = oneshotRequest.TaskId,
                Outcome = TaskOutcome.Succeeded,
                Output = "uid=0",
            }),
        });
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}"))!.Status == "Completed");
        var afterEnd = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("hi\n") });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)afterEnd.StatusCode);
    }

    // Reads downstream frames until the TaskRequest for taskId arrives. The
    // handshake precedes tasking; other kind-bearing downstream frames are
    // skipped (a channel input racing the dispatch, never before it).
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

    // Reads downstream frames until the ChannelInput for taskId arrives --
    // the only kind-bearing downstream frame today.
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

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()) };

    private static async Task<Implant> EnrollImplantAsync(
        IImplantRepository implants, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Implant, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class AuditBody
    {
        public string Kind { get; set; } = "";
        public string? Output { get; set; }
    }

    /// <summary>
    /// A real Kestrel teamserver with the plain-HTTP operator API and the
    /// WebSocket beacon riding the same listener family.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
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
