using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.CoreState.Staging;
using Rod.CoreState.Tasks;
using Rod.Audit;
using Rod.Transport;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Conformance.Tests;

// The Tier 0 conformance harness (todo.md, implant reach): a rig that drives
// a candidate implant against a live teamserver and reports pass/fail per
// contract clause -- enroll shapes, handshake order, result/chunk discipline,
// signature verification, kill-date refusal (extending/implants.md). Pointing
// it at the reference implant passes; pointing it at a deliberately broken
// one fails with the violated clause named.

/// <summary>The check-in shape a candidate speaks.</summary>
public enum CandidateTransport
{
    /// <summary>gRPC over mTLS: the reference implant's transport.</summary>
    GRpc,

    /// <summary>The plain-HTTP envelope: one POST per poll check-in.</summary>
    Envelope,
}

/// <summary>
/// Where a candidate phase points: the endpoints to dial, the credential to
/// redeem, the CA to pin, and -- for the kill-date phase -- the baked kill
/// date the candidate must refuse to outlive.
/// </summary>
public sealed record ConformanceTarget(
    string EnrollUrl,
    string BeaconHostPort,
    string StagerToken,
    string CaPemPath,
    DateTimeOffset? KillDate = null);

/// <summary>
/// The implant under test. A candidate may be an in-process loop (the rig's
/// own deliberately broken implants) or a spawned process (the reference
/// implant); the rig starts it per phase against a fresh target and stops it
/// between phases. <see cref="HasExited"/> reports whether the candidate has
/// stopped itself -- the observable half of the kill-date refusal clause.
/// The signature-verification phase probes over the gRPC stream, so a
/// candidate that speaks only the envelope is named there rather than probed.
/// </summary>
public interface IImplantCandidate : IDisposable
{
    CandidateTransport Transport { get; }

    /// <summary>Launch the candidate against the target; returns once started.</summary>
    Task StartAsync(ConformanceTarget target);

    /// <summary>Stop the candidate. Idempotent.</summary>
    Task StopAsync();

    /// <summary>True when the candidate stopped on its own (not via StopAsync).</summary>
    bool HasExited { get; }
}

/// <summary>One contract clause and how the candidate fared against it.</summary>
public sealed record ConformanceClause(string Clause, bool Passed, string Detail);

/// <summary>The per-clause outcome of one harness run.</summary>
public sealed record ConformanceReport(IReadOnlyList<ConformanceClause> Clauses)
{
    /// <summary>The clauses that failed, for the caller to name.</summary>
    public IReadOnlyList<ConformanceClause> Failed
        => Clauses.Where(c => !c.Passed).ToArray();

    public override string ToString()
        => string.Join(Environment.NewLine,
            Clauses.Select(c => $"{(c.Passed ? "PASS" : "FAIL")}  {c.Clause}: {c.Detail}"));
}

/// <summary>
/// One live teamserver plus the hostile tasking probe, and the clause battery
/// that drives a candidate against them. The main host is a real Kestrel
/// teamserver (plain-HTTP enroll, mTLS beacon); the probe is a second mTLS
/// endpoint presenting the same CA that feeds an enrolled implant crafted
/// tasking -- unsigned, wrongly signed, signed for another implant, and a
/// correctly signed control -- and records what came back. The signature
/// clause reads the probe: a conforming implant refuses the first three and
/// runs the control.
/// </summary>
public sealed class ConformanceRig : IAsyncDisposable
{
    // The clause names are the contract; tests assert against them verbatim.
    public const string EnrollClause = "enroll.public-key-and-token";
    public const string HandshakeClause = "handshake.first-frame-ok";
    public const string RoundTripClause = "task.round-trip";
    public const string ChunkClause = "chunk.discipline";
    public const string SignatureClause = "signature.verification";
    public const string KillDateClause = "kill-date.refusal";

    private static readonly TimeSpan ObserveDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KillDateGrace = TimeSpan.FromSeconds(8);

