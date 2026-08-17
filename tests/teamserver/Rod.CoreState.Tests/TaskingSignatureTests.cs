using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rod.CoreState.Pki;

namespace Rod.CoreState.Tests;

/// <summary>
/// Direct checks of the tasking signature (architecture.md Sec 9 -- command
/// signing): the CA signs the canonical
/// <c>(implant_id, task_id, verb, arguments)</c> encoding with
/// RSASSA-PSS/SHA-256, and the signature verifies against the CA certificate
/// an implant already holds. These tests drive the dev authority through
/// <see cref="IImplantCertificateAuthority.SignTasking"/> -- the same port the
/// beacon endpoint signs dispatched tasks with -- and verify like an implant
/// would, against <see cref="DevCertificateAuthority.GetCaCertificate"/> and
/// the canonical bytes from <see cref="TaskingCanonical"/>.
/// </summary>
public class TaskingSignatureTests
{
    private const string ImplantId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void SignedTasking_VerifiesAgainstCaCertificate()
    {
        var authority = new DevCertificateAuthority();

        var signature = authority.SignTasking(ImplantId, "task-1", "shell.exec", "whoami");

        using var ca = authority.GetCaCertificate();
        using var publicKey = ca.GetRSAPublicKey()!;
        Assert.True(publicKey.VerifyData(
            TaskingCanonical.Bytes(ImplantId, "task-1", "shell.exec", "whoami"),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
    }

    [Theory]
    [InlineData("ffffffffffffffffffffffffffffffff", "task-1", "shell.exec", "whoami")] // different implant
    [InlineData(ImplantId, "task-2", "shell.exec", "whoami")]                           // different task id
    [InlineData(ImplantId, "task-1", "recon.hostenum", "whoami")]                       // different verb
    [InlineData(ImplantId, "task-1", "shell.exec", "whoami -a")]                        // different arguments
    public void SignedTasking_DoesNotVerify_AgainstTamperedFields(
        string implantId, string taskId, string verb, string arguments)
    {
        var authority = new DevCertificateAuthority();

        var signature = authority.SignTasking(ImplantId, "task-1", "shell.exec", "whoami");

        using var ca = authority.GetCaCertificate();
        using var publicKey = ca.GetRSAPublicKey()!;
        Assert.False(publicKey.VerifyData(
            TaskingCanonical.Bytes(implantId, taskId, verb, arguments),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
    }

    [Fact]
    public void CanonicalBytes_LengthPrefixesKeepEmbeddingsDistinct()
    {
        // "ab" + "c" and "a" + "bc" are the concatenation collision the length
        // prefixes exist to prevent: the canonical encodings must differ.
        var left = TaskingCanonical.Bytes("ab", "c", "", "");
        var right = TaskingCanonical.Bytes("a", "bc", "", "");
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void CanonicalBytes_EmptyFields_EncodeAsBareLengthPrefixes()
    {
        var bytes = TaskingCanonical.Bytes("", "", "", "");
        // Four uint32 zero lengths and nothing else.
        Assert.Equal(new byte[16], bytes);
    }
}
