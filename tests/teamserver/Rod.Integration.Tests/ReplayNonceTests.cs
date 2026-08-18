using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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
/// Acceptance for the tasking replay nonces (architecture.md Sec 9): command
/// signing binds tasking to its implant, but a captured signed frame used to
/// verify on replay to the same implant. The arm is negotiated at handshake
/// -- an implant that advertises <c>replay_nonces</c> gets every dispatched
/// task stamped with a per-implant monotonic nonce covered by the signature,
/// and refuses any nonce at or below the highest it accepted. The acceptance
/// criterion is the todo's own: a replayed task frame is rejected by the
/// implant and the rejection surfaces on the task. The implant here is a
/// minimal in-process client that mirrors the reference verifier, so the
/// slice stays about the server's negotiation, stamping, and the surfaced
/// rejection.
/// </summary>
public class ReplayNonceTests
{
    [Fact]
    public async Task ReplayedTaskFrame_IsRejected_AndTheRejectionSurfacesOnTheTask()
    {
        await using var env = await TestEnv.StartAsync();
        var (implant, leaf, key) = await env.EnrollImplantAsync();
        using var connection = await env.ConnectBeaconAsync(implant, leaf, key, advertiseReplayNonces: true);

        // Negotiated: the server echoes the arm, so every dispatched task for
        // this implant carries a nonce covered by the signature.
        Assert.True(connection.HandshakeEcho);

        // Task one dispatches with the first nonce and verifies over the
        // five-element tuple. The double holds its result -- the replay lands
        // while the task is still live server-side, which is what makes the
        // rejection the task's visible outcome.
        var marker = "rod-replay-" + Guid.NewGuid().ToString("N")[..8];
        var (taskId, _) = await env.IssueTaskAsync(implant, "shell.exec", $"echo {marker}");
        var first = await connection.ReadTaskAsync();
        Assert.Equal(taskId, first.TaskId);
        Assert.True(first.HasTaskNonce);
        Assert.Equal(1UL, first.TaskNonce);
        Assert.True(connection.Verifies(first), "the first dispatch must verify over the five-element tuple");

        // The replay: the same captured frame delivered again. The signature
        // still checks -- it genuinely is the server's -- but the nonce falls
        // at the accepted floor, so the implant refuses and reports the
        // refusal as the task's result.
        Assert.False(connection.Verifies(first), "the replayed frame must fail the nonce floor");
        await connection.ReportAsync(first, TaskOutcome.Failed,
            $"task rejected: replayed tasking (nonce {first.TaskNonce} at or below the accepted floor); not executed");

        // The rejection surfaces on the task: the operator reads the refusal
        // as the task's own outcome (architecture.md Sec 9).
        var task = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        Assert.Equal("Failed", task!.Outcome);
        Assert.Contains("replayed tasking", task.Output);

        // Fresh tasking still flows after the replay: the next dispatch gets
        // a higher nonce, advances the floor, and completes normally.
        var (secondId, _) = await env.IssueTaskAsync(implant, "shell.exec", $"echo {marker}-2");
        var second = await connection.ReadTaskAsync();
        Assert.Equal(secondId, second.TaskId);
        Assert.True(second.TaskNonce > first.TaskNonce);
        Assert.True(connection.Verifies(second));
        await connection.ReportAsync(second, TaskOutcome.Succeeded, marker + "-2");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, secondId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task NonAdvertisingImplant_ReceivesNonceLessTasking_Unchanged()
    {
        // The evolution rules (extending/implants.md): no new mandatory work
        // on the task path. An implant that does not advertise keeps the
        // original wire shape -- no nonce field, the four-element signed
        // tuple -- and tasking verifies exactly as before the arm existed.
        await using var env = await TestEnv.StartAsync();
        var (implant, leaf, key) = await env.EnrollImplantAsync();
        using var connection = await env.ConnectBeaconAsync(implant, leaf, key, advertiseReplayNonces: false);

        Assert.False(connection.HandshakeEcho);

        var (taskId, _) = await env.IssueTaskAsync(implant, "shell.exec", "whoami");
        var task = await connection.ReadTaskAsync();
        Assert.Equal(taskId, task.TaskId);
        Assert.False(task.HasTaskNonce);
        Assert.True(connection.Verifies(task));

        await connection.ReportAsync(task, TaskOutcome.Succeeded, "red-team\\operator");
        var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        Assert.Equal("Succeeded", done!.Outcome);
    }

    [Fact]
    public async Task ReplayNegotiation_IsStickyAcrossHandshakes()
    {
        // The flag is one-way on the implant (architecture.md Sec 9): once an
        // implant advertised the arm, a later handshake that stops advertising
        // cannot downgrade its tasking back to the nonce-less shape.
        await using var env = await TestEnv.StartAsync();
        var (implant, leaf, key) = await env.EnrollImplantAsync();

        using (var first = await env.ConnectBeaconAsync(implant, leaf, key, advertiseReplayNonces: true))
        {
            Assert.True(first.HandshakeEcho);
            var (taskId, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo one");
            var task = await first.ReadTaskAsync();
            Assert.True(task.HasTaskNonce);
            await first.ReportAsync(task, TaskOutcome.Succeeded, "one");
            await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
        }

        // Reconnect without advertising: the sticky flag keeps the nonce shape.
        using (var second = await env.ConnectBeaconAsync(implant, leaf, key, advertiseReplayNonces: false))
        {
            Assert.True(second.HandshakeEcho, "the negotiation is sticky: the echo survives a downgrade handshake");
            var (taskId, _) = await env.IssueTaskAsync(implant, "shell.exec", "echo two");
            var task = await second.ReadTaskAsync();
            Assert.True(task.HasTaskNonce);
            Assert.True(task.TaskNonce > 1UL);
            await second.ReportAsync(task, TaskOutcome.Succeeded, "two");
            var done = await env.WaitUntilTaskCompletesAsync(implant.EngagementId, taskId);
            Assert.Equal("Succeeded", done!.Outcome);
        }
    }

    /// <summary>
    /// A real Kestrel teamserver (mTLS beacon + plain-HTTP operator API) plus
    /// the enroll fixture, the same shape the neighboring beacon suites use.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int HttpPort { get; private set; }
        public int MtlsPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.HttpPort = GetFreeTcpPort();
            env.MtlsPort = GetFreeTcpPort();

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

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            await AuthenticatedHost.LoginAsync(env.Http);
            return env;
        }

        public async Task<(Implant Implant, X509Certificate2 Leaf, RSA Key)> EnrollImplantAsync()
        {
            var ca = Host.Services.GetRequiredService<IImplantCertificateAuthority>();
            var implants = Host.Services.GetRequiredService<IImplantRepository>();
            var clock = Host.Services.GetRequiredService<TimeProvider>();
            var now = clock.GetUtcNow();
            var implant = Implant.Enroll(
                ImplantId.New(), EngagementId.New(), now.AddDays(30), ImplantClass.Stage2, now);
            await implants.SaveAsync(implant);

            var key = RSA.Create(2048);
            var issued = await ca.IssueWithKeyAsync(
                new ImplantCertificateSubject(implant.Id, implant.EngagementId), key, CancellationToken.None);
            return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), key);
        }

        public async Task<BeaconConnection> ConnectBeaconAsync(
            Implant implant, X509Certificate2 leaf, RSA key, bool advertiseReplayNonces)
            => await BeaconConnection.OpenAsync(this, implant, leaf, key, advertiseReplayNonces);

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

        public async Task<TaskBody?> WaitUntilTaskCompletesAsync(EngagementId engagementId, string taskId)
        {
            var end = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < end)
            {
                var task = await Http.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{taskId}");
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

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    /// <summary>
    /// The minimal in-process implant: mTLS gRPC check-in with the handshake
    /// (optionally advertising the replay-nonce arm), the reference verifier's
    /// nonce floor, and result reporting. It never executes tasking -- the
    /// suite is about negotiation, stamping, and the surfaced rejection.
    /// </summary>
    private sealed class BeaconConnection : IDisposable
    {
        public bool HandshakeEcho { get; private set; }

        private readonly GrpcChannel _channel;
        private readonly AsyncDuplexStreamingCall<Frame, Frame> _call;
        private readonly string _implantId;
        private readonly X509Certificate2[] _cas;
        private readonly TaskNonceFloor _floor = new();

        private BeaconConnection(
            GrpcChannel channel, AsyncDuplexStreamingCall<Frame, Frame> call,
            string implantId, X509Certificate2[] cas, bool echo)
        {
            _channel = channel;
            _call = call;
            _implantId = implantId;
            _cas = cas;
            HandshakeEcho = echo;
        }

        public static async Task<BeaconConnection> OpenAsync(
            TestEnv env, Implant implant, X509Certificate2 leaf, RSA key, bool advertiseReplayNonces)
        {
            var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>()
                .GetCaCertificate();
            var leafWithKey = leaf.CopyWithPrivateKey(key);
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509CertificateCollection { leafWithKey },
                    RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                    {
                        if (cert is null || chain is null)
                            return false;
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                        chain.ChainPolicy.ExtraStore.Add(ca);
                        return chain.Build((X509Certificate2)cert)
                            && chain.ChainElements[^1].Certificate.Thumbprint == ca.Thumbprint;
                    },
                },
            };
            var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{env.MtlsPort}",
                new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
            var call = new Beacon.BeaconClient(channel).CheckIn();

            var handshake = new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplantId = implant.Id.ToString(),
                ReplayNonces = advertiseReplayNonces,
            };
            handshake.Capabilities.Add("shell.exec");
            await call.RequestStream.WriteAsync(new Frame
            {
                Payload = ByteString.CopyFrom(handshake.ToByteArray()),
            });

            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            var response = HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload);
            Assert.Equal(HandshakeStatus.Ok, response.Status);
            return new BeaconConnection(
                channel, call, implant.Id.ToString(), [ca], response.ReplayNonces);
        }

        /// <summary>Awaits the next dispatched task frame.</summary>
        public async Task<TaskRequest> ReadTaskAsync()
        {
            Assert.True(await _call.ResponseStream.MoveNext(CancellationToken.None));
            return TaskRequest.Parser.ParseFrom(_call.ResponseStream.Current.Payload);
        }

        /// <summary>
        /// The reference verification posture: the signature over the tuple
        /// (five elements when the task carries a nonce), then the nonce
        /// floor. Accepted nonces raise the floor.
        /// </summary>
        public bool Verifies(TaskRequest task)
        {
            using var rsa = _cas[0].GetRSAPublicKey()!;
            var canonical = Canonical(
                _implantId, task.TaskId, task.Verb, task.Arguments,
                task.HasTaskNonce ? task.TaskNonce : null);
            if (task.Signature.Length == 0
                || !rsa.VerifyData(canonical, task.Signature.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return false;

            if (!task.HasTaskNonce)
                return true;
            if (_floor.IsReplay(task.TaskNonce))
                return false;
            _floor.Observed(task.TaskNonce);
            return true;
        }

        public async Task ReportAsync(TaskRequest task, TaskOutcome outcome, string output)
            => await _call.RequestStream.WriteAsync(new Frame
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = task.TaskId,
                    Outcome = outcome,
                    Output = output,
                }.ToByteArray()),
            });

        public void Dispose()
        {
            try { _call.RequestStream.CompleteAsync().GetAwaiter().GetResult(); } catch { }
            _call.Dispose();
            _channel.Dispose();
        }

        // The canonical signed encoding from rod.proto, nonce appended as its
        // decimal string when the task carries one.
        private static byte[] Canonical(
            string implantId, string taskId, string verb, string arguments, ulong? nonce)
        {
            var fields = nonce is { } value
                ? new[] { implantId, taskId, verb, arguments, value.ToString() }
                : new[] { implantId, taskId, verb, arguments };
            using var buffer = new MemoryStream();
            foreach (var field in fields)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(field);
                var length = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)bytes.Length);
                buffer.Write(length);
                buffer.Write(bytes);
            }
            return buffer.ToArray();
        }

        private sealed class TaskNonceFloor
        {
            private ulong _highest;
            public bool IsReplay(ulong nonce) => nonce <= _highest;
            public void Observed(ulong nonce)
            {
                if (nonce > _highest)
                    _highest = nonce;
            }
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
