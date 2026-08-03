using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Pki;
using Rod.Transport;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Roadmap M1.2 acceptance: enroll a fake implant and receive a certificate
/// bound to <c>(implant_id, engagement_id)</c> plus the CA chain -- end to end
/// through the in-memory TestServer. This drives the full enrollment slice
/// (stager redeem, implant creation, CA issue) via the implant-side endpoint and
/// verifies the issued binding by inspecting the certificate (no real mTLS
/// handshake; that is M1.3). Failure paths assert each redeem outcome maps to
/// the right wire <see cref="EnrollStatus"/>.
/// </summary>
public class EnrollmentTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        IHost host = TransportHost.CreateHostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder.UseTestServer())
            .Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        var client = server.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return (client, host);
    }

    private static async Task<string> MintTokenForNewEngagementAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync("/engagements", new EngagementEndpoints.CreateEngagementRequest(
            OwnerId: Guid.NewGuid(),
            OwnerHandle: "cneale",
            OwnerDisplayName: "Cecil Neale",
            Name: "Operation Smokeshow"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        Assert.NotNull(created);

        var mintResponse = await client.PostAsync($"/engagements/{created!.EngagementId}/stager-tokens", content: null);
        mintResponse.EnsureSuccessStatusCode();
        var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();
        Assert.NotNull(token);
        return token!.Secret;
    }

    [Fact]
    public async Task Enroll_IssuesCertificateBoundToImplantAndEngagement_WithCaChain()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var secret = await MintTokenForNewEngagementAsync(client);

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.Equal(EnrollStatus.Ok, enrolled!.Status);

            // The engagement was resolved from the token; the implant id is new.
            Assert.False(string.IsNullOrWhiteSpace(enrolled.ImplantId));
            Assert.False(string.IsNullOrWhiteSpace(enrolled.EngagementId));

            // Cert material is present.
            Assert.False(string.IsNullOrWhiteSpace(enrolled.LeafCertificate));
            Assert.NotNull(enrolled.CaChain);
            Assert.True(enrolled.CaChain!.Length >= 1);

            using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(enrolled.LeafCertificate!));
            using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(enrolled.CaChain[0]));

            // Binding: the leaf's common name is the implant id, and the Rod
            // engagement-id extension carries the engagement id.
            Assert.Equal($"CN={enrolled.ImplantId}", leaf.Subject);
            Assert.True(RodImplantEngagementExtension.TryRead(leaf, out var engagementFromCert),
                "Leaf certificate must carry the Rod engagement-id extension.");
            Assert.Equal(enrolled.EngagementId, engagementFromCert);

            // Chain: the leaf is issued by the dev CA, and the only chain-status is
            // UntrustedRoot (the dev root is not in a system trust store). Any other
            // status -- a bad signature, a name mismatch, a broken link -- would
            // fail this. This is the precise "chains to the CA root" claim for a
            // self-signed dev CA.
            Assert.Equal(root.Subject, leaf.Issuer);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(root);
            var chainOk = chain.Build(leaf);
            Assert.True(chainOk,
                "Leaf must chain to the CA root: " +
                string.Join(", ", chain.ChainStatus.Select(s => $"{s.Status}({s.StatusInformation.Trim()})")));
        }
    }

    [Fact]
    public async Task Enroll_ReturnsBadToken_ForUnknownSecret()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: "totally-bogus-secret", Class: null));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.Equal(EnrollStatus.BadToken, body!.Status);
        }
    }

    [Fact]
    public async Task Enroll_ReturnsSpent_WhenTokenAlreadyConsumed()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var secret = await MintTokenForNewEngagementAsync(client);

            // First enroll consumes the single-use token.
            var first = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            // Second enroll with the same secret: the token is spent (the store
            // removed it), so the lookup finds nothing -> BadToken.
            var second = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));

            Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
            var body = await second.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.Equal(EnrollStatus.BadToken, body!.Status);
        }
    }

    [Fact]
    public async Task Enroll_WithClientPublicKey_SignsLeafOverImplantKey()
    {
        // The mTLS-capable enroll path (architecture.md Sec 9): the implant sends
        // only its public key and the CA signs a leaf over it, so the implant keeps
        // its private key and can present the leaf in a handshake. The issued leaf's
        // public key must equal the key the implant retained -- proof the server
        // never had to see the private half.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var secret = await MintTokenForNewEngagementAsync(client);

            using var implantKey = RSA.Create(2048);
            var publicKeyDer = implantKey.ExportSubjectPublicKeyInfo();
            var publicKeyB64 = Convert.ToBase64String(publicKeyDer);

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: secret,
                    Class: null,
                    PublicKey: publicKeyB64));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.Equal(EnrollStatus.Ok, enrolled!.Status);

            using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(enrolled.LeafCertificate!));

            // The leaf's public key is the implant's -- the server signed over the
            // public half the implant supplied and never saw the private key.
            using var leafRsa = leaf.GetRSAPublicKey()!;
            var leafPublicKey = leafRsa.ExportSubjectPublicKeyInfo();
            Assert.Equal(publicKeyDer, leafPublicKey);

            // The binding is intact regardless of which key path was taken.
            Assert.Equal($"CN={enrolled.ImplantId}", leaf.Subject);
            Assert.True(RodImplantEngagementExtension.TryRead(leaf, out var engagementFromCert));
            Assert.Equal(enrolled.EngagementId, engagementFromCert);
        }
    }

    [Fact]
    public async Task Enroll_WithMalformedPublicKey_ReturnsBadRequest()
    {
        // A public key that is not a recognizable SubjectPublicKeyInfo is a bad
        // request, not a token failure: the token stays intact.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            var secret = await MintTokenForNewEngagementAsync(client);

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: secret,
                    Class: null,
                    PublicKey: Convert.ToBase64String("not-a-real-public-key"u8.ToArray())));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
