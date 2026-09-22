using System.Net;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Pki;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the development engagement CA
/// (<see cref="DevCertificateAuthority"/>, architecture.md Sec 9): the
/// throwaway root the dev runs and the test suite ride, exercised on the
/// obligation the https fronts depend on -- a server leaf whose SAN names
/// the host the client dialed.
/// </summary>
public class DevCertificateAuthorityTests
{
    [Fact]
    public void GetServerCertificate_NamesTheDialedHostInTheSan()
    {
        // The reference implant's rustls client pins the CA but runs full
        // webpki validation, server-name matching included: the leaf must
        // carry the dialed name as a SAN (IP literal as an IP SAN,
        // hostname as a DNS SAN), or every https front is unusable by the
        // one client that matters.
        var authority = new DevCertificateAuthority();

        using var byAddress = authority.GetServerCertificate("127.0.0.1");
        var addressSan = Assert.IsType<X509SubjectAlternativeNameExtension>(
            Assert.Single(byAddress.Extensions.OfType<X509SubjectAlternativeNameExtension>()));
        Assert.Equal(IPAddress.Parse("127.0.0.1"), Assert.Single(addressSan.EnumerateIPAddresses()));

        using var byName = authority.GetServerCertificate("stage.example.test");
        var nameSan = Assert.IsType<X509SubjectAlternativeNameExtension>(
            Assert.Single(byName.Extensions.OfType<X509SubjectAlternativeNameExtension>()));
        Assert.Equal("stage.example.test", Assert.Single(nameSan.EnumerateDnsNames()));
    }

    [Fact]
    public void GetServerCertificate_CachesPerHost()
    {
        // One leaf per distinct dialed name, reused between renewals: the
        // selector asks per handshake, and the cache is bounded by the
        // front count. Not disposed: the authority owns the cached leaves.
        var authority = new DevCertificateAuthority();

        var first = authority.GetServerCertificate("stage.example.test");
        Assert.Same(first, authority.GetServerCertificate("stage.example.test"));
        Assert.NotSame(first, authority.GetServerCertificate("alt.example.test"));
    }
}
