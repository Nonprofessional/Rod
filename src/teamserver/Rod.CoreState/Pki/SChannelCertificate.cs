using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rod.CoreState.Pki;

/// <summary>
/// Re-imports an in-memory certificate/key pair through PFX so SChannel can
/// use it as a TLS credential. Windows cannot present a certificate whose
/// private key exists only as an ephemeral in-memory handle:
/// <c>AcquireCredentialsHandle</c> fails with <c>SEC_E_NO_CREDENTIALS</c>
/// ("the platform does not support ephemeral keys") and the listener kills
/// every handshake before its first byte goes out -- the failure mode the
/// Windows CI lane exists to catch. The PFX import with a persisted key set
/// leaves the pair in the store-shaped form every TLS stack accepts; Linux's
/// OpenSSL sees no difference, so the round trip runs only on Windows.
/// </summary>
internal static class SChannelCertificate
{
    /// <summary>
    /// Pairs <paramref name="certificate"/> with <paramref name="privateKey"/>
    /// in a form the platform TLS stack can present, re-imported through PFX
    /// with a persisted key set on Windows. The caller owns the result.
    /// </summary>
    public static X509Certificate2 WithUsableKey(X509Certificate2 certificate, RSA privateKey)
    {
        var paired = certificate.CopyWithPrivateKey(privateKey);
        if (!OperatingSystem.IsWindows())
            return paired;

        using (paired)
        {
            return X509CertificateLoader.LoadPkcs12(
                paired.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
        }
    }
}
