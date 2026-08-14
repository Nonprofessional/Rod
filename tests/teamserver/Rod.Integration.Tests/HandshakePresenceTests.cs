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
/// Roadmap  acceptance: a connecting implant appears online in its
/// engagement. Drives the full slice end to end through a real Kestrel mTLS
/// endpoint -- the implant opens the gRPC beacon stream presenting its bound
/// client certificate, completes the handshake, and the operator sees it online
/// via the presence query (now backed by the session registry, ). Failure
/// paths assert each refusal maps to the right wire status.
///
/// This is the real mTLS handshake the rest of only inspected certificates
/// for: the client cert chains to the dev CA, and the server's identity check
/// binds it to the enrolled engagement (architecture.md Sec 9).
/// </summary>
public class HandshakePresenceTests
{
    [Fact]
    public async Task Handshake_MtlsImplant_AppearsOnlineInEngagement()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        // Enroll an implant directly through the core ports, then issue a leaf
        // certificate over a key pair we keep so we can present it in the mTLS
        // handshake (IssueAsync discards the key; IssueWithKeyAsync keeps it).
        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, 1, 0));

        // Receive the server's handshake response.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = ParseResponse(call.ResponseStream.Current);
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

        // Disconnecting closes the stream and the session is closed (offline).
        await call.RequestStream.CompleteAsync();
        await call.ResponseStream.MoveNext(CancellationToken.None); // server ends the stream
        await Task.Delay(50); // the session is closed in the finally on stream close
        Assert.Null(await sessions.GetActiveAsync(implant.Id));
    }

    [Fact]
    public async Task Handshake_RefusesVersionMismatch_OverMtls()
    {
        await using var env = await TestEnv.StartAsync();
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);

        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, major: 2, minor: 0));

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = ParseResponse(call.ResponseStream.Current);
        Assert.Equal(HandshakeStatus.VersionMismatch, response.Status);
    }

    [Fact]
    public async Task Handshake_RejectsUnknownClientCertificate_AtTls()
    {
        // A client cert that does NOT chain to the dev CA is refused at the TLS
        // layer, before any beacon handler runs -- this is the "unknown implant"
        // path for mTLS. The connection never completes the gRPC call.
        await using var env = await TestEnv.StartAsync();

        using var rogueKey = RSA.Create(2048);
        var rogue = BuildSelfSignedLeaf(rogueKey, "rogue-implant", "rogue-engagement");

        using var channel = env.ConnectBeacon(rogue, rogueKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();

        // The server refuses the TLS handshake (the cert does not chain to the
        // dev CA), so the connection is torn down before any beacon handler
        // runs. Where that surfaces in the client stack depends on the .NET
        // runtime patch and the exact frame the connection dies in: gRPC may
        // own the failure (RpcException) or the HTTP/2 layer may give up first
        // (HttpIOException). Both confirm the TLS refusal; accepting either
        // keeps the assertion honest across runtimes instead of pinning it to
        // whichever one the local machine happens to produce.
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await call.RequestStream.WriteAsync(HandshakeFrame(ImplantId.New(), 1, 0));
            await call.ResponseStream.MoveNext(CancellationToken.None);
        });
        Assert.NotNull(thrown);
        Assert.True(
            thrown is RpcException or HttpIOException,
            $"Expected the TLS refusal to surface as RpcException or HttpIOException, " +
            $"but got {thrown.GetType().FullName}: {thrown.Message}");
    }

    [Fact]
    public async Task Handshake_FileBackedCa_BindsEnrollmentToExternalCa()
    {
        // The production CA path (architecture.md Sec 9): when
        // Pki:CaCertificatePath and Pki:CaPrivateKeyPath are configured, the
        // teamserver signs implant leaves with that externally provisioned CA.
        // An implant enrolled through it completes the mTLS handshake -- its leaf
        // chains to the configured CA, which is what the server trusts -- so
        // enrollment binds to a non-dev CA chain.
        using var dir = new TempDir();
        var (caCert, caKey) = BuildExternalCa();
        WritePem(dir, "ca.crt", Pem("CERTIFICATE", caCert.Export(X509ContentType.Cert)));
        WritePem(dir, "ca.key", caKey.ExportRSAPrivateKeyPem());

        await using var env = await TestEnv.StartAsync(extendConfig: d =>
        {
            d["Pki:CaCertificatePath"] = Path.Combine(dir.Root, "ca.crt");
            d["Pki:CaPrivateKeyPath"] = Path.Combine(dir.Root, "ca.key");
        });

        // The config-driven swap registered the file-backed authority, not the dev CA.
        var ca = env.Host.Services.GetRequiredService<IImplantCertificateAuthority>();
        Assert.IsType<FileBackedCertificateAuthority>(ca);
        var sessions = env.Host.Services.GetRequiredService<ISessionRegistry>();
        var implants = env.Host.Services.GetRequiredService<IImplantRepository>();
        var clock = env.Host.Services.GetRequiredService<TimeProvider>();

        var (implant, leafCert, leafKey) = await EnrollImplantAsync(implants, ca, clock);
        using var channel = env.ConnectBeacon(leafCert, leafKey);
        var client = new Beacon.BeaconClient(channel);
        var call = client.CheckIn();
        await call.RequestStream.WriteAsync(HandshakeFrame(implant.Id, 1, 0));

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = ParseResponse(call.ResponseStream.Current);
        Assert.Equal(HandshakeStatus.Ok, response.Status);

        // The leaf chained to the external CA at TLS and the implant is now online.
        var online = await sessions.ListActiveAsync(implant.EngagementId);
        Assert.Single(online);
        Assert.Equal(implant.Id, online[0].ImplantId);

        await call.RequestStream.CompleteAsync();
        await call.ResponseStream.MoveNext(CancellationToken.None);
    }

    private static async Task<(Implant Implant, X509Certificate2 Leaf, RSA LeafKey)> EnrollImplantAsync(
        IImplantRepository implants, IImplantCertificateAuthority ca, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var implant = Implant.Enroll(
            ImplantId.New(), EngagementId.New(), "key-abc",
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
            Capabilities = { "shell.exec", "file.push" },
        };
        return new Frame { Payload = Google.Protobuf.ByteString.CopyFrom(request.ToByteArray()) };
    }

    private static HandshakeResponse ParseResponse(Frame frame)
        => HandshakeResponse.Parser.ParseFrom(frame.Payload);

    /// <summary>
    /// A real Kestrel teamserver with the mTLS implant endpoint bound, plus a
    /// plain-HTTP operator API. Disposed to tear the listener down.
    /// </summary>
    private sealed class TestEnv : IAsyncDisposable
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public int MtlsPort { get; private set; }
        public int HttpPort { get; private set; }

        public static async Task<TestEnv> StartAsync(Action<Dictionary<string, string?>>? extendConfig = null)
        {
            var env = new TestEnv();
            env.MtlsPort = GetFreeTcpPort();
            env.HttpPort = GetFreeTcpPort();

            var config = AuthenticatedHost.BuildConfig(extendConfig);
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

        // Connects a gRPC channel that performs the client side of mTLS: presents
        // the implant leaf (with its private key) and trusts the dev CA as the
        // server identity. The CA is resolved from the same teamserver the channel
        // connects to. The channel owns its handler and disposes it.
        public GrpcChannel ConnectBeacon(X509Certificate2 leaf, RSA leafKey)
        {
            // Some leaves already carry their private key (e.g. a self-signed test
            // cert); others are DER-only and need the key attached for the TLS
            // handshake to prove possession.
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

    // A self-signed leaf that does NOT chain to the dev CA, for the TLS-rejection
    // path. Mimics the implant leaf shape (CN + Rod engagement extension) but is
    // its own issuer, so the server's ClientCertificateValidation refuses it.
    private static X509Certificate2 BuildSelfSignedLeaf(RSA key, string implantId, string engagementId)
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(1);
        var request = new CertificateRequest(
            $"CN={implantId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2", "Client Authentication") }, critical: true));
        request.CertificateExtensions.Add(RodImplantEngagementExtension.Build(engagementId));
        return request.CreateSelfSigned(notBefore, notAfter);
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
}
