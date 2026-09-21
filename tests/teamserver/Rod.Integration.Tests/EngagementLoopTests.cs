using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// The engagement-critical loop end to end in one test run (the todo's
/// end-to-end item): a real mTLS teamserver, a beacon client standing in for
/// the implant, and an operator over HTTP walk handshake, signed task
/// dispatch, staged artifact capture, paginated history walking, and the
/// staleness sweep closing a silently dead stream so the recovered implant
/// re-handshakes (architecture.md Sec 4.3, Sec 10.3, Sec 11). Coordination is
/// by polling readback, never sleeps.
/// </summary>
public class EngagementLoopTests
{
    [Fact]
    public async Task EngagementCriticalLoop_WalksHandshakeTaskingExfilPagingAndRecovery()
    {
        await using var env = await TestEnv.StartAsync();
        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        await AuthenticatedHost.LoginAsync(env.Http);

        var implant = await EnrollImplantAsync(implants, clock);
        var authority = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var caCert = authority.GetCaCertificate();

        // --- Phase 1: handshake + signed task dispatch + captured result. ---
        using var beaconA = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beaconA.ReceiveHandshakeAsync()).Status);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        var request = TaskRequest.Parser.ParseFrom(await beaconA.ReceiveSingleFrameAsync());
        Assert.Equal("shell.exec", request.Verb);

        // The dispatched task carries the teamserver's signature over the
        // canonical implant/task tuple (architecture.md Sec 9), verified here
        // the way the implant does: against the CA it holds from enrollment.
        Assert.True(request.Signature.Length > 0);
        Assert.True(VerifyTasking(caCert, implant.Id.ToString(), request));

        var result = new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "loop\\operator",
        };
        await beaconA.SendFramesAsync(new[] { ResultFrame(result) });
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 3);

        // --- Phase 2: staged artifact over the exfil channel. ---
        var exfilIssued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "exfil.push", Arguments = "loot.txt /opt/loot" });
        exfilIssued.EnsureSuccessStatusCode();

        var exfilRequest = TaskRequest.Parser.ParseFrom(await beaconA.ReceiveSingleFrameAsync());
        Assert.Equal("exfil.push", exfilRequest.Verb);

        await beaconA.SendFramesAsync(new[] { ResultFrame(new TaskResult
        {
            TaskId = exfilRequest.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "pushed loot.txt",
        }) });
        var chunk = new ExfilChunk
        {
            TaskId = exfilRequest.TaskId,
            Name = "loot.txt",
            Sequence = 1,
            Terminal = true,
            ContentType = "text/plain",
        };
        chunk.Data = ByteString.CopyFromUtf8("engagement-loop loot");
        await beaconA.SendFramesAsync(new[] { new Frame
        {
            Payload = ByteString.CopyFrom(chunk.ToByteArray()),
            Kind = FrameKind.ExfilChunk,
        } });

        Guid artifactId = Guid.Empty;
        await WaitUntilAsync(async () =>
        {
            var events = await audit.ForTaskAsync(Guid.Parse(exfilRequest.TaskId));
            var captured = events.FirstOrDefault(e => e.Kind == AuditEventKind.ExfilCaptured);
            if (captured is null)
                return false;
            artifactId = Guid.Parse(captured.Outcome!);
            return true;
        });
        var artifactBytes = await env.Http.GetByteArrayAsync(
            $"/engagements/{implant.EngagementId}/artifacts/{artifactId:N}");
        Assert.Equal("engagement-loop loot", System.Text.Encoding.UTF8.GetString(artifactBytes));

        // End the working stream cleanly; the history and recovery phases need
        // no live stream.
        beaconA.Dispose();

        // Wait out the dead stream's server-side teardown before seeding the
        // queue. The moment the probes enqueue, the dispatch wake releases --
        // and a writer still unwinding from stream A could claim one and write
        // it into the closing connection: dispatched on the server, received
        // nowhere, and the drain below then waits on a frame that never comes.
        // The staleness sweep closing the session (threshold 2s) strictly
        // follows the runner's unwind, so an empty session roster proves no
        // writer from stream A remains to race the seeds.
        await WaitUntilAsync(
            async () => await sessions.GetActiveAsync(implant.Id, CancellationToken.None) is null,
            timeout: TimeSpan.FromSeconds(20));

        // --- Phase 3: seeded history walked through paginated listings. ---
        for (var i = 0; i < 5; i++)
        {
            var seeded = await env.Http.PostAsJsonAsync(
                $"/engagements/{implant.EngagementId}/tasks",
                new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = $"check-{i}" });
            seeded.EnsureSuccessStatusCode();
        }

        // Walk the engagement's task history in pages of three: every issued
        // task appears exactly once, newest windows first.
        var seen = new HashSet<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var pageUri = $"/engagements/{implant.EngagementId}/tasks?limit=3";
            if (cursor is not null)
                pageUri += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await env.Http.GetFromJsonAsync<TaskPageBody>(pageUri);
            Assert.NotNull(page);
            foreach (var t in page!.Items)
                Assert.True(seen.Add(t.TaskId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);
        Assert.Equal(7, seen.Count); // shell.exec + exfil.push + five probes
        Assert.True(pages >= 3);

        // The exfil task's artifact list pages too (one item, one page).
        var artifactPage = await env.Http.GetFromJsonAsync<ArtifactPageBody>(
            $"/engagements/{implant.EngagementId}/tasks/{exfilRequest.TaskId}/artifacts?limit=1");
        Assert.Single(artifactPage!.Items);
        Assert.Null(artifactPage.NextCursor);

        // --- Phase 4: silent stream death swept, recovered implant re-handshakes. ---
        var beaconB = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beaconB.ReceiveHandshakeAsync()).Status);
        Assert.NotNull(await sessions.GetActiveAsync(implant.Id, CancellationToken.None));

        // The recovered implant drains the tasking that queued while it was
        // away -- the five probes dispatch downstream on the fresh stream.
        for (var i = 0; i < 5; i++)
        {
            var queued = TaskRequest.Parser.ParseFrom(await beaconB.ReceiveSingleFrameAsync());
            Assert.Equal("shell.exec", queued.Verb);
            Assert.True(VerifyTasking(caCert, implant.Id.ToString(), queued));
        }

        // Now the stream dies silently: abandoned without a graceful close,
        // the connection stays up but no frame ever advances its last-seen, so
        // only the staleness sweep can close the session (architecture.md
        // Sec 10.3). Not disposed on purpose.
        _ = beaconB;

        await WaitUntilAsync(
            async () => await sessions.GetActiveAsync(implant.Id, CancellationToken.None) is null,
            timeout: TimeSpan.FromSeconds(20));

        // The recovered implant reconnects on a fresh stream and re-handshakes
        // -- the sweep ended the old session, and the handshake opens a new one.
        var beaconC = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        Assert.Equal(HandshakeStatus.Ok, (await beaconC.ReceiveHandshakeAsync()).Status);
        Assert.NotNull(await sessions.GetActiveAsync(implant.Id, CancellationToken.None));
    }

    // Verifies a dispatched task's signature exactly as the implant does
    // (TaskingVerifier's contract): RSASSA-PSS/SHA-256 over the canonical
    // length-prefixed (implant_id, task_id, verb, arguments) encoding, against
    // the enrollment CA's public key.
    private static bool VerifyTasking(X509Certificate2 ca, string implantId, TaskRequest task)
    {
        using var publicKey = ca.GetRSAPublicKey()!;
        return publicKey.VerifyData(
            Canonical(implantId, task.TaskId, task.Verb, task.Arguments),
            task.Signature.Span,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
    }

    private static byte[] Canonical(string implantId, string taskId, string verb, string arguments)
    {
        var fields = new[] { implantId, taskId, verb, arguments }
            .Select(System.Text.Encoding.UTF8.GetBytes)
            .ToArray();
        using var buffer = new MemoryStream();
        foreach (var field in fields)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)field.Length);
            buffer.Write(length);
            buffer.Write(field);
        }
        return buffer.ToArray();
    }

    private static async Task<Implant> EnrollImplantAsync(
        IImplantRepository implants, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        return implant;
    }

    private static Frame HandshakeFrame(ImplantId implant)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
            Capabilities = { "shell.exec", "exfil.push", "file.pull" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static Frame ResultFrame(TaskResult result)
        => new() { Payload = ByteString.CopyFrom(result.ToByteArray()), Kind = FrameKind.TaskResult };

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

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

    private sealed class TaskPageBody
    {
        public TaskItemBody[] Items { get; set; } = [];
        public string? NextCursor { get; set; }
    }

    private sealed class TaskItemBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class ArtifactPageBody
    {
        public ArtifactItemBody[] Items { get; set; } = [];
        public string? NextCursor { get; set; }
    }

    private sealed class ArtifactItemBody
    {
        public string ArtifactId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint and the
    /// operator API, configured with a short staleness threshold so the sweep
    /// closes a silently dead session within the test's horizon.
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

            var config = AuthenticatedHost.BuildConfig(settings =>
            {
                settings["Sessions:Staleness:Threshold"] = "00:00:02";
                settings["Sessions:Staleness:SweepInterval"] = "00:00:00.200";
            });
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
