using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// ADR 0004 acceptance: an implant streams an artifact off the target as
/// ExfilChunk frames on the beacon stream, and the teamserver reassembles the
/// chunks into an artifact scoped to the engagement and bound to the task that
/// triggered the push. Drives the full slice end to end through a real Kestrel
/// mTLS endpoint -- the operator POSTs an <c>exfil.push</c> task over HTTP, the
/// beacon stream pushes it to the implant, the implant writes back a result
/// followed by one ExfilChunk frame, and the operator reads the captured
/// artifact back through the artifact store alongside the ExfilCaptured audit
/// event (architecture.md Sec 10.1 exfil, Sec 11).
/// </summary>
public class ExfilRoundTripTests
{
    [Fact]
    public async Task ExfilPush_ChunksRoundTrip_ToEngagementScopedArtifact()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var artifacts = env.Host.Services.GetRequiredService<IArtifactStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);
        var operatorId = OperatorId.New();

        // Open the beacon stream and complete the handshake first.
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, 1, 0));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // Operator tasks the implant over HTTP. exfil.push is Stage-2 gated, and
        // the enrolled implant is Stage-2, so issuance succeeds.
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), IssuedBy = operatorId.Value, Verb = "exfil.push", Arguments = "loot.txt /opt/secret/loot.txt" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);
        Assert.Equal("exfil.push", issuedBody!.Verb);

        // The server pushes the task downstream; the implant reads it.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var request = TaskRequest.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        Assert.Equal("exfil.push", request.Verb);

        // The implant writes back a TaskResult (kind = TASK_RESULT), then the
        // streamed ExfilChunk frames (kind = EXFIL_CHUNK). Here the implant sends
        // the result, then a single terminal chunk carrying the file bytes.
        var result = new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "pushed loot.txt: 18 bytes, 1 chunks",
        };
        await call.RequestStream.WriteAsync(ResultFrame(result));

        var payload = System.Text.Encoding.UTF8.GetBytes("loot file contents\n");
        var chunk = new ExfilChunk
        {
            TaskId = request.TaskId,
            Name = "loot.txt",
            ContentType = "text/plain",
            Sequence = 1,
            Terminal = true,
            Data = ByteString.CopyFrom(payload),
        };
        await call.RequestStream.WriteAsync(ExfilChunkFrame(chunk));

        // Wait for the server to capture the result and the exfil chunk. The
        // three-event task arc (Issued/Dispatched/Completed) plus the
        // ExfilCaptured event bound to the same task id both land before the
        // readback, so poll for the artifact rather than racing the stream.
        var taskId = Guid.Parse(request.TaskId);
        await WaitUntilAsync(async () => (await audit.ForTaskAsync(taskId)).Count >= 4);
        await WaitUntilAsync(async () => (await artifacts.ForTaskAsync(taskId)).Count >= 1);

        // The captured artifact is scoped to the engagement, bound to the task,
        // and carries the streamed bytes verbatim.
        var captured = (await artifacts.ForTaskAsync(taskId))[0];
        Assert.Equal(implant.EngagementId.Value, captured.EngagementId);
        Assert.Equal(taskId, captured.TaskId);
        Assert.Equal("loot.txt", captured.Name);
        Assert.Equal("text/plain", captured.ContentType);
        Assert.Equal(payload, captured.Content);
        Assert.Equal(payload.Length, captured.Size);

        // The audit trail for the task now carries the ExfilCaptured event
        // alongside the three-event task arc.
        var taskAudit = await audit.ForTaskAsync(taskId);
        var exfilEvent = Assert.Single(taskAudit, e => e.Kind == AuditEventKind.ExfilCaptured);
        Assert.Equal("exfil.push", exfilEvent.Verb);
        Assert.Equal("loot.txt;text/plain", exfilEvent.Payload);
        Assert.Equal(taskId, exfilEvent.TaskId);
        Assert.Equal(implant.Id.Value, exfilEvent.ImplantId);

        // The engagement-wide trail also reflects the capture.
        var trail = await audit.ListAsync(implant.EngagementId.Value);
        Assert.Contains(trail, e => e.Kind == AuditEventKind.ExfilCaptured);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task ExfilPush_MultiChunk_ReassemblesInOrder()
    {
        // A larger payload spanning three chunks: the first two are non-terminal,
        // the third closes the stream. The server must reassemble them in
        // sequence order and store the full payload as one artifact.
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        var artifacts = env.Host.Services.GetRequiredService<IArtifactStore>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);
        var operatorId = OperatorId.New();

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, 1, 0));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), IssuedBy = operatorId.Value, Verb = "exfil.push", Arguments = "blob.bin /opt/blob.bin" });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var request = TaskRequest.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        var taskId = Guid.Parse(request.TaskId);

        // Result first.
        await call.RequestStream.WriteAsync(ResultFrame(new TaskResult
        {
            TaskId = request.TaskId,
            Outcome = TaskOutcome.Succeeded,
            Output = "pushed blob.bin",
        }));

        // Three chunks: the first chunk size mirrors the implant's 512 KiB slice,
        // kept small here so the test is fast while still exercising the
        // multi-chunk reassembly path.
        var partA = new byte[1024];
        var partB = new byte[1024];
        var partC = new byte[512];
        for (var i = 0; i < partA.Length; i++) partA[i] = (byte)(i % 251);
        for (var i = 0; i < partB.Length; i++) partB[i] = (byte)(i % 253);
        for (var i = 0; i < partC.Length; i++) partC[i] = (byte)(i % 255);
        var full = new byte[partA.Length + partB.Length + partC.Length];
        Array.Copy(partA, 0, full, 0, partA.Length);
        Array.Copy(partB, 0, full, partA.Length, partB.Length);
        Array.Copy(partC, 0, full, partA.Length + partB.Length, partC.Length);

        await call.RequestStream.WriteAsync(ExfilChunkFrame(new ExfilChunk
        {
            TaskId = request.TaskId,
            Name = "blob.bin",
            ContentType = "application/octet-stream",
            Sequence = 1,
            Terminal = false,
            Data = ByteString.CopyFrom(partA),
        }));
        await call.RequestStream.WriteAsync(ExfilChunkFrame(new ExfilChunk
        {
            TaskId = request.TaskId,
            Name = "blob.bin",
            ContentType = "application/octet-stream",
            Sequence = 2,
            Terminal = false,
            Data = ByteString.CopyFrom(partB),
        }));
        await call.RequestStream.WriteAsync(ExfilChunkFrame(new ExfilChunk
        {
            TaskId = request.TaskId,
            Name = "blob.bin",
            ContentType = "application/octet-stream",
            Sequence = 3,
            Terminal = true,
            Data = ByteString.CopyFrom(partC),
        }));

        await WaitUntilAsync(async () => (await artifacts.ForTaskAsync(taskId)).Count >= 1);

        var captured = (await artifacts.ForTaskAsync(taskId))[0];
        Assert.Equal("blob.bin", captured.Name);
        Assert.Equal(full, captured.Content);
        Assert.Equal(full.Length, captured.Size);

        await call.RequestStream.CompleteAsync();
    }

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(), "key-exfil",
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
    }

    private static Frame HandshakeFrame(ImplantId implant, int major, int minor)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = major, Minor = minor },
            ImplantId = implant.ToString(),
            Capabilities = { "exfil.push", "exfil.stage" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static Frame ResultFrame(TaskResult result)
        => new()
        {
            Payload = ByteString.CopyFrom(result.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

    private static Frame ExfilChunkFrame(ExfilChunk chunk)
        => new()
        {
            Payload = ByteString.CopyFrom(chunk.ToByteArray()),
            Kind = FrameKind.ExfilChunk,
        };

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    // Polls until condition is true or the timeout elapses. The capture/audit
    // append runs on the stream thread, asynchronously to the HTTP readback, so
    // the readback needs to wait for it rather than race it.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
    }

    // Minimal DTO for the task-issuance JSON response; the transport owns the
    // wire shape.
    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
        public string Verb { get; set; } = "";
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the TaskRoundTripTests harness.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync()
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            env.Host = TransportHost.CreateHostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseRodMtls(env.MtlsPort)
                    .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
                .Build();
            await env.Host.StartAsync();

            env.Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}") };
            return env;
        }

        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, RSA leafKey)
        {
            var leafWithKey = leaf.HasPrivateKey ? leaf : leaf.CopyWithPrivateKey(leafKey);
            var ca = Host.Services.GetRequiredService<IImplantCertificateAuthority>().GetCaCertificate();

            var handler = new SocketsHttpHandler();
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { leafWithKey },
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    if (cert is null)
                        return false;
                    chain!.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain.ChainPolicy.ExtraStore.Add(ca);
                    return chain.Build((X509Certificate2)cert);
                },
            };

            return GrpcChannel.ForAddress($"https://127.0.0.1:{MtlsPort}", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
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
