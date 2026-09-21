using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState;
using Rod.CoreState.Application;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Pki;
using Rod.CoreState.Sessions;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: a connecting implant appears online in its
/// engagement. Drives the full slice end to end through a real Kestrel
/// endpoint -- the implant opens the WebSocket beacon stream, completes the
/// handshake, and the operator sees it online via the presence query (now
/// backed by the session registry). Failure paths assert each refusal maps
/// to the right wire status, and the file-backed CA path proves enrollment
/// binds to the externally provisioned CA through the tasking signature it
/// issues.
/// </summary>
public class HandshakePresenceTests
{
    [Fact]
    public async Task Handshake_Implant_AppearsOnlineInEngagement()
    {
        await using var env = await TestEnv.StartAsync();
        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var implant = await EnrollImplantAsync(implants, clock);

        using var beacon = await WsBeaconClient.ConnectAsync(
            env.HttpPort, implant.Id.ToString(), new[] { "shell.exec", "file.push" });

        // Receive the server's handshake response.
        var response = await beacon.ReceiveHandshakeAsync();
        Assert.Equal(HandshakeStatus.Ok, response.Status);
        Assert.Equal(ProtocolVersions.Major, response.Version.Major);
        Assert.Equal(implant.EngagementId.ToString(), response.EngagementId);

        // The acceptance point: the implant now has an active session in its
        // engagement (it is online).
        var online = await sessions.ListActiveAsync(implant.EngagementId);
        var session = Assert.Single(online);
        Assert.Equal(implant.Id, session.ImplantId);
        Assert.Equal(new[] { "shell.exec", "file.push" }, session.Capabilities);

        // And visible through the operator query, scoped to its engagement.
        var operatorView = await env.Http.GetFromJsonAsync<PresenceEndpoints.PresenceRecordResponse[]>(
            $"/engagements/{implant.EngagementId}/presence");
        Assert.NotNull(operatorView);
        Assert.Single(operatorView!, r => r.ImplantId == implant.Id.ToString());

        // Ending the stream does NOT end the session: a session is the
        // implant's live channel, not one TCP connection -- a poll-mode implant
        // ends every contact stream and reconnects seconds later, so liveness
        // is last-seen based and the staleness sweeper is the close path.
        beacon.Dispose();
        await Task.Delay(50);
        Assert.NotNull(await sessions.GetActiveAsync(implant.Id));

        // The sweep (here driven directly, as the hosted sweeper would) is what
        // takes the implant off the roster: close the silent session, and the
        // implant reads offline.
        await sessions.CloseAsync(session.Id, clock.GetUtcNow());
        Assert.Null(await sessions.GetActiveAsync(implant.Id));
    }

    [Fact]
    public async Task Handshake_RefusesVersionMismatch()
    {
        await using var env = await TestEnv.StartAsync();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var implant = await EnrollImplantAsync(implants, clock);

        using var beacon = await WsBeaconClient.ConnectAsync(
            env.HttpPort, implant.Id.ToString(), new[] { "shell.exec", "file.push" },
            handshakeVersion: (2, 0));

        var response = await beacon.ReceiveHandshakeAsync();
        Assert.Equal(HandshakeStatus.VersionMismatch, response.Status);
    }

    [Fact]
    public async Task Handshake_FileBackedCa_BindsEnrollmentToExternalCa()
    {
        // The production CA path (architecture.md Sec 9): when
        // Pki:CaCertificatePath and Pki:CaPrivateKeyPath are configured, the
        // teamserver signs its tasking with that externally provisioned CA.
        // The proof on the web posture is the signature itself: a task
        // dispatched to the enrolled implant verifies under the external CA's
        // public key, so enrollment bound the engagement to the configured CA
        // chain -- no dev CA key was involved anywhere.
        using var dir = new TempDir();
        var (caCert, caKey) = BuildExternalCa();
        WritePem(dir, "ca.crt", Pem("CERTIFICATE", caCert.Export(X509ContentType.Cert)));
        WritePem(dir, "ca.key", caKey.ExportRSAPrivateKeyPem());

        await using var env = await TestEnv.StartAsync(extendConfig: d =>
        {
            d["Pki:CaCertificatePath"] = Path.Combine(dir.Root, "ca.crt");
            d["Pki:CaPrivateKeyPath"] = Path.Combine(dir.Root, "ca.key");
        });

        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var implant = await EnrollImplantAsync(implants, clock);
        using var beacon = await WsBeaconClient.ConnectAsync(env.HttpPort, implant.Id.ToString());
        var response = await beacon.ReceiveHandshakeAsync();
        Assert.Equal(HandshakeStatus.Ok, response.Status);

        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{implant.EngagementId}/tasks",
            new { ImplantId = implant.Id.ToString(), Verb = "shell.exec", Arguments = "id" });
        issued.EnsureSuccessStatusCode();

        // The dispatched tasking carries the external CA's signature over the
        // canonical tuple -- the chain the enrollment bound, proven on the
        // surviving surface.
        var task = TaskRequest.Parser.ParseFrom(await beacon.ReceiveSingleFrameAsync());
        using var rsa = caCert.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(
            Canonical(implant.Id.ToString(), task.TaskId, task.Verb, task.Arguments),
            task.Signature.Span,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
        var online = await sessions.ListActiveAsync(implant.EngagementId);
        Assert.Single(online);
        Assert.Equal(implant.Id, online[0].ImplantId);
    }

    private static async Task<Implant> EnrollImplantAsync(
        IImplantRepository implants, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(),
            now.AddDays(30), ImplantClass.Stage2, now);
        await implants.SaveAsync(implant);

        return implant;
    }

    // The canonical signed encoding from rod.proto: for each field, the
    // little-endian uint32 length of its UTF-8 bytes followed by the bytes.
    private static byte[] Canonical(params string[] fields)
    {
        using var buffer = new MemoryStream();
        foreach (var field in fields)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(field);
            var length = (uint)bytes.Length;
            buffer.WriteByte((byte)length);
            buffer.WriteByte((byte)(length >> 8));
            buffer.WriteByte((byte)(length >> 16));
            buffer.WriteByte((byte)(length >> 24));
            buffer.Write(bytes);
        }
        return buffer.ToArray();
    }

    // A self-signed CA root for the file-backed-authority path, written to PEM so
    // FileBackedCertificateAuthority can load it. Production supplies the
    // equivalent externally; here it is generated in-process.
    private static (X509Certificate2 Ca, RSA Key) BuildExternalCa()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Rod Test External CA,O=Rod,C=ZZ", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        // Long-lived so the 30-day implant leaves always fit inside it; a real
        // externally provisioned engagement CA behaves the same way.
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365)), key);
    }

    private static string Pem(string type, byte[] der)
        => $"-----BEGIN {type}-----\n"
           + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)
           + $"\n-----END {type}-----\n";

    private static void WritePem(TempDir dir, string name, string pem)
        => File.WriteAllText(Path.Combine(dir.Root, name), pem);

    // A self-cleaning temp directory for the file-backed-CA PEM files.
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Rod.Integration.Tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>
    /// A real Kestrel teamserver with the plain-HTTP operator API and the
    /// WebSocket beacon riding the same listener family. Disposed to tear
    /// the listener down.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync(Action<Dictionary<string, string?>>? extendConfig = null)
        {
            var env = new TestEnv();
            env.HttpPort = TestSupport.GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig(extendConfig);
            env.Host = TransportHost.CreateHostBuilder(
                    configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                    mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                    configuration: config)
                .ConfigureWebHost(webBuilder => webBuilder
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

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Host is not null)
                await Host.StopAsync();
            Host?.Dispose();
        }
    }
}
