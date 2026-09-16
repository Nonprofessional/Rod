using System.Net;
using System.Net.Http.Json;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Tasks;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Quic;
using Rod.Transport.Payloads;
using Rod.V1;
// The domain entity shares its name with the BCL Task, and both namespaces
// define a TaskOutcome; pin the BCL Task and reach the wire outcome by name.
using Task = System.Threading.Tasks.Task;
using TaskOutcome = Rod.V1.TaskOutcome;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the QUIC stream transport (architecture.md Sec 8): the
/// socket-owning family's duplex variant, for egress that passes UDP/443
/// but blocks TCP. A from-scratch QUIC implant -- no HTTP stack, just a
/// QUIC client, the stream check-in framing, and the protobuf messages --
/// sees a listener entry created through the operator API carry a check-in
/// end to end: TLS terminated at the CA-issued leaf with no client
/// certificate anywhere, the handshake as the identity, tasking pushed onto
/// the open stream the moment it is queued, and the live channel an
/// operator types into -- the native-channel tier, the point of the duplex
/// shape.
/// </summary>
public class QuicBeaconRoundTripTests
{
    [QuicFact]
    public async Task AQuicListenerEntry_CreatedThroughTheOperatorApi_CarriesACheckInEndToEnd()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implant = await StageImplantAsync(host, engagementId);

