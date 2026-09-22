using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the receive-ack arm (architecture.md Sec 10.3 -- the
/// dispatch strand on a dying stream): a claimed task whose frame was written
/// into a closing connection used to mark Dispatched and never redeliver --
/// the failed-write requeue covered only the write, and below the result no
/// delivery evidence existed. The arm adds a TaskAck frame (the implant acks a
/// parsed task before executing it), negotiates it at handshake so unupgraded
/// implants keep today's semantics, requeues ack-less dispatches at stream
/// end, and makes duplicate results idempotent (first result wins). The
/// acceptance criterion is the todo's own: a task whose frame rides a stream
/// that dies before the ack is redelivered on the next contact, and an
/// implant that already held it re-acks without running it twice. The implant
/// here is a minimal in-process client that mirrors the reference ledger's
/// re-ack posture, so the slice stays about the server's negotiation,
/// requeue, and first-wins discipline.
/// </summary>
public class TaskAckRedeliveryTests
{
    [Fact]
    public async Task AcklessDispatch_OnDyingStream_IsRedeliveredOnTheNextContact()
    {
        await using var env = await TestEnv.StartAsync();
        var implant = await env.EnrollImplantAsync();

        string taskId;
        using (var first = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true))
        {
            Assert.True(first.AcksEcho);

            var (issued, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo strand");
            taskId = issued;
            var original = await first.ReadTaskAsync();
            Assert.Equal(taskId, original.TaskId);

            // The stream dies before the ack: the connection closes holding a
            // dispatch the server never saw evidence for.
        }

        // The next contact redelivers it: the stream's end returned the
        // ack-less dispatch to the queue, and the reconnect's writer claims
        // from it.
        using var second = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true);
        var redelivered = await second.ReadTaskAsync();
        Assert.Equal(taskId, redelivered.TaskId);

        // An implant that already held it re-acks without running it twice,
        // then reports the outcome; the strand closes on the task itself.
        await second.AckAsync(redelivered.TaskId);
        await second.ReportAsync(redelivered, TaskOutcome.Succeeded, "strand closed");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, redelivered.TaskId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task AcklessDispatch_OnAbortedStream_IsRedeliveredOnTheNextContact()
    {
        await using var env = await TestEnv.StartAsync();
        var implant = await env.EnrollImplantAsync();

        string taskId;
        using (var first = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true))
        {
            var (issued, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo abort-strand");
            taskId = issued;
            var original = await first.ReadTaskAsync();
            Assert.Equal(taskId, original.TaskId);

            // The connection aborts holding the dispatch: no complete, no
            // ack. The server's read fails rather than ending cleanly -- the
            // death shape that used to skip the strand close and strand the
            // dispatch in Dispatched forever.
            first.Abort();
        }

        // The reconnect redelivers it all the same.
        using var second = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true);
        var redelivered = await second.ReadTaskAsync();
        Assert.Equal(taskId, redelivered.TaskId);

        await second.AckAsync(redelivered.TaskId);
        await second.ReportAsync(redelivered, TaskOutcome.Succeeded, "abort strand closed");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, redelivered.TaskId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task AckedDispatch_OnDyingStream_IsNotRedelivered()
    {
        await using var env = await TestEnv.StartAsync();
        var implant = await env.EnrollImplantAsync();

        string taskId;
        using (var first = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true))
        {
            var (issued, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo held");
            taskId = issued;
            var task = await first.ReadTaskAsync();
            Assert.Equal(taskId, task.TaskId);

            // The ack crossed before the stream died: the server holds
            // delivery evidence, so the dispatch stands and the result --
            // whenever the implant next reports it -- is what completes it.
            await first.AckAsync(taskId);

            // The ack must actually cross: CompleteAsync orders the frames,
            // but the server drains them asynchronously, and an ack it never
            // read leaves the dispatch in the strand -- redelivered, by
            // design, on the reconnect. The settle gives the server its read
            // cycle before the close takes the stream away.
            await System.Threading.Tasks.Task.Delay(500);
        }

        using var second = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true);
        Assert.False(await second.TaskArrivesAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Dispatched", (await env.GetTaskAsync(implant.EngagementId, taskId))!.Status);

        await second.ReportIdAsync(taskId, TaskOutcome.Succeeded, "reported after the reconnect");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task UnadvertisedImplant_KeepsTodaysDispatchSemantics()
    {
        // The evolution rules (extending/implants.md): no new mandatory work
        // on the task path. An implant that does not advertise the arm never
        // gets a requeue -- a written frame counts as delivered, exactly as
        // before the arm existed -- and a later handshake on the same implant
        // negotiates fresh (the arm is per handshake, never sticky).
        await using var env = await TestEnv.StartAsync();
        var implant = await env.EnrollImplantAsync();

        string taskId;
        using (var first = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: false))
        {
            Assert.False(first.AcksEcho);
            var (issued, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo legacy");
            taskId = issued;
            var task = await first.ReadTaskAsync();
            Assert.Equal(taskId, task.TaskId);
        }

        using var second = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: false);
        Assert.False(await second.TaskArrivesAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Dispatched", (await env.GetTaskAsync(implant.EngagementId, taskId))!.Status);

        // The same implant may adopt the arm on its next handshake: the
        // negotiation is per connection, so a stream-mode artifact that
        // upgrades mid-run gets the arm exactly where it asked for it.
        using var third = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true);
        Assert.True(third.AcksEcho);
        await third.ReportIdAsync(taskId, TaskOutcome.Succeeded, "closed on an ack-negotiating stream");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task DuplicateResult_IsIdempotent_FirstWins()
    {
        // The retransmission tolerance the arm requires: an implant resends a
        // cached result after a stream death, and either the original or the
        // resend may land first -- never both. A second result for a
        // completed task changes nothing.
        await using var env = await TestEnv.StartAsync();
        var implant = await env.EnrollImplantAsync();
        using var connection = await env.ConnectBeaconAsync(implant, advertiseTaskAcks: true);

        var (taskId, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo once");
        var task = await connection.ReadTaskAsync();
        await connection.AckAsync(taskId);
        await connection.ReportAsync(task, TaskOutcome.Succeeded, "the first answer");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        Assert.Equal("the first answer", done!.Output);

        await connection.ReportAsync(task, TaskOutcome.Failed, "a late duplicate with different content");
        await Task.Delay(500);
        var reread = await env.GetTaskAsync(implant.EngagementId, taskId);
        Assert.Equal("the first answer", reread!.Output);
        Assert.Equal("Succeeded", reread.Outcome);
    }

    /// <summary>
    /// A real Kestrel teamserver with the implant endpoint bound, plus a
    /// plain-HTTP operator API -- the same harness shape the replay-nonce
    /// acceptance uses.
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
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async Task<Implant> EnrollImplantAsync()
        {
            var implants = Host.Services.GetRequiredService<IImplantRepository>();
            var clock = Host.Services.GetRequiredService<TimeProvider>();
            var now = clock.GetUtcNow();
            var implant = Implant.Enroll(
                ImplantId.New(), EngagementId.New(), now.AddDays(30), ImplantClass.Implant, now);
            await implants.SaveAsync(implant);
            return implant;
        }

        public async Task<BeaconConnection> ConnectBeaconAsync(Implant implant, bool advertiseTaskAcks)
            => await BeaconConnection.OpenAsync(this, implant, advertiseTaskAcks);

        public async Task<(string TaskId, string Verb)> IssueTaskAsync(
            Implant implant, string verb, string arguments)
        {
            var issued = await Http.PostAsJsonAsync(
                $"/engagements/{implant.EngagementId}/tasks",
                new { ImplantId = implant.Id.ToString(), Verb = verb, Arguments = arguments });
            issued.EnsureSuccessStatusCode();
            var body = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
            Assert.NotNull(body);
            return (body!.TaskId, body.Verb);
        }

        public async Task<TaskBody?> GetTaskAsync(EngagementId engagementId, string taskId)
            => await Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{taskId}");

        public async Task<TaskBody?> WaitUntilTaskCompletesAsync(EngagementId engagementId, string taskId)
        {
            var end = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < end)
            {
                var task = await GetTaskAsync(engagementId, taskId);
                if (task is { Status: "Completed" })
                    return task;
                await Task.Delay(250);
            }
            throw new TimeoutException($"Task {taskId} did not complete within the deadline.");
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    /// <summary>
    /// The minimal in-process implant: WebSocket beacon contact with the handshake
    /// (optionally advertising the receive-ack arm), the ack frame, and result
    /// reporting. It never executes tasking -- the suite is about the server's
    /// requeue, negotiation, and first-wins discipline.
    /// </summary>
    private sealed class BeaconConnection : IDisposable
    {
        public bool AcksEcho { get; }

        private readonly WsBeaconClient _beacon;

        private BeaconConnection(WsBeaconClient beacon, bool acksEcho)
        {
            _beacon = beacon;
            AcksEcho = acksEcho;
        }

        public static async Task<BeaconConnection> OpenAsync(
            TestEnv env, Implant implant, bool advertiseTaskAcks)
        {
            var beacon = await WsBeaconClient.ConnectAsync(
                env.HttpPort, implant.Id.ToString(), new[] { "shell.exec" },
                taskAcks: advertiseTaskAcks);
            var response = await beacon.ReceiveHandshakeAsync();
            Assert.Equal(HandshakeStatus.Ok, response.Status);
            return new BeaconConnection(beacon, response.TaskAcks);
        }

        /// <summary>Awaits the next dispatched task frame.</summary>
        public async Task<TaskRequest> ReadTaskAsync()
            => TaskRequest.Parser.ParseFrom(await _beacon.ReceiveSingleFrameAsync());

        /// <summary>
        /// Whether any tasking arrives within the window -- the negative
        /// assertion the no-redelivery cases hang on, answered off the
        /// harness's buffered queue so the connection stays usable after
        /// a miss.
        /// </summary>
        public async Task<bool> TaskArrivesAsync(TimeSpan window)
            => await _beacon.FrameArrivesAsync(window);

        /// <summary>The receive-ack: delivery evidence for one parsed task.</summary>
        public async Task AckAsync(string taskId)
            => await _beacon.SendFramesAsync(new[] { new Frame
            {
                Kind = FrameKind.TaskAck,
                Payload = ByteString.CopyFrom(new TaskAck { TaskId = taskId }.ToByteArray()),
            } });

        public async Task ReportAsync(TaskRequest task, TaskOutcome outcome, string output)
            => await ReportIdAsync(task.TaskId, outcome, output);

        public async Task ReportIdAsync(string taskId, TaskOutcome outcome, string output)
            => await _beacon.SendFramesAsync(new[] { new Frame
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = taskId,
                    Outcome = outcome,
                    Output = output,
                }.ToByteArray()),
            } });

        public void Dispose()
        {
            try { _beacon.Dispose(); } catch { }
        }

        /// <summary>
        /// Kills the connection without a graceful close -- the abort
        /// shape a network drop takes, where the server's read fails rather
        /// than ending cleanly.
        /// </summary>
        public void Abort()
        {
            _beacon.Dispose();
        }
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
        public string Verb { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Outcome { get; set; }
    }
}
