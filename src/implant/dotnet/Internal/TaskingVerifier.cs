using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

/// <summary>
/// Verifies the teamserver's tasking signature before a task executes
/// (architecture.md Sec 9 -- command signing). The implant acts only on
/// teamserver-authorized tasking: the CA whose leaf the implant presents also
/// signs each dispatched task, and the implant holds that CA certificate from
/// enrollment (or its pinned bundle), so verification needs no extra key
/// material. This class is the implant's independent side of the canonical
/// encoding contract documented on the TaskRequest message in rod.proto --
/// implants build against the protocol, not the teamserver's assemblies.
/// </summary>
internal static class TaskingVerifier
{
    /// <summary>
    /// Verifies the task's RSASSA-PSS/SHA-256 signature over the canonical
    /// <c>(implant_id, task_id, verb, arguments)</c> encoding, where the
    /// implant id is this implant's own <paramref name="implantId"/> -- so a
    /// frame signed for a different implant of the same CA fails here and
    /// captured tasking cannot be replayed cross-implant. Verification tries
    /// each RSA-bearing CA in <paramref name="cas"/> because the list's
    /// provenance differs by deployment -- the enrollment chain (root first)
    /// or the pinned CA bundle -- and the signer is whichever CA issued this
    /// implant's leaf. An empty signature is unsigned and always rejected.
    /// </summary>
    public static bool Verify(string implantId, TaskRequest task, IReadOnlyList<X509Certificate2> cas)
    {
        if (task.Signature.Length == 0)
            return false;

        var canonical = CanonicalBytes(implantId, task.TaskId, task.Verb, task.Arguments);
        foreach (var ca in cas)
        {
            using var publicKey = ca.GetRSAPublicKey();
            if (publicKey is null)
                continue;
            if (publicKey.VerifyData(canonical, task.Signature.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return true;
        }
        return false;
    }

    // The canonical signed encoding: for each of implant_id, task_id, verb,
    // arguments, the little-endian uint32 UTF-8 byte length followed by the
    // bytes.
    private static byte[] CanonicalBytes(string implantId, string taskId, string verb, string arguments)
    {
        var id = Encoding.UTF8.GetBytes(implantId);
        var task = Encoding.UTF8.GetBytes(taskId);
        var v = Encoding.UTF8.GetBytes(verb);
        var args = Encoding.UTF8.GetBytes(arguments);
        var buffer = new byte[16 + id.Length + task.Length + v.Length + args.Length];
        var offset = WriteField(buffer, 0, id);
        offset = WriteField(buffer, offset, task);
        offset = WriteField(buffer, offset, v);
        WriteField(buffer, offset, args);
        return buffer;
    }

    private static int WriteField(byte[] buffer, int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)value.Length);
        value.CopyTo(buffer, offset + 4);
        return offset + 4 + value.Length;
    }
}