    private readonly IHost _host;
    private readonly WebApplication _probe;
    private readonly TaskingProbe _taskingProbe;
    private readonly string _caPemPath;
    private readonly IImplantRepository _implants;
    private readonly ISessionRegistry _sessions;
    private readonly EngagementService _engagements;
    private readonly TaskService _taskService;
    private readonly IArtifactStore _artifacts;
    private readonly ITaskRepository _taskRecords;
    private readonly OperatorId _operator;

    public int EnrollPort { get; }
    public int BeaconPort { get; }
    public int ProbePort { get; }
    public string EnrollUrl => $"http://127.0.0.1:{EnrollPort}/implants/enroll";
    public string BeaconHostPort => $"127.0.0.1:{BeaconPort}";
    public string ProbeHostPort => $"127.0.0.1:{ProbePort}";

    private ConformanceRig(
        IHost host, WebApplication probe, TaskingProbe taskingProbe, string caPemPath,
        int enrollPort, int beaconPort, int probePort)
    {
        _host = host;
        _probe = probe;
        _taskingProbe = taskingProbe;
        _caPemPath = caPemPath;
        _implants = host.Services.GetRequiredService<IImplantRepository>();
        _sessions = host.Services.GetRequiredService<ISessionRegistry>();
        _engagements = host.Services.GetRequiredService<EngagementService>();
        _taskService = host.Services.GetRequiredService<TaskService>();
        _artifacts = host.Services.GetRequiredService<IArtifactStore>();
        _taskRecords = host.Services.GetRequiredService<ITaskRepository>();
        _operator = host.Services.GetRequiredService<IOperatorRepository>()
            .FindByHandleAsync("conformance").GetAwaiter().GetResult()!.Id;
        EnrollPort = enrollPort;
        BeaconPort = beaconPort;
        ProbePort = probePort;
    }

    public static async Task<ConformanceRig> StartAsync()
    {
        var enrollPort = GetFreeTcpPort();
        var beaconPort = GetFreeTcpPort();
        var probePort = GetFreeTcpPort();

        // The live teamserver: the transport core on a real Kestrel host --
        // plain-HTTP enroll, mTLS beacon. The harness observes outcomes
        // through the core services the operator API drives.
        var host = TransportHost.CreateHostBuilder().ConfigureWebHost(web => web
                .UseRodMtls(beaconPort)
                .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(enrollPort)))
            .Build();

        // The harness's operator identity: tasking must attribute to someone.
        {
            var operators = host.Services.GetRequiredService<IOperatorRepository>();
            await operators.SaveAsync(Operator.Register(
                OperatorId.New(), "conformance", "Conformance Harness", DateTimeOffset.UtcNow));
        }
        await host.StartAsync();

