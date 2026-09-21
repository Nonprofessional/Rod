using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Pki;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the server-leaf renewal rule both certificate
/// authorities apply (<see cref="ServerLeafRotation"/>, architecture.md
/// Sec 9): a cached leaf is re-minted once it enters the renewal window
/// before its NotAfter, so a teamserver whose engagement outlasts the
/// leaf's 30-day lifetime never presents an expired certificate to the
/// implant clients that pin the CA.
/// </summary>
public class ServerLeafRotationTests
{
    [Fact]
    public void Due_ALeafInsideTheRenewalWindowIsReMinted()
    {
        // Hours left on a one-day window: the leaf is due for replacement.
        using var leaf = SelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2));
        Assert.True(ServerLeafRotation.Due(leaf, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Due_AFreshLeafIsNot()
    {
        // The leaf the authorities mint: a full lifetime ahead of it.
        using var leaf = SelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        Assert.False(ServerLeafRotation.Due(leaf, DateTimeOffset.UtcNow));
    }

    // The key material is irrelevant to the rule; a throwaway self-signed
    // pair just carries the validity dates under test.
    private static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Rod Rotation Test,O=Rod,C=ZZ", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
