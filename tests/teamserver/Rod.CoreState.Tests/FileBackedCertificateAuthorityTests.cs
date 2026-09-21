using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Pki;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the production engagement CA
/// (<see cref="FileBackedCertificateAuthority"/>, architecture.md Sec 9): it
/// consumes an externally provisioned CA (PEM cert + RSA key on disk), serves
/// the tasking signer and the server-leaf issuer off it, and fails fast on
/// malformed provisioning. These tests generate a throwaway CA in-process,
/// drop it to PEM files, and drive the authority's ports.
/// </summary>
public class FileBackedCertificateAuthorityTests
{
    [Fact]
    public void GetCaCertificate_ReturnsTheLoadedCa()
    {
        using var dir = TempDir.Create();
        var (ca, caKey) = BuildCa();
        var authority = new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                WritePemCert(dir, "ca.crt", ca), WritePemKey(dir, "ca.key", caKey), CaPrivateKeyPassphrase: null));

        Assert.Equal(ca.Thumbprint, authority.GetCaCertificate().Thumbprint);
    }

    [Fact]
    public void EncryptedPrivateKey_LoadsAndSignsTasking()
    {
        // An encrypted PKCS#8 key round-trips through the passphrase path:
        // the signer works off it, which is the authority's live obligation.
        using var dir = TempDir.Create();
        var (ca, caKey) = BuildCa();
        var authority = new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                WritePemCert(dir, "ca.crt", ca),
                WriteEncryptedPemKey(dir, "ca.key", caKey, "secret-passphrase"),
                "secret-passphrase"));

        var signature = authority.SignTasking("implant", "task", "shell.exec", "id");
        using var rsa = caKey;
        Assert.True(rsa.VerifyData(
            TaskingCanonical.Bytes("implant", "task", "shell.exec", "id"),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
    }

    [Fact]
    public void SignTasking_VerifiesUnderTheLoadedCa()
    {
        using var dir = TempDir.Create();
        var (ca, caKey) = BuildCa();
        var authority = new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                WritePemCert(dir, "ca.crt", ca), WritePemKey(dir, "ca.key", caKey), CaPrivateKeyPassphrase: null));

        var signature = authority.SignTasking("implant", "task", "shell.exec", "id");
        using var publicKey = ca.GetRSAPublicKey()!;
        Assert.True(publicKey.VerifyData(
            TaskingCanonical.Bytes("implant", "task", "shell.exec", "id"),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
    }

    [Fact]
    public void GetServerCertificate_ChainsToTheLoadedCa()
    {
        using var dir = TempDir.Create();
        var (ca, caKey) = BuildCa();
        var authority = new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                WritePemCert(dir, "ca.crt", ca), WritePemKey(dir, "ca.key", caKey), CaPrivateKeyPassphrase: null));

        using var server = authority.GetServerCertificate();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.Add(ca);
        Assert.True(chain.Build(server));
        Assert.Equal(ca.Thumbprint, chain.ChainElements[^1].Certificate.Thumbprint);
    }

    [Fact]
    public void Constructor_ThrowsWhenPrivateKeyMismatchesCertificate()
    {
        using var dir = TempDir.Create();
        var (ca, _) = BuildCa();
        using var otherKey = RSA.Create(2048);

        Assert.Throws<InvalidOperationException>(() => new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                WritePemCert(dir, "ca.crt", ca), WritePemKey(dir, "ca.key", otherKey), CaPrivateKeyPassphrase: null)));
    }

    [Fact]
    public void Constructor_ThrowsWhenCertificateFileIsMissing()
    {
        // A missing cert file surfaces at construction (fail fast), not deferred
        // to the first enrollment. The exact exception is runtime-dependent
        // (CryptographicException when no PEM is found), so assert loosely.
        using var dir = TempDir.Create();
        var (_, caKey) = BuildCa();

        Assert.ThrowsAny<Exception>(() => new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(
                Path.Combine(dir.Root, "absent.crt"), WritePemKey(dir, "ca.key", caKey), CaPrivateKeyPassphrase: null)));
    }

    [Fact]
    public void Constructor_ThrowsWhenOnlyOnePathIsSupplied()
    {
        using var dir = TempDir.Create();
        var (ca, caKey) = BuildCa();
        var certPath = WritePemCert(dir, "ca.crt", ca);
        var keyPath = WritePemKey(dir, "ca.key", caKey);

        // Cert without key.
        Assert.Throws<ArgumentException>(() => new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(certPath, CaPrivateKeyPath: "", CaPrivateKeyPassphrase: null)));
        // Key without cert.
        Assert.Throws<ArgumentException>(() => new FileBackedCertificateAuthority(
            new FileBackedCertificateAuthorityOptions(CaCertificatePath: "", keyPath, CaPrivateKeyPassphrase: null)));
    }

    // Builds a throwaway self-signed CA root and its key, mirroring the dev CA's
    // root construction. The cert and key are written to PEM separately below so
    // the authority under test recombines them from disk.
    private static (X509Certificate2 Ca, RSA Key) BuildCa()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Rod Test CA,O=Rod,C=ZZ", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        // A real externally provisioned CA is long-lived so the 30-day leaves always
        // fit inside it; mirror that here (a 30-day CA would let a leaf issued a
        // moment later outlive the issuer and fail CertificateRequest.Create).
        var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
        return (ca, key);
    }

    private static string WritePemCert(TempDir dir, string name, X509Certificate2 ca)
    {
        File.WriteAllText(Path.Combine(dir.Root, name), Pem("CERTIFICATE", ca.Export(X509ContentType.Cert)));
        return Path.Combine(dir.Root, name);
    }

    private static string WritePemKey(TempDir dir, string name, RSA key)
    {
        File.WriteAllText(Path.Combine(dir.Root, name), key.ExportRSAPrivateKeyPem());
        return Path.Combine(dir.Root, name);
    }

    private static string WriteEncryptedPemKey(TempDir dir, string name, RSA key, string passphrase)
    {
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterationCount: 100_000);
        File.WriteAllText(Path.Combine(dir.Root, name), key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe));
        return Path.Combine(dir.Root, name);
    }

    private static string Pem(string type, byte[] der)
        => $"-----BEGIN {type}-----\n"
           + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)
           + $"\n-----END {type}-----\n";

    // A self-cleaning temp directory for the PEM files. Defensive dispose: a
    // stray file lock on Windows must not fail the test run.
    private sealed class TempDir : IDisposable
    {
        public string Root { get; }
        private TempDir(string root) => Root = root;

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Rod.Pki.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