        var ca = host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var caPemPath = Path.Combine(
            Path.GetTempPath(), "rod-conformance-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(caPemPath, Pem(
            ca.GetCaCertificate().Export(X509ContentType.Cert)));

        // The hostile tasking probe: a second mTLS endpoint presenting the same
        // CA as its server identity, so an enrolled implant's pinning accepts
        // it, feeding crafted tasking and recording the results.
        var taskingProbe = new TaskingProbe(ca);
        var probe = WebApplication.CreateBuilder();
        probe.Services.AddGrpc();
        probe.Services.AddSingleton(taskingProbe);
        probe.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(probePort, listen =>
            listen.UseHttps(https =>
            {
                https.ServerCertificateSelector = (_, _) => ca.GetCaCertificate();
                https.ClientCertificateMode =
                    Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate;
            })));
        var probeApp = probe.Build();
        probeApp.MapGrpcService<ProbeBeaconService>();
        await probeApp.StartAsync();

        return new ConformanceRig(host, probeApp, taskingProbe, caPemPath, enrollPort, beaconPort, probePort);
    }

    /// <summary>
    /// Runs the clause battery against one candidate, in three phases: the
    /// live server (enroll, handshake, tasking, chunking), the probe
    /// (signature verification), and a past kill date (self-termination
    /// before any check-in).
    /// </summary>
    public async Task<ConformanceReport> RunAsync(IImplantCandidate candidate)
    {
        var clauses = new List<ConformanceClause>();

        // ---- Phase 1: the live teamserver.
        var engagement = await MintEngagementTokenAsync();
        try
        {
            await candidate.StartAsync(new ConformanceTarget(
                EnrollUrl, BeaconHostPort, engagement.Token, _caPemPath));

            var enrolled = await UntilAsync(ObserveDeadline, async () =>
                (await _implants.ListByEngagementAsync(engagement.EngagementId)).Count > 0);
            clauses.Add(new ConformanceClause(EnrollClause, enrolled,
                enrolled ? "enrolled with an RSA-2048 SPKI public key via the one-use token"
                         : "no enrollment appeared in the engagement within the deadline"));

            var online = await UntilAsync(ObserveDeadline, async () =>
                (await _sessions.ListActiveAsync(engagement.EngagementId)).Count > 0);
            clauses.Add(new ConformanceClause(HandshakeClause, online,
                online ? "the first frame was a handshake and the session opened"
                         : "no session opened: the server never accepted a handshake-first check-in"));

            if (!online)
            {
                // Without a session there is nothing downstream to observe;
                // name the cascade rather than waiting out every deadline.
                clauses.Add(new ConformanceClause(RoundTripClause, false, "no live session (handshake refused)"));
                clauses.Add(new ConformanceClause(ChunkClause, false, "no live session (handshake refused)"));
            }
            else
            {
                var implant = (await _implants.ListByEngagementAsync(engagement.EngagementId))[0].Id;

                var marker = "rod-conformance-" + Guid.NewGuid().ToString("N")[..8];
                var issued = await _taskService.IssueAsync(new IssueTaskCommand(
                    engagement.EngagementId, implant, _operator, "shell.exec", $"echo {marker}"));
                var completed = await UntilAsync(ObserveDeadline, async () =>
                {
                    var task = await _taskRecords.FindAsync(issued.TaskId);
                    return task is { Status: Rod.CoreState.Tasks.TaskStatus.Completed }
                        && task.Outcome == Rod.CoreState.Tasks.TaskOutcome.Succeeded
                        && task.Output?.Contains(marker) == true;
                });
                clauses.Add(new ConformanceClause(RoundTripClause, completed,
                    completed ? "a shell.exec task completed with its own marker in the output"
                             : "the shell.exec task did not complete Succeeded with the marker in its output"));

                var content = RandomNumberGenerator.GetBytes(1536 * 1024);
                var path = Path.Combine(Path.GetTempPath(),
                    "rod-conformance-pull-" + Guid.NewGuid().ToString("N") + ".bin");
                await File.WriteAllBytesAsync(path, content);
                try
                {
                    var pull = await _taskService.IssueAsync(new IssueTaskCommand(
                        engagement.EngagementId, implant, _operator, "file.pull", path));
                    var artifact = await UntilAsync(ObserveDeadline, async () =>
                    {
                        var stored = (await _artifacts.ForTaskAsync(pull.TaskId.Value))
                            .FirstOrDefault(a => a.Name == Path.GetFileName(path));
                        return stored is { Content.Length: > 0 } && stored.Content.AsSpan().SequenceEqual(content);
                    });
                    clauses.Add(new ConformanceClause(ChunkClause, artifact,
                        artifact ? "the file.pull artifact reassembled byte-for-byte in strict sequence"
                                 : "the exfil chunk run never reassembled into the byte-equal artifact (sequence discipline violated, or no chunks arrived)"));
                }
                finally
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        finally
        {
            await candidate.StopAsync();
        }

        // ---- Phase 2: the hostile tasking probe.
        if (candidate.Transport != CandidateTransport.GRpc)
        {
            clauses.Add(new ConformanceClause(SignatureClause, false,
                "the probe speaks the gRPC stream; an envelope-only candidate cannot be probed"));
        }
        else
        {
            var probeEngagement = await MintEngagementTokenAsync();
            _taskingProbe.Reset();
            try
            {
                await candidate.StartAsync(new ConformanceTarget(
                    EnrollUrl, ProbeHostPort, probeEngagement.Token, _caPemPath));
                var verdict = await _taskingProbe.AwaitVerdictAsync(ObserveDeadline);
                clauses.Add(new ConformanceClause(SignatureClause, verdict.Passed, verdict.Detail));
            }
            finally
            {
                await candidate.StopAsync();
            }
        }

        // ---- Phase 3: the past kill date.
        var killEngagement = await MintEngagementTokenAsync();
        try
        {
            await candidate.StartAsync(new ConformanceTarget(
                EnrollUrl, BeaconHostPort, killEngagement.Token, _caPemPath,
                KillDate: DateTimeOffset.UtcNow.AddHours(-1)));
            await Task.Delay(KillDateGrace);
            var ranAnyway =
                (await _implants.ListByEngagementAsync(killEngagement.EngagementId)).Count > 0;
            var stayed = !ranAnyway && candidate.HasExited;
            clauses.Add(new ConformanceClause(KillDateClause, stayed,
                stayed ? "refused to run past the baked kill date: no enrollment, no check-in, exited"
                         : "the candidate ran past its kill date (enrolled or stayed alive anyway)"));
        }
        finally
        {
            await candidate.StopAsync();
        }

        return new ConformanceReport(clauses);
    }

    public async ValueTask DisposeAsync()
    {
        try { File.Delete(_caPemPath); } catch { }
        await _probe.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string Pem(byte[] der)
        => "-----BEGIN CERTIFICATE-----\n"
           + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)
           + "\n-----END CERTIFICATE-----\n";

    private async Task<(EngagementId EngagementId, string Token)> MintEngagementTokenAsync()
    {
        var created = await _engagements.CreateEngagementAsync(
            new CreateEngagementCommand(_operator, "conformance-" + Guid.NewGuid().ToString("N")[..8]));
        var minted = await _engagements.MintStagerTokenForOwnerAsync(
            new MintStagerTokenCommand(created.EngagementId));
        return (created.EngagementId, minted.Secret);
    }

    private static async Task<bool> UntilAsync(TimeSpan deadline, Func<Task<bool>> condition)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            if (await condition())
                return true;
            await Task.Delay(250);
        }
        return false;
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

/// <summary>The probe's verdict on the signature clause.</summary>
public sealed record SignatureVerdict(bool Passed, string Detail);

/// <summary>
/// Records the crafted tasking outcomes: for each of unsigned, wrongly
/// signed, and signed-for-another-implant, a conforming implant reports the
/// task Failed and runs nothing; for the correctly signed control it runs
/// the command. A candidate that skips verification executes the crafted
/// tasking and is named by which case betrayed it.
/// </summary>
internal sealed class TaskingProbe
{
    private static readonly TimeSpan ResultWait = TimeSpan.FromSeconds(20);

    private readonly IImplantCertificateAuthority _ca;
    private readonly object _gate = new();
    private readonly Dictionary<string, Rod.V1.TaskOutcome> _results = new();

    internal TaskingProbe(IImplantCertificateAuthority ca)
    {
        _ca = ca;
    }

    /// <summary>Clears the recorded results for a fresh candidate phase.</summary>
    public void Reset()
    {
        lock (_gate)
            _results.Clear();
    }

    /// <summary>
    /// Waits for every crafted task's outcome (or the deadline), then renders
    /// the verdict naming the first betraying case.
    /// </summary>
    public async Task<SignatureVerdict> AwaitVerdictAsync(TimeSpan deadline)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            lock (_gate)
            {
                if (_results.Count == 4)
                    return Verdict();
            }
            await Task.Delay(250);
        }
        lock (_gate)
            return Verdict();
    }

    private SignatureVerdict Verdict()
    {
        var cases = new (string Kind, Rod.V1.TaskOutcome Expected)[]
        {
            ("unsigned tasking", Rod.V1.TaskOutcome.Failed),
            ("wrongly signed tasking", Rod.V1.TaskOutcome.Failed),
            ("tasking signed for another implant", Rod.V1.TaskOutcome.Failed),
            ("correctly signed control", Rod.V1.TaskOutcome.Succeeded),
        };
        foreach (var (kind, expected) in cases)
        {
            if (!_results.TryGetValue(kind, out var outcome))
                return new SignatureVerdict(false, $"no result arrived for the {kind} probe");
            if (outcome != expected)
                return new SignatureVerdict(false,
                    expected == Rod.V1.TaskOutcome.Failed
                        ? $"executed the {kind}: it reported {outcome} instead of refusing it"
                        : $"refused the {kind}: it reported {outcome} instead of running it");
        }
        return new SignatureVerdict(true,
            "refused unsigned, wrongly signed, and cross-implant tasking; ran the signed control");
    }

    /// <summary>
    /// The probe script, transport-neutral: handshake, then one crafted task
    /// at a time, each awaited for its result. Returns when the connection
    /// ends or every case resolved; unrelated frames are ignored.
    /// </summary>
    public async Task RunScriptAsync(
        Func<Task<Frame?>> readFrame,
        Func<Frame, Task> writeFrame,
        CancellationToken cancellationToken)
    {
        // Handshake: the implant speaks first; echo OK whatever it advertised.
        var first = await readFrame();
        if (first is null)
            return;
        HandshakeRequest handshake;
        try
        {
            handshake = HandshakeRequest.Parser.ParseFrom(first.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return; // Not a handshake: the missing results are the outcome.
        }

        await writeFrame(new Frame
        {
            Payload = ByteString.CopyFrom(new HandshakeResponse
            {
                Status = HandshakeStatus.Ok,
                Version = new ProtocolVersion { Major = 1, Minor = 0 },
                EngagementId = string.Empty,
            }.ToByteArray()),
        });

        foreach (var kind in new[] { "unsigned tasking", "wrongly signed tasking",
                                     "tasking signed for another implant", "correctly signed control" })
        {
            var request = new TaskRequest
            {
                TaskId = Guid.NewGuid().ToString(),
                Verb = "shell.exec",
                Arguments = "echo probe-" + Guid.NewGuid().ToString("N")[..8],
            };
            switch (kind)
            {
                case "wrongly signed tasking":
                    request.Signature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(256));
                    break;
                case "tasking signed for another implant":
                    // Validly signed by the real CA, but over a tuple naming a
                    // different implant: the verifier's own id must reject it.
                    request.Signature = ByteString.CopyFrom(_ca.SignTasking(
                        Guid.NewGuid().ToString(), request.TaskId, request.Verb, request.Arguments));
                    break;
                case "correctly signed control":
                    request.Signature = ByteString.CopyFrom(_ca.SignTasking(
                        handshake.ImplantId, request.TaskId, request.Verb, request.Arguments));
                    break;
            }

            await writeFrame(new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) });

            // Await this case's result with its own bound; the connection
            // ending first leaves the rest of the cases unrecorded.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResultWait);
            try
            {
                while (true)
                {
                    var incoming = await readFrame();
                    if (incoming is null)
                        return;
                    TaskResult result;
                    try
                    {
                        result = TaskResult.Parser.ParseFrom(incoming.Payload);
                    }
                    catch (InvalidProtocolBufferException)
                    {
                        continue;
                    }
                    if (result.TaskId != request.TaskId)
                        continue;
                    lock (_gate)
                        _results[kind] = result.Outcome;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>The probe's gRPC surface: one scripted connection per CheckIn.</summary>
internal sealed class ProbeBeaconService : Beacon.BeaconBase
{
    private readonly TaskingProbe _probe;

    public ProbeBeaconService(TaskingProbe probe)
    {
        _probe = probe;
    }

    public override async Task CheckIn(
        IAsyncStreamReader<Frame> requestStream,
        IServerStreamWriter<Frame> responseStream,
        ServerCallContext context)
    {
        async Task<Frame?> Read()
            => await requestStream.MoveNext(context.CancellationToken) ? requestStream.Current : null;

        Task Write(Frame frame) => responseStream.WriteAsync(frame);

        await _probe.RunScriptAsync(Read, Write, context.CancellationToken);
    }
}
