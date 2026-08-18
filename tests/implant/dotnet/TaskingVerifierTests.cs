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
// rejected. The replay-nonce arm (Sec 9, tasking replay nonces) has its own
// battery: a nonce-bearing task verifies over the five-element tuple and
// advances the accepted floor, a repeated nonce is refused as a replay, and
// nonce-less tasking is refused once the arm was negotiated. The CA here is a
// throwaway self-signed root standing in for the enrollment CA chain the
// beacon holds at runtime; the verifier only reads its RSA public key.
public class TaskingVerifierTests
{
    private const string ImplantId = "0123456789abcdef0123456789abcdef";
    private const string OtherImplantId = "ffffffffffffffffffffffffffffffff";

    [Fact]
    public void SignedTask_Verifies()
    {
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, task, [ca], new()));
    }

    [Fact]
    public void UnsignedTask_IsRejected()
    {
        using var ca = BuildCa();
        var task = new TaskRequest { TaskId = "task-1", Verb = "shell.exec", Arguments = "whoami" };

        Assert.Equal(TaskingVerdict.RejectedSignature, TaskingVerifier.Verify(ImplantId, task, [ca], new()));
    }

    [Fact]
    public void TamperedArguments_AreRejected()
    {
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");
        task.Arguments = "whoami -a";

        Assert.Equal(TaskingVerdict.RejectedSignature, TaskingVerifier.Verify(ImplantId, task, [ca], new()));
    }

    [Fact]
    public void WrongKeySignature_IsRejected()
    {
        using var ca = BuildCa();
        using var other = BuildCa();
        var task = SignedTask(other, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.Equal(TaskingVerdict.RejectedSignature, TaskingVerifier.Verify(ImplantId, task, [ca], new()));
    }

    [Fact]
    public void TaskSignedForAnotherImplant_IsRejectedOnThisOne()
    {
        // Audience binding: a captured frame signed for a different implant
        // under the same CA must not verify here, so captured tasking cannot
        // be replayed cross-implant.
        using var ca = BuildCa();
        var task = SignedTask(ca, OtherImplantId, "task-1", "shell.exec", "whoami");

        Assert.Equal(TaskingVerdict.RejectedSignature, TaskingVerifier.Verify(ImplantId, task, [ca], new()));
    }

    [Fact]
    public void EnrollmentChainWithRootFirst_VerifiesAgainstTheRoot()
    {
        // The beacon holds the CA list root-first from enrollment; the signer
        // is that root, so a multi-entry list still verifies.
        using var ca = BuildCa();
        using var unrelated = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");

        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, task, [ca, unrelated], new()));
    }

    [Fact]
    public void NonceBearingTask_Verifies_OverTheFiveElementTuple()
    {
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami", nonce: 7);

        var nonces = new TaskNonceTracker();
        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, task, [ca], nonces));
        Assert.True(nonces.IsReplay(7)); // the floor advanced to the accepted nonce
    }

    [Fact]
    public void ReplayedNonce_IsRejected()
    {
        // The acceptance criterion for the replay-nonce arm: a captured frame
        // delivered a second time still carries a good signature, but its
        // nonce falls at the accepted floor, so it is refused and nothing
        // executes.
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami", nonce: 7);

        var nonces = new TaskNonceTracker();
        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, task, [ca], nonces));
        Assert.Equal(TaskingVerdict.RejectedReplay, TaskingVerifier.Verify(ImplantId, task, [ca], nonces));
    }

    [Fact]
    public void LaterNonce_IsAccepted_EvenAfterARollbackAttempt()
    {
        // The floor never moves down: fresh tasking after a replay keeps
        // verifying, and an older captured frame stays refused.
        using var ca = BuildCa();
        var first = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami", nonce: 7);
        var later = SignedTask(ca, ImplantId, "task-2", "shell.exec", "id", nonce: 8);
        var older = SignedTask(ca, ImplantId, "task-0", "shell.exec", "uname", nonce: 6);

        var nonces = new TaskNonceTracker();
        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, first, [ca], nonces));
        Assert.Equal(TaskingVerdict.Accepted, TaskingVerifier.Verify(ImplantId, later, [ca], nonces));
        Assert.Equal(TaskingVerdict.RejectedReplay, TaskingVerifier.Verify(ImplantId, older, [ca], nonces));
    }

    [Fact]
    public void NonceLessTask_AfterNegotiation_IsRejected()
    {
        // Once the handshake echoed replay_nonces, nonce-less tasking is not
        // the agreed shape anymore: refused, not silently accepted.
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami");

        var nonces = new TaskNonceTracker { Negotiated = true };
        Assert.Equal(TaskingVerdict.RejectedNoNonce, TaskingVerifier.Verify(ImplantId, task, [ca], nonces));
    }

    [Fact]
    public void TamperedNonce_IsRejectedByTheSignature()
    {
        // The nonce is inside the signed tuple: swapping it for a higher value
        // breaks the signature, so an attacker cannot age a captured frame past
        // the floor.
        using var ca = BuildCa();
        var task = SignedTask(ca, ImplantId, "task-1", "shell.exec", "whoami", nonce: 7);
        task.TaskNonce = 99;

        var nonces = new TaskNonceTracker();
        Assert.Equal(TaskingVerdict.RejectedSignature, TaskingVerifier.Verify(ImplantId, task, [ca], nonces));
    }

    private static TaskRequest SignedTask(
        X509Certificate2 ca, string implantId, string taskId, string verb, string arguments, ulong? nonce = null)
    {
        var task = new TaskRequest { TaskId = taskId, Verb = verb, Arguments = arguments };
        if (nonce is { } value)
            task.TaskNonce = value;
        using var key = ca.GetRSAPrivateKey()!;
        task.Signature = ByteString.CopyFrom(key.SignData(
            Canonical(implantId, taskId, verb, arguments, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return task;
    }

    // The canonical signed encoding from the TaskRequest contract in rod.proto:
    // per field, the little-endian uint32 UTF-8 byte length then the bytes. The
    // nonce, when the task carries one, is the fifth field as its unsigned
    // decimal string.
    private static byte[] Canonical(string implantId, string taskId, string verb, string arguments, ulong? nonce)
    {
        var fields = nonce is { } value
            ? new[] { implantId, taskId, verb, arguments, value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : new[] { implantId, taskId, verb, arguments };
        var encoded = fields.Select(f => Encoding.UTF8.GetBytes(f)).ToArray();
        using var buffer = new MemoryStream();
        foreach (var field in encoded)
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
