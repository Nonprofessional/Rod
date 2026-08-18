using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: an operator types into a live shell on a connected implant
/// (architecture.md Sec 10.3, the streaming task shape). Drives the full slice
/// through a real Kestrel mTLS endpoint with a contract-faithful fake implant:
/// the operator issues <c>shell.interact</c>, the TaskRequest opens the
/// channel, the implant's output chunks land on the task's transcript as they
/// stream, the operator's input posts flow back down as ChannelInput frames,
/// and the final TaskResult closes the task with the whole session as its
/// record. Also checks the input route's refusals: a one-shot task takes no
/// live input, and a channel with no live stream cannot accept any.
/// </summary>
public class InteractiveShellRoundTripTests
{
    [Fact]
    public async Task ShellInteract_StreamsBothWays_AndCompletesWithTheTranscript()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "shell.interact"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

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
        var request = await NextTaskRequestAsync(call, issuedBody!.TaskId);
        Assert.Equal("shell.interact", request.Verb);
        await call.RequestStream.WriteAsync(OutputFrame(request.TaskId, "$ "));

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
        var input = await NextChannelInputAsync(call, request.TaskId);
        Assert.Equal("echo hi\n", Encoding.UTF8.GetString(input.Data.Span));

        // The shell answers, and the answer lands on the transcript too.
        await call.RequestStream.WriteAsync(OutputFrame(request.TaskId, "hi\n"));
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}"))!.Output == "$ hi\n");

        // The operator closes stdin: eof rides the same route.
        var closed = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/input",
            new { Eof = true });
        closed.EnsureSuccessStatusCode();
        var eof = await NextChannelInputAsync(call, request.TaskId);
        Assert.True(eof.Eof);

        // The shell exits and the implant reports the task like any other:
        // one final TaskResult whose output joins the transcript.
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "shell exited",
        }));

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

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task InputRoute_RefusesOneShotTasksAndDeadChannels()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

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
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, "shell.exec"));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        var oneshot = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        oneshot.EnsureSuccessStatusCode();
        var oneshotBody = await oneshot.Content.ReadFromJsonAsync<TaskIssuedBody>();
        var oneshotRequest = await NextTaskRequestAsync(call, oneshotBody!.TaskId);

        var refused = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("hi\n") });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)refused.StatusCode);

        // A completed one-shot task is refused on the verb first -- it never
        // was a channel -- same 422 as before, not a liveness conflict.
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = oneshotRequest.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "uid=0",
        }));
        await WaitUntilAsync(async () =>
            (await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}"))!.Status == "Completed");
        var afterEnd = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks/{oneshotRequest.TaskId}/input",
            new { Data = Encoding.UTF8.GetBytes("hi\n") });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)afterEnd.StatusCode);

        await call.RequestStream.CompleteAsync();
    }

    // The deadline every downstream read waits under: a frame that never
    // arrives must fail the test, not park it forever.
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);

    // Reads downstream frames until the TaskRequest for taskId arrives. The
    // handshake precedes tasking; other kind-bearing downstream frames are
    // skipped (a channel input racing the dispatch, never before it).
    private static async Task<TaskRequest> NextTaskRequestAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string taskId)
    {
        while (true)
        {
            Assert.True(await MoveNextAsync(call, "task request"), "stream ended early");
            var frame = call.ResponseStream.Current;
            if (frame.Kind != FrameKind.Unspecified)
                continue;
            var request = TaskRequest.Parser.ParseFrom(frame.Payload);
            if (request.TaskId == taskId)
                return request;
        }
    }

    // Reads downstream frames until the ChannelInput for taskId arrives --
    // the only kind-bearing downstream frame today.
    private static async Task<ChannelInput> NextChannelInputAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string taskId)
    {
        while (true)
        {
            Assert.True(await MoveNextAsync(call, "channel input"), "stream ended early");
            var frame = call.ResponseStream.Current;
            if (frame.Kind != FrameKind.ChannelInput)
                continue;
            var input = ChannelInput.Parser.ParseFrom(frame.Payload);
            if (input.TaskId == taskId)
                return input;
        }
    }

    // One bounded downstream read: the raw gRPC wait carries no deadline of
    // its own, and a hung suite costs an hour -- a missing frame fails the
    // test with what it was waiting for instead.
    private static async Task<bool> MoveNextAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call, string awaiting)
    {
        using var deadline = new CancellationTokenSource(ReadDeadline);
        try
        {
            return await call.ResponseStream.MoveNext(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for the downstream {awaiting} frame.");
        }
        catch (RpcException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for the downstream {awaiting} frame.");
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

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
    }

    private static Frame HandshakeFrame(ImplantId implant, params string[] capabilities)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
        };
        request.Capabilities.Add(capabilities);
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

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
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the task round-trip harness.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public OperatorId OperatorId { get; private set; }
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
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

        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, RSA leafKey)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(leafKey);
            var ca = Host.Services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();

            var handler = new SocketsHttpHandler();
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { leafWithKey },
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    if (cert is null)
                        return false;
                    chain!.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain!.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain!.ChainPolicy.ExtraStore.Add(ca);
                    return chain.Build((X509Certificate2)cert);
                },
            };

            return GrpcChannel.ForAddress($"https://127.0.0.1:{MtlsPort}", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
