using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// TaskingVerifierTests checks the implant's side of command signing
// (architecture.md Sec 9): a task signed by the tasking CA for this implant
// verifies, and a tampered, wrong-key, empty, or other-implant signature is
// rejected. The CA here is a throwaway self-signed root standing in for the
// enrollment CA chain the beacon holds at runtime; the verifier only reads its
// RSA public key.
public class TaskingVerifierTests
{
    private const string ImplantId = "0123456789abcdef0123456789abcdef";
    private const string OtherImplantId = "ffffffffffffffffffffffffffffffff";

    [Fact]
    public void SignedTask_Verifies()
    {
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.True(TaskingVerifier.Verify(ImplantId, task, [ca]));
    }

    [Fact]
    public void UnsignedTask_IsRejected()
    {
        using var ca = BuildCa();
        var task = new TaskRequest { TaskId = "task-1", Verb = "shell.exec", Arguments = "whoami" };

        Assert.False(TaskingVerifier.Verify(ImplantId, task, [ca]));
    }

    [Fact]
    public void TamperedArguments_AreRejected()
    {
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");
        task.Arguments = "whoami -a";

        Assert.False(TaskingVerifier.Verify(ImplantId, task, [ca]));
    }

    [Fact]
    public void WrongKeySignature_IsRejected()
    {
        using var ca = BuildCa();
        using var other = BuildCa();
        var task = SignedTask(other, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.False(TaskingVerifier.Verify(ImplantId, task, [ca]));
    }

    [Fact]
    public void TaskSignedForAnotherImplant_IsRejectedOnThisOne()
    {
        // Audience binding: a captured frame signed for a different implant
        // under the same CA must not verify here, so captured tasking cannot
        // be replayed cross-implant.
        using var ca = BuildCa();
        var task = SignedTask(ca, OtherImplantId, "task-1", "shell.exec", "whoami");

        Assert.False(TaskingVerifier.Verify(ImplantId, task, [ca]));
    }

    [Fact]
    public void EnrollmentChainWithRootFirst_VerifiesAgainstTheRoot()
    {
        // The beacon holds the CA list root-first from enrollment; the signer
        // is that root, so a multi-entry list still verifies.
        using var ca = BuildCa();
        using var unrelated = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.True(TaskingVerifier.Verify(ImplantId, task, [ca, unrelated]));
    }

    private static TaskRequest SignedTask(
        X509Certificate2 ca, string implantId, string taskId, string verb, string arguments)
    {
        var task = new TaskRequest { TaskId = taskId, Verb = verb, Arguments = arguments };
        using var key = ca.GetRSAPrivateKey()!;
        task.Signature = ByteString.CopyFrom(key.SignData(
            Canonical(implantId, taskId, verb, arguments), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return task;
    }

    // The canonical signed encoding from the TaskRequest contract in rod.proto:
    // per field, the little-endian uint32 UTF-8 byte length then the bytes.
    private static byte[] Canonical(string implantId, string taskId, string verb, string arguments)
    {
        var fields = new[] { implantId, taskId, verb, arguments }
            .Select(f => Encoding.UTF8.GetBytes(f))
            .ToArray();
        using var buffer = new MemoryStream();
        foreach (var field in fields)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)field.Length);
            buffer.Write(length);
            buffer.Write(field);
        }
        return buffer.ToArray();
    }

    private static X509Certificate2 BuildCa()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=rod-tasking-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