            var port = GetFreeUdpPort();
            var created = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "runtime-quic",
                    Transport: "quic",
                    BindAddress: $"127.0.0.1:{port}",
                    PublicEndpoint: $"10.0.0.5:{port}"));
            created.EnsureSuccessStatusCode();
            var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
            Assert.NotNull(listener);
            Assert.Equal("running", listener!.State);
            Assert.Equal($"10.0.0.5:{port}", listener.PublicEndpoint);

            // The engagement's roster reports the entry like any transport's.
            var roster = await client.GetFromJsonAsync<ListenerEndpoints.ListenerResponse[]>(
                $"/engagements/{engagementId}/listeners");
            Assert.NotNull(roster);
            var recorded = Assert.Single(roster!);
            Assert.Equal("quic", recorded.Transport);
            Assert.Equal("running", recorded.State);

            // The check-in: dial, open the stream, speak first. The implant
            // advertises the replay-nonce arm like the reference implant, and
            // the response echoes it.
            var authority = host.Services.GetRequiredService<IImplantCertificateAuthority>();
            using var ca = authority.GetCaCertificate();
            await using var session = await QuicImplant.ConnectAsync(port, ca, implant.Id.ToString());
            var handshake = await session.ReceiveHandshakeAsync();
            Assert.Equal(HandshakeStatus.Ok, handshake.Status);
            Assert.Equal(1, handshake.Version.Major);
            Assert.True(handshake.ReplayNonces);

            // The push shape: the task arrives on the open stream the moment
            // the operator queues it -- no poll, no waiting interval, over
            // the UDP socket the entry owns.
            var marker = "rod-quic-marker-" + Guid.NewGuid().ToString("N")[..8];
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = $"echo {marker}" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            var request = TaskRequest.Parser.ParseFrom(await session.ReceiveSingleFrameAsync());
            Assert.Equal(issuedBody!.TaskId, request.TaskId);
            Assert.Equal("shell.exec", request.Verb);
            Assert.NotEmpty(request.Signature.ToByteArray());

            await session.SendFramesAsync(Frames.Result(request.TaskId, TaskOutcome.Succeeded, marker));

            var task = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Status == "Completed" ? fetched : null;
            });
            Assert.Contains(marker, task!.Output);
        }
    }

    [QuicFact]
    public async Task TheQuicStream_HoldsTheInteractiveChannel()
    {
        // The duplex point: the channel verbs run live over the QUIC stream.
        // An operator's typing reaches the implant as a ChannelInput frame on
        // the open connection, and the implant's output streams back -- the
        // native-channel tier the poll members of the family cannot carry.
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var implant = await StageImplantAsync(host, engagementId);

            var port = GetFreeUdpPort();
            var created = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "runtime-quic",
                    Transport: "quic",
                    BindAddress: $"127.0.0.1:{port}",
                    PublicEndpoint: $"10.0.0.5:{port}"));
            created.EnsureSuccessStatusCode();

            var authority = host.Services.GetRequiredService<IImplantCertificateAuthority>();
            using var ca = authority.GetCaCertificate();
            await using var session = await QuicImplant.ConnectAsync(port, ca, implant.Id.ToString());
            Assert.Equal(HandshakeStatus.Ok, (await session.ReceiveHandshakeAsync()).Status);

            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implant.Id.ToString(), Verb = ChannelVerbs.ShellInteract, Arguments = "" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();
            Assert.NotNull(issuedBody);

            var request = TaskRequest.Parser.ParseFrom(await session.ReceiveSingleFrameAsync());
            Assert.Equal(ChannelVerbs.ShellInteract, request.Verb);

            // The operator types; the frame crosses the stream.
            var typed = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks/{issuedBody!.TaskId}/input",
                new { Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("whoami\n")) });
            typed.EnsureSuccessStatusCode();

            var input = ChannelInput.Parser.ParseFrom(
                await session.ReceiveSingleFrameAsync(expectKind: FrameKind.ChannelInput));
            Assert.Equal(issuedBody.TaskId, input.TaskId);
            Assert.Equal("whoami\n", input.Data.ToStringUtf8());

            // The implant streams its shell's output back over the channel,
            // on the same connection the tasking arrived on.
            await session.SendFramesAsync(Frames.ChannelOut(request.TaskId, "root\n"));

            var task = await WaitUntilAsync(async () =>
            {
                var fetched = await client.GetFromJsonAsync<TaskBody>(
                    $"/engagements/{engagementId}/tasks/{issuedBody.TaskId}");
                return fetched?.Output?.Contains("root") == true ? fetched : null;
            });
            Assert.Equal("Dispatched", task!.Status);
        }
    }

    [QuicFact]
    public async Task ABuildNamingAQuicBeacon_BakesTheQuicDial()
    {
        // The issuance half of the honest carrier declaration: a quic
        // listener is beacon-nameable, and the baked beacon URL must name the
        // dial the artifact's check-in client picks by -- the transport's own
        // scheme completed over the bare public endpoint. A poll-mode bake is
        // refused with the reason: the quic session holds one live stream
        // and has no poll cycle.
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var port = GetFreeUdpPort();
            var created = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "runtime-quic",
                    Transport: "quic",
                    BindAddress: $"127.0.0.1:{port}",
                    PublicEndpoint: $"10.0.0.5:{port}"));
            created.EnsureSuccessStatusCode();
            var listener = await created.Content.ReadFromJsonAsync<ListenerEndpoints.ListenerResponse>();
            Assert.NotNull(listener);

            var refused = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                Request(mode: "poll", beaconListenerId: listener!.Id));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
            Assert.NotNull(problem);
            Assert.Contains("no poll cycle", problem!.Error, StringComparison.OrdinalIgnoreCase);

            var parse = await PayloadBuildRequestParser.ParseAsync(
                Request(beaconListenerId: listener.Id),
                new EngagementId(Guid.Parse(engagementId)),
                operatorId,
                host.Services.GetRequiredService<Rod.Audit.IPayloadStore>(),
                host.Services.GetRequiredService<IListenerRegistry>(),
                host.Services.GetRequiredService<IImplantCertificateAuthority>(),
                CancellationToken.None);
            Assert.Null(parse.Error);
            Assert.Equal($"quic://10.0.0.5:{port}", parse.Request!.Transport.BeaconEndpoint);

        }

        static Rod.Transport.Endpoints.PayloadEndpoints.BuildPayloadRequest Request(
            string? mode = null, string? beaconListenerId = null)
            => new(
                Language: null,
                Class: null,
                TargetOs: null,
                TargetArch: null,
                Endpoint: "https://c2.example.test/implants/enroll",
                UriPath: null,
                SleepSeconds: null,
                JitterSeconds: null,
                KillDate: null,
                Mode: mode,
                BeaconListenerId: beaconListenerId);
    }

    private static async Task<Implant> StageImplantAsync(IHost host, string engagementId)
    {
        var implants = host.Services.GetRequiredService<IImplantRepository>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), new EngagementId(Guid.Parse(engagementId)), now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);
        return implant;
    }

    // A free UDP port below the Linux ephemeral range, the same discipline
    // the TCP helper keeps: a bind there is a reservation, not a race.
    private static int GetFreeUdpPort()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(20_000, 32_760);
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                // Taken; the next candidate.
            }
        }
        throw new InvalidOperationException("No free UDP port inside the probed range.");
    }

    private static async Task<string> CreateEngagementAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Quic Stream"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<T?> WaitUntilAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = await probe();
            if (found is not null)
                return found;
            await Task.Delay(50);
        }
        throw new TimeoutException("The condition never held inside the window.");
    }

    private sealed class IssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Output { get; set; }
    }

    private sealed class ProblemBody
    {
        public string? Error { get; set; }
    }

    // The from-scratch QUIC implant: the stream check-in framing over one
    // bidirectional stream, nothing else -- no HTTP stack, no gRPC library,
    // the client an implant author of any language with a QUIC stack writes
    // from the contract doc.
    private sealed class QuicImplant : IAsyncDisposable
    {
        [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
        [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
        [System.Runtime.Versioning.SupportedOSPlatformGuard("osx")]
        private static bool QuicSupported => QuicListener.IsSupported;

        private readonly QuicConnection _connection;
        private readonly QuicStream _stream;

        private QuicImplant(QuicConnection connection, QuicStream stream)
        {
            _connection = connection;
            _stream = stream;
        }

        public static async Task<QuicImplant> ConnectAsync(int port, X509Certificate2 ca, string implantId)
        {
            if (!QuicSupported)
                throw new InvalidOperationException(
                    "The host provides no QUIC stack; install libmsquic to run this acceptance.");

            var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new(QuicListenerService.Alpn) },
                    RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                        PinsTheAuthority(certificate as X509Certificate2, chain, ca),
                },
            }, CancellationToken.None);
            var stream = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, CancellationToken.None);
            var implant = new QuicImplant(connection, stream);

            // The implant speaks first: the handshake frame, the identity
            // this certificate-less transport runs on.
            var handshake = new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplantId = implantId,
                ReplayNonces = true,
            };
            handshake.Capabilities.Add("shell.exec");
            handshake.Capabilities.Add(ChannelVerbs.ShellInteract);
            await implant.SendFramesAsync(new Frame
            {
                Payload = ByteString.CopyFrom(handshake.ToByteArray()),
            });
            return implant;
        }

        // The pinned-CA posture the reference implant pins (C2.PinServerChain,
        // mirrored): the dev CA is in no system store, so the presented chain
        // builds against the anchor with the unknown-authority allowance, and
        // must terminate at exactly its thumbprint.
        private static bool PinsTheAuthority(X509Certificate2? certificate, X509Chain? chain, X509Certificate2 ca)
        {
            if (certificate is null || chain is null)
                return false;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(ca);
            if (!chain.Build(certificate))
                return false;
            return chain.ChainElements.Count > 0
                && chain.ChainElements[^1].Certificate.Thumbprint == ca.Thumbprint;
        }

        public async Task<HandshakeResponse> ReceiveHandshakeAsync()
            => HandshakeResponse.Parser.ParseFrom(await ReceiveSingleFrameAsync());

        public async Task<byte[]> ReceiveSingleFrameAsync(FrameKind expectKind = FrameKind.Unspecified)
        {
            var frames = ParseFrames(await ReceiveMessageAsync());
            Assert.Single(frames);
            if (expectKind != FrameKind.Unspecified)
                Assert.Equal(expectKind, frames[0].Kind);
            return frames[0].Payload.ToByteArray();
        }

        public Task SendFramesAsync(params Frame[] frames)
            => WriteMessageAsync(frames);

        public async ValueTask DisposeAsync()
        {
            if (!QuicSupported)
                return;

            await _stream.DisposeAsync();
            await _connection.DisposeAsync();
        }

        // One message in: the varint length prefix, then exactly that many
        // body bytes of delimited frames.
        private async Task<byte[]> ReceiveMessageAsync()
        {
            if (!QuicSupported)
                throw new InvalidOperationException("The host provides no QUIC stack.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            long length = 0;
            var shift = 0;
            while (true)
            {
                var one = new byte[1];
                if (await _stream.ReadAsync(one, timeout.Token) <= 0)
                    throw new InvalidOperationException("the stream closed under the test.");
                length |= (long)(one[0] & 0x7f) << shift;
                if ((one[0] & 0x80) == 0)
                    break;
                shift += 7;
            }

            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _stream.ReadAsync(body, offset, (int)length - offset, timeout.Token);
                if (read <= 0)
                    throw new InvalidOperationException("the stream closed mid-message.");
                offset += read;
            }
            return body;
        }

        // One message out: the varint length prefix, then the framed body.
        private async Task WriteMessageAsync(Frame[] frames)
        {
            if (!QuicSupported)
                throw new InvalidOperationException("The host provides no QUIC stack.");

            var body = EncodeFrames(frames);
            var prefix = new byte[5];
            var value = (ulong)body.Length;
            var index = 0;
            while (value >= 0x80)
            {
                prefix[index++] = (byte)(value | 0x80);
                value >>= 7;
            }
            prefix[index++] = (byte)value;

            await _stream.WriteAsync(prefix, 0, index, CancellationToken.None);
            await _stream.WriteAsync(body, 0, body.Length, CancellationToken.None);
            await _stream.FlushAsync(CancellationToken.None);
        }

        // The envelope codec: the protobuf canonical delimited-stream shape --
        // an unsigned varint length before each marshaled Frame.

        private static byte[] EncodeFrames(IReadOnlyList<Frame> frames)
        {
            var body = new MemoryStream();
            foreach (var frame in frames)
            {
                var marshaled = frame.ToByteArray();
                WriteVarint(body, marshaled.Length);
                body.Write(marshaled);
            }
            return body.ToArray();
        }

        private static List<Frame> ParseFrames(byte[] body)
        {
            var frames = new List<Frame>();
            var position = 0;
            while (position < body.Length)
            {
                uint length = 0;
                var shift = 0;
                int delimiter;
                for (delimiter = 0; delimiter < 5; delimiter++)
                {
                    var b = body[position + delimiter];
                    length |= (uint)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0)
                        break;
                    shift += 7;
                }
                position += delimiter + 1;
                frames.Add(Frame.Parser.ParseFrom(body, position, (int)length));
                position += (int)length;
            }
            return frames;
        }

        private static void WriteVarint(MemoryStream target, int value)
        {
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                target.WriteByte((byte)(remaining | 0x80));
                remaining >>= 7;
            }
            target.WriteByte((byte)remaining);
        }
    }

    // The upstream frame builders the from-scratch implant sends after its
    // handshake.
    private static class Frames
    {
        public static Frame Result(string taskId, TaskOutcome outcome, string output)
            => new()
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = taskId,
                    Outcome = outcome,
                    Output = output,
                }.ToByteArray()),
            };

        public static Frame ChannelOut(string taskId, string output)
            => new()
            {
                Kind = FrameKind.ChannelOutput,
                Payload = ByteString.CopyFrom(new ChannelOutput
                {
                    TaskId = taskId,
                    Data = ByteString.CopyFromUtf8(output),
                }.ToByteArray()),
            };
    }
}
