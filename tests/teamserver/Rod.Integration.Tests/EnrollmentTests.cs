using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.CoreState.Pki;
using Rod.Transport.Endpoints;
using Rod.V1;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance: enroll a fake implant and receive a certificate
/// bound to <c>(implant_id, engagement_id)</c> plus the CA chain -- end to end
/// through the in-memory TestServer. This drives the full enrollment slice
/// (stager redeem, implant creation, CA issue) via the implant-side endpoint and
/// verifies the issued binding by inspecting the certificate (no real mTLS
/// handshake; the listener tests cover that). Failure paths assert each redeem outcome maps to
/// the right wire <see cref="EnrollStatus"/>. The implant enrollment endpoint is
/// anonymous (implants authenticate with the stager token, not a cookie); the
/// operator routes that mint a stager token require the operator session.
/// </summary>
public class EnrollmentTests
{
    private static (HttpClient Client, IHost Host) CreateClient()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        return (client, host);
    }

    private static async Task<string> MintEngagementIdAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        return created!.EngagementId;
    }

    private static async Task<string> MintTokenForNewEngagementAsync(HttpClient client)
    {
        var createResponse = await client.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Smokeshow"));
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
    public async Task Enroll_AesGcmEnvelope_DecodesUnderTheRecordedKey()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = Guid.Parse(await MintEngagementIdAsync(client));
            var payloads = host.Services.GetRequiredService<Rod.Audit.IPayloadStore>();

            // The per-artifact key pair, minted as the build would mint it and
            // recorded beside a stored payload, as the build would record it.
            var (keyId, key) = Rod.Transport.Payloads.AesGcmEnvelope.Mint();
            await payloads.SaveAsync(new Rod.Audit.PayloadRecord(
                Guid.NewGuid(), engagementId, "Stage2", "DotNet", "application/octet-stream",
                new string('a', 64), Array.Empty<byte>(), 0, DateTimeOffset.UtcNow,
                EnvelopeKeyId: keyId, EnvelopeKey: key));

            // A body encrypted under the key decodes: the garbage token inside
            // is what answers (401), proving the envelope -- not the decode --
            // was the failure point.
            var json = JsonSerializer.Serialize(
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: "not-a-token", Class: null));
            var wrapped = Rod.Transport.Payloads.AesGcmEnvelope.Wrap(
                System.Text.Encoding.UTF8.GetBytes(json), keyId, key, Rod.Transport.Payloads.AesGcmEnvelope.Aad);
            var encrypted = await client.PostAsync("/implants/enroll",
                new StringContent($"\"{wrapped}\"", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, encrypted.StatusCode);

            // A tampered body fails authentication (GCM), a foreign key id is
            // unknown, and both read as a bad request -- nothing about the
            // envelope's contents leaks.
            var tamperedChars = wrapped.ToCharArray();
            tamperedChars[^2] = tamperedChars[^2] == 'A' ? 'B' : 'A';
            var tampered = await client.PostAsync("/implants/enroll",
                new StringContent($"\"{new string(tamperedChars)}\"", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

            var (otherId, otherKey) = Rod.Transport.Payloads.AesGcmEnvelope.Mint();
            var foreign = Rod.Transport.Payloads.AesGcmEnvelope.Wrap(
                System.Text.Encoding.UTF8.GetBytes(json), otherId, otherKey, Rod.Transport.Payloads.AesGcmEnvelope.Aad);
            var unknownKey = await client.PostAsync("/implants/enroll",
                new StringContent($"\"{foreign}\"", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, unknownKey.StatusCode);
        }
    }

    [Fact]
    public async Task Enroll_AnswersWithTheCaChain_TheTaskingSigner()
    {
        // The enrollment's certificate answer is the CA chain alone: it
        // carries the tasking signer every implant verifies its dispatched
        // work under, and no transport leaf is minted (the certificate
        // posture retired with the mTLS family -- the per-artifact key is
        // the identity).
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var secret = await MintTokenForNewEngagementAsync(client);
            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.Equal(EnrollStatus.Ok, enrolled!.Status);

            Assert.False(string.IsNullOrWhiteSpace(enrolled.ImplantId));
            Assert.False(string.IsNullOrWhiteSpace(enrolled.EngagementId));

            // The chain is the dev CA root, and it verifies tasking: what
            // the implant pins from this answer is exactly the signer.
            Assert.NotNull(enrolled.CaChain);
            Assert.Single(enrolled.CaChain!);
            using var root = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(enrolled.CaChain[0]));
            var ca = host.Services.GetRequiredService<Rod.CoreState.Pki.IImplantCertificateAuthority>();
            Assert.Equal(ca.GetCaCertificate().Thumbprint, root.Thumbprint);
            var signature = ca.SignTasking(enrolled.ImplantId, "t1", "shell.exec", "id");
            using var rsa = root.GetRSAPublicKey()!;
            Assert.True(rsa.VerifyData(
                Rod.CoreState.Pki.TaskingCanonical.Bytes(enrolled.ImplantId, "t1", "shell.exec", "id"),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss));
        }
    }

    [Fact]
    public async Task Enroll_ReturnsBadToken_ForUnknownSecret()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            // Enrollment is implant-facing and anonymous; no operator session is
            // involved, and the bogus token never resolves to an engagement.
            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: "totally-bogus-secret", Class: null));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.Equal(EnrollStatus.BadToken, body!.Status);
        }
    }

    [Fact]
    public async Task Enroll_RecordsReportedHostIdentity_OnTheImplantList()
    {
        // The device dimension of the fleet: an implant reports the machine it
        // runs on at enroll, the teamserver records it, and the operator implant
        // list reads it back. A client that reports nothing (a pre-field one)
        // enrolls the same way and the fields read back null.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await MintEngagementIdAsync(client);

            var mintResponse = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
            mintResponse.EnsureSuccessStatusCode();
            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: token!.Secret,
                    Class: null,
                    Hostname: "  web01.example.test  ",
                    Os: "Linux 6.12",
                    Arch: "x64",
                    Username: "svc-app"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var listResponse = await client.GetAsync($"/engagements/{engagementId}/implants");
            listResponse.EnsureSuccessStatusCode();
            var implants = await listResponse.Content
                .ReadFromJsonAsync<ImplantEndpoints.ImplantResponse[]>();
            Assert.NotNull(implants);
            var implant = Assert.Single(implants!);

            // Reported facts round-trip; the whitespace-padded hostname was
            // trimmed on the way in.
            Assert.Equal("web01.example.test", implant!.Hostname);
            Assert.Equal("Linux 6.12", implant.Os);
            Assert.Equal("x64", implant.Arch);
            Assert.Equal("svc-app", implant.Username);
        }
    }

    [Fact]
    public async Task Enroll_WithoutHostIdentity_LeavesTheFieldsNull()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var engagementId = await MintEngagementIdAsync(client);

            var mintResponse = await client.PostAsync($"/engagements/{engagementId}/stager-tokens", content: null);
            mintResponse.EnsureSuccessStatusCode();
            var token = await mintResponse.Content.ReadFromJsonAsync<EngagementEndpoints.StagerTokenResponse>();

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: token!.Secret, Class: null));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var listResponse = await client.GetAsync($"/engagements/{engagementId}/implants");
            listResponse.EnsureSuccessStatusCode();
            var implants = await listResponse.Content
                .ReadFromJsonAsync<ImplantEndpoints.ImplantResponse[]>();
            Assert.NotNull(implants);
            var implant = Assert.Single(implants!);

            Assert.Null(implant!.Hostname);
            Assert.Null(implant.Os);
            Assert.Null(implant.Arch);
            Assert.Null(implant.Username);
        }
    }

    [Fact]
    public async Task Enroll_ReturnsSpent_WhenTokenAlreadyConsumed()
    {
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
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
    public async Task Enroll_WithClientPublicKey_AcceptsTheKey_NoLeafIssued()
    {
        // The implant's key pair is a Tier 0 obligation: it sends only the
        // public half with its enroll request. The server accepts and
        // validates it, but mints nothing over it in-tree -- the transport
        // certificate posture retired with the mTLS family, and the answer's
        // leaf field carries the not-supplied shape.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var secret = await MintTokenForNewEngagementAsync(client);

            using var implantKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
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
            Assert.True(string.IsNullOrEmpty(enrolled.LeafCertificate));
            Assert.Single(enrolled.CaChain!);
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
            await AuthenticatedHost.LoginAsync(client);
            var secret = await MintTokenForNewEngagementAsync(client);

            var response = await client.PostAsJsonAsync("/implants/enroll",
                new EnrollmentEndpoints.EnrollRequest(
                    StagerTokenSecret: secret,
                    Class: null,
                    PublicKey: Convert.ToBase64String("not-a-real-public-key"u8.ToArray())));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Enroll_AcceptsBase64EnvelopeBody()
    {
        // The malleable profile's base64 envelope (architecture.md Sec 7) wraps
        // the JSON body as a single base64 string so the request stops looking
        // like a structured C2 message. The teamserver decodes it before
        // binding, so the envelope changes the wire shape, not the contract:
        // the enroll succeeds exactly like the raw-JSON shape.
        var (client, host) = CreateClient();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);
            var secret = await MintTokenForNewEngagementAsync(client);

            var json = JsonSerializer.Serialize(
                new EnrollmentEndpoints.EnrollRequest(StagerTokenSecret: secret, Class: null));
            var wrapped = "\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\"";
            var response = await client.PostAsync("/implants/enroll",
                new StringContent(wrapped, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var enrolled = await response.Content.ReadFromJsonAsync<EnrollmentEndpoints.EnrollmentResponse>();
            Assert.NotNull(enrolled);
            Assert.Equal(EnrollStatus.Ok, enrolled!.Status);
            Assert.False(string.IsNullOrWhiteSpace(enrolled.ImplantId));
        }
    }
}
