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
/// QUIC client, the stream contact framing, and the protobuf messages --
/// sees a listener entry created through the operator API carry a contact
/// end to end: TLS terminated at the CA-issued leaf with no client
/// certificate anywhere, the handshake as the identity, tasking pushed onto
/// the open stream the moment it is queued, and the live channel an
/// operator types into -- the native-channel tier, the point of the duplex
/// shape.
/// </summary>
public class QuicBeaconRoundTripTests
{
    [QuicFact]
    public async Task AQuicListenerEntry_CreatedThroughTheOperatorApi_CarriesAContactEndToEnd()
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

            // The contact: dial, open the stream, speak first. The implant
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
        // dial the artifact's contact client picks by -- the transport's own
        // scheme completed over the bare public endpoint. Either mode bakes:
        // the poll shape cycles the session on the client's idle window at
        // the baked cadence.
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

            var built = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/payloads",
                Request(mode: "poll", beaconListenerId: listener!.Id));
            built.EnsureSuccessStatusCode();

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

    // Enrollment over QUIC (architecture.md Sec 8, the designed
    // full-independence step): the opening stream's first exchange is an
    // enroll -- the JSON body the web route carries promoted into the
    // rod.v1 frame grammar -- answered on the same stream and followed
    // immediately by the ordinary handshake. One connection carries
    // enroll-then-session; the leaf binds the implant's public key; the
    // recorded implant carries the reported host facts; the tasking then
    // rides the same session any handshake-opened one would.
    [QuicFact]
    public async Task TheQuicStream_CarriesEnrollThenSession_OnOneConnection()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);
            var token = await MintStagerTokenAsync(client, engagementId);

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
            // The implant's own keypair: only the public half crosses the
            // exchange, and the issued leaf must bind it (architecture.md
            // Sec 9).
            using var implantKey = System.Security.Cryptography.ECDsa.Create(
                System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

            await using var session = await QuicImplant.ConnectForEnrollAsync(port, ca);
            var enroll = await session.EnrollExchangeAsync(new Rod.V1.EnrollRequest
            {
                StagerTokenSecret = token,
                PublicKey = ByteString.CopyFrom(implantKey.ExportSubjectPublicKeyInfo()),
                Hostname = "quic-host01",
                KillDate = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
            });

            Assert.Equal(EnrollStatus.Ok, enroll.Status);
            Assert.False(string.IsNullOrWhiteSpace(enroll.ImplantId));
            Assert.Equal(engagementId, enroll.EngagementId);

            // The leaf binds the implant's public key; the chain rides along.
            using var leaf = X509CertificateLoader.LoadCertificate(enroll.LeafCertificate.ToByteArray());
            using var leafKey = leaf.GetECDsaPublicKey()!;
            Assert.Equal(implantKey.ExportSubjectPublicKeyInfo(), leafKey.ExportSubjectPublicKeyInfo());
            Assert.NotEmpty(enroll.CaChain);

            // A manually minted token names no build, so the answer carries
            // no per-artifact contact key.
            Assert.False(enroll.HasEnvelopeKeyId);

            // The host facts crossed the frame exchange: the roster's implant
            // is the one the enroll issued, with the reported hostname.
            var implants = await client.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                $"/engagements/{engagementId}/implants");
            var recorded = Assert.Single(implants!);
            Assert.Equal(enroll.ImplantId, recorded!.ImplantId);
            Assert.Equal("quic-host01", recorded.Hostname);

            // The ordinary handshake follows on the same stream, and the
            // session it opens carries tasking like any other.
            var handshake = await session.HandshakeAsync(enroll.ImplantId);
            Assert.Equal(HandshakeStatus.Ok, handshake.Status);

            var marker = "rod-quic-enroll-marker-" + Guid.NewGuid().ToString("N")[..8];
            var issued = await client.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = enroll.ImplantId, Verb = "shell.exec", Arguments = $"echo {marker}" });
            issued.EnsureSuccessStatusCode();
            var issuedBody = await issued.Content.ReadFromJsonAsync<IssuedBody>();

            var request = TaskRequest.Parser.ParseFrom(await session.ReceiveSingleFrameAsync());
            Assert.Equal(issuedBody!.TaskId, request.TaskId);
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

    // The scope rule the QUIC listener enforces directly (architecture.md
    // Sec 8): a token minted for another engagement is refused whole and
    // unspent -- the QUIC listener knows its own engagement, the fact the
    // HTTP route resolves from the local port. The refused token still
    // enrolls its own engagement over the web route afterwards.
    [QuicFact]
    public async Task TheQuicStream_RefusesAForeignEngagementsToken_WithoutSpendingIt()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var listenerEngagement = await CreateEngagementAsync(client);
            var foreignEngagement = await CreateEngagementAsync(client);
            var foreignToken = await MintStagerTokenAsync(client, foreignEngagement);

            var port = GetFreeUdpPort();
            var created = await client.PostAsJsonAsync(
                $"/engagements/{listenerEngagement}/listeners",
                new ListenerEndpoints.CreateListenerRequest(
                    Name: "runtime-quic",
                    Transport: "quic",
                    BindAddress: $"127.0.0.1:{port}",
                    PublicEndpoint: $"10.0.0.5:{port}"));
            created.EnsureSuccessStatusCode();

            var authority = host.Services.GetRequiredService<IImplantCertificateAuthority>();
            using var ca = authority.GetCaCertificate();

            await using var session = await QuicImplant.ConnectForEnrollAsync(port, ca);
            var enroll = await session.EnrollExchangeAsync(
                new Rod.V1.EnrollRequest { StagerTokenSecret = foreignToken });
            Assert.Equal(EnrollStatus.BadToken, enroll.Status);
            Assert.True(string.IsNullOrEmpty(enroll.ImplantId));

            // Unspent: the same secret still enrolls its own engagement over
            // the web route -- the refusal kept its use.
            var webEnroll = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: foreignToken, Class: null));
            Assert.Equal(HttpStatusCode.OK, webEnroll.StatusCode);
        }
    }

    // The per-artifact contact key on the QUIC enroll answer
    // (architecture.md Sec 8/9): a token minted by a build names the
    // artifact, the artifact names the key, and the QUIC-enrolled implant
    // receives at enroll the key its listener-side binding demands.
    [QuicFact]
    public async Task TheQuicEnroll_AnswerCarriesTheBuildsContactKey()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            // A minted token plus the payload record a build would leave
            // behind: the token id binds the record, the record the key.
            var mint = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
            mint.EnsureSuccessStatusCode();
            var token = await mint.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
            var (keyId, key) = Rod.Transport.Payloads.AesGcmEnvelope.Mint();
            await host.Services.GetRequiredService<Rod.Audit.IPayloadStore>().SaveAsync(
                new Rod.Audit.PayloadRecord(
                    Guid.NewGuid(), Guid.Parse(engagementId), "Stage2", "DotNet",
                    "application/octet-stream", new string('a', 64), Array.Empty<byte>(), 0,
                    DateTimeOffset.UtcNow,
                    TokenId: Guid.Parse(token!.StagerTokenId),
                    EnvelopeKeyId: keyId,
                    EnvelopeKey: key));

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

            await using var session = await QuicImplant.ConnectForEnrollAsync(port, ca);
            var enroll = await session.EnrollExchangeAsync(
                new Rod.V1.EnrollRequest { StagerTokenSecret = token.Secret });
            Assert.Equal(EnrollStatus.Ok, enroll.Status);
            Assert.True(enroll.HasEnvelopeKeyId);
            Assert.Equal(keyId.ToByteArray(), enroll.EnvelopeKeyId.ToByteArray());
            Assert.Equal(key, enroll.EnvelopeKey.ToByteArray());
        }
    }

    // The build story (architecture.md Sec 8, enrollment over QUIC): a quic
    // listener is enroll-nameable and the parser bakes its dial -- the
    // transport's own scheme over the bare public endpoint -- with the
    // beacon deriving from it. Either mode bakes: the poll shape cycles the
    // session on the client's idle window at the baked cadence. Registry-
    // seeded (no socket binds), so it runs wherever the parser does.
    [Fact]
    public async Task ABuildNamingAQuicListenerAsItsEnroll_BakesTheQuicDial()
    {
        var (client, host, operatorId) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await CreateEngagementAsync(client);

            var registry = host.Services.GetRequiredService<IListenerRegistry>();
            var listener = Listener.Define(
                ListenerId.New(), "parser-quic", "quic", "127.0.0.1:9443", "10.0.0.5:9443",
                DateTimeOffset.UtcNow, new EngagementId(Guid.Parse(engagementId)));
            await registry.RegisterAsync(listener);

            var poll = await PayloadBuildRequestParser.ParseAsync(
                EnrollRequest(listener.Id.ToString(), mode: "poll"),
                new EngagementId(Guid.Parse(engagementId)),
                operatorId,
                host.Services.GetRequiredService<Rod.Audit.IPayloadStore>(),
                registry,
                host.Services.GetRequiredService<IImplantCertificateAuthority>(),
                CancellationToken.None);
            Assert.Null(poll.Error);
            Assert.Equal("quic://10.0.0.5:9443", poll.Request!.Transport.Endpoint);
            Assert.Equal("poll", poll.Request.Mode);

            var parse = await PayloadBuildRequestParser.ParseAsync(
                EnrollRequest(listener.Id.ToString()),
                new EngagementId(Guid.Parse(engagementId)),
                operatorId,
                host.Services.GetRequiredService<Rod.Audit.IPayloadStore>(),
                registry,
                host.Services.GetRequiredService<IImplantCertificateAuthority>(),
                CancellationToken.None);
            Assert.Null(parse.Error);
            Assert.Equal("quic://10.0.0.5:9443", parse.Request!.Transport.Endpoint);
            // The derived single-front shape: no beacon is named (the bake
            // derives the quic dial from the enroll endpoint itself -- it
            // carries no path to strip), the same discipline a web front's
            // envelope cycle follows.
            Assert.Null(parse.Request.Transport.BeaconEndpoint);

        }

        static Rod.Transport.Endpoints.PayloadEndpoints.BuildPayloadRequest EnrollRequest(
            string listenerId, string? mode = null)
            => new(
                Language: null,
                Class: null,
                TargetOs: null,
                TargetArch: null,
                Endpoint: null,
                UriPath: null,
                SleepSeconds: null,
                JitterSeconds: null,
                KillDate: null,
                Mode: mode,
                ListenerId: listenerId);
    }

    private static async Task<string> MintStagerTokenAsync(HttpClient client, string engagementId)
    {
        var mint = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
        mint.EnsureSuccessStatusCode();
        var token = await mint.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        return token!.Secret;
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

    // The from-scratch QUIC implant: the stream contact framing over one
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
            var implant = await DialAsync(port, ca);

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

        // The enroll carriage's dial: connect and open the stream without
        // speaking -- the opening exchange may be an enroll instead of a
        // handshake (architecture.md Sec 8, enrollment over QUIC), and the
        // exchange methods below speak in order.
        public static async Task<QuicImplant> ConnectForEnrollAsync(int port, X509Certificate2 ca)
            => await DialAsync(port, ca);

        private static async Task<QuicImplant> DialAsync(int port, X509Certificate2 ca)
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
            return new QuicImplant(connection, stream);
        }

        // The opening enroll exchange: a kind-bearing EnrollRequest frame
        // answered by an EnrollResponse frame on the same stream.
        public async Task<Rod.V1.EnrollResponse> EnrollExchangeAsync(Rod.V1.EnrollRequest request)
        {
            await SendFramesAsync(new Frame
            {
                Kind = FrameKind.EnrollRequest,
                Payload = ByteString.CopyFrom(request.ToByteArray()),
            });
            return Rod.V1.EnrollResponse.Parser.ParseFrom(
                await ReceiveSingleFrameAsync(FrameKind.EnrollResponse));
        }

        // The ordinary handshake, sent after a successful enroll on the same
        // connection -- enroll-then-session on one stream.
        public async Task<HandshakeResponse> HandshakeAsync(string implantId)
        {
            var handshake = new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplantId = implantId,
                ReplayNonces = true,
            };
            handshake.Capabilities.Add("shell.exec");
            await SendFramesAsync(new Frame
            {
                Payload = ByteString.CopyFrom(handshake.ToByteArray()),
            });
            return await ReceiveHandshakeAsync();
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
