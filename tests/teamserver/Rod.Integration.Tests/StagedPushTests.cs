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
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.V1;
using Task = System.Threading.Tasks.Task;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for staged uploads (architecture.md Sec 10, the per-verb typed
/// arm): a payload too large for the task arguments string is staged as a
/// task-bound artifact at issuance, its sha256 rides the signed arguments, and
/// the implant demands and reassembles the chunk run over the tasking channel
/// -- the mirror of exfil chunking in the other direction. Drives the full
/// slice end to end through a real Kestrel mTLS endpoint with a 10 MiB
/// payload: the AC is that the file lands whole on the target through the
/// tasking channel.
/// </summary>
public class StagedPushTests
{
    [Fact]
    public async Task TenMiBUpload_LandsWhole_ThroughTheTaskingChannel()
    {
        var content = RandomNumberGenerator.GetBytes(10 * 1024 * 1024);
        var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

        // Open the beacon stream and complete the handshake first.
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(HandshakeStatus.Ok, ParseResponse(call.ResponseStream.Current).Status);

        // The operator issues the push with the payload as content, not as an
        // arguments string. The target path is the whole argument; the server
        // appends the sha256 token and stages the bytes.
        var targetPath = Path.Combine(Path.GetTempPath(), "rod-staged-" + Guid.NewGuid().ToString("N") + ".bin");
        await AuthenticatedHost.LoginAsync(env.Http);
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "file.push", Arguments = targetPath, Content = content });
        issued.EnsureSuccessStatusCode();
        var issuedBody = await issued.Content.ReadFromJsonAsync<TaskIssuedBody>();
        Assert.NotNull(issuedBody);
        Assert.EndsWith("sha256:" + expectedHash, issuedBody!.Arguments);

        // The task arrives marked staged: the typed arm's advisory size on the
        // TaskRequest, the binding hash inside the signed arguments.
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.True(await call.ResponseStream.MoveNext(readDeadline.Token));
        var request = TaskRequest.Parser.ParseFrom(call.ResponseStream.Current.Payload);
        Assert.Equal(issuedBody.TaskId, request.TaskId);
        Assert.True(request.HasStagedBytes);
        Assert.Equal((ulong)content.Length, request.StagedBytes);
        Assert.EndsWith("sha256:" + expectedHash, request.Arguments);

        // The implant demands the payload and reassembles the chunk run the
        // server answers with -- 10 MiB at the 512 KiB chunk budget is twenty
        // chunks, terminal on the last.
        await call.RequestStream.WriteAsync(new Frame
        {
            Payload = ByteString.CopyFrom(new StagedPull { TaskId = request.TaskId }.ToByteArray()),
            Kind = FrameKind.StagedPull,
        });

        var payload = new List<byte>();
        StagedChunk chunk;
        do
        {
            Assert.True(await call.ResponseStream.MoveNext(readDeadline.Token));
            chunk = StagedChunk.Parser.ParseFrom(call.ResponseStream.Current.Payload);
            Assert.Equal(request.TaskId, chunk.TaskId);
            payload.AddRange(chunk.Data);
        } while (!chunk.Terminal);

        Assert.Equal(content.Length, payload.Count);
        Assert.Equal(content, payload.ToArray());

        // The implant reports the write; the task completes and the trail
        // carries the full arc plus the staged artifact's attach.
        await call.RequestStream.WriteAsync(ResultFrame(request.TaskId,
            $"wrote {content.Length} bytes to {targetPath}"));

        var audit = env.Host.Services.GetRequiredService<IAuditStore>();
        await WaitUntilAsync(async () =>
            (await audit.ForTaskAsync(Guid.Parse(request.TaskId))).Count == 4);

        var fetched = await env.Http.GetFromJsonAsync<TaskBody>(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}");
        Assert.NotNull(fetched);
        Assert.Equal("Completed", fetched!.Status);
        Assert.Equal("Succeeded", fetched.Outcome);
        Assert.Equal(4, fetched.Audit.Length);
        Assert.Contains(fetched.Audit, e => e.Kind == nameof(AuditEventKind.ArtifactAttached));

        var artifacts = await env.Http.GetFromJsonAsync<ArtifactListBody>(
            $"/engagements/{implant.EngagementId}/tasks/{request.TaskId}/artifacts");
        Assert.NotNull(artifacts);
        var staged = artifacts!.Items.Single(a => a.Name == "staged-" + Guid.Parse(request.TaskId).ToString("N"));
        Assert.Equal(content.Length, staged.Size);

        await call.RequestStream.CompleteAsync();
    }

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        var leafKey = RSA.Create(2048);
        var issued = await ca.IssueWithKeyAsync(
            new ImplantCertificateSubject(implant.Id, implant.EngagementId), leafKey, CancellationToken.None);
        return (implant, X509CertificateLoader.LoadCertificate(issued.Leaf), leafKey);
    }

    private static Frame HandshakeFrame(ImplantId implant)
    {
        var request = new HandshakeRequest
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplantId = implant.ToString(),
            Capabilities = { "file.push" },
        };
        return new Frame { Payload = ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static Frame ResultFrame(string taskId, string output)
        => new()
        {
            Payload = ByteString.CopyFrom(new TaskResult
            {
                TaskId = taskId,
                Outcome = TaskOutcome.Succeeded,
                Output = output,
            }.ToByteArray()),
            Kind = FrameKind.TaskResult,
        };

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    // Polls until condition is true or the timeout elapses; the audit append
    // runs on the stream thread, asynchronously to the HTTP readback.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
    }

    private sealed class TaskIssuedBody
    {
        public string TaskId { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private sealed class TaskBody
    {
        public string Status { get; set; } = "";
        public string? Outcome { get; set; }
        public AuditBody[] Audit { get; set; } = Array.Empty<AuditBody>();
    }

    private sealed class AuditBody
    {
        public string Kind { get; set; } = "";
    }

    private sealed class ArtifactListBody
    {
        public ArtifactBody[] Items { get; set; } = Array.Empty<ArtifactBody>();
    }

    private sealed class ArtifactBody
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Mirrors the task round-trip harness.
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
