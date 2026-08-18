using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

/// <summary>Why a dispatched task was accepted or refused.</summary>
internal enum TaskingVerdict
{
    /// <summary>The signature verified and the nonce (if any) advanced the floor.</summary>
    Accepted,

    /// <summary>The signature was absent or failed verification; nothing executed.</summary>
    RejectedSignature,

    /// <summary>
    /// The task's nonce is at or below the highest already accepted: a replayed
    /// frame (architecture.md Sec 9 -- tasking replay nonces). Nothing executed.
    /// </summary>
    RejectedReplay,

    /// <summary>
    /// The task carries no nonce after the handshake negotiated the replay-nonce
    /// arm -- the nonce-less shape is only legal before negotiation. Nothing
    /// executed.
    /// </summary>
    RejectedNoNonce,
}

/// <summary>
/// The implant's replay-nonce state (architecture.md Sec 9 -- tasking replay
/// nonces): whether the handshake negotiated the arm, and the highest nonce
/// this implant has accepted. The floor spans the implant's whole run -- it is
/// NOT reset on a reconnect -- because the server's counter is per-implant, so
/// a nonce at or below the floor is a replay no matter which connection
/// delivered it.
/// </summary>
internal sealed class TaskNonceTracker
{
    /// <summary>
    /// True once a handshake echoed <c>replay_nonces</c>: from then on every
    /// dispatched task must carry a nonce, and nonce-less tasking is refused.
    /// </summary>
    public bool Negotiated { get; set; }

    private ulong _highest;

    /// <summary>True when the nonce was already accepted (or passed): a replay.</summary>
    public bool IsReplay(ulong nonce) => nonce <= _highest;

    /// <summary>Records an accepted nonce as the new floor.</summary>
    public void Observed(ulong nonce)
    {
        if (nonce > _highest)
            _highest = nonce;
    }
}

/// <summary>
/// Verifies the teamserver's tasking signature before a task executes
/// (architecture.md Sec 9 -- command signing). The implant acts only on
/// teamserver-authorized tasking: the CA whose leaf the implant presents also
/// signs each dispatched task, and the implant holds that CA certificate from
/// enrollment (or its pinned bundle), so verification needs no extra key
/// material. This class is the implant's independent side of the canonical
/// encoding contract documented on the TaskRequest message in rod.proto --
/// implants build against the protocol, not the teamserver's assemblies.
///
/// A task that carries <c>task_nonce</c> (the negotiated replay-nonce arm) is
/// verified over the five-element tuple and then checked against the nonce
/// floor: a captured frame replayed to this implant falls at or below the
/// floor and is refused, with the refusal reported on the task so the attack
/// is visible to the operator.
/// </summary>
internal static class TaskingVerifier
{
    /// <summary>
    /// Verifies the task's RSASSA-PSS/SHA-256 signature over the canonical
    /// tuple -- four elements for nonce-less tasking, five when the task
    /// carries <c>task_nonce</c> -- where the implant id is this implant's own
    /// <paramref name="implantId"/>: a frame signed for a different implant of
    /// the same CA fails here and captured tasking cannot be replayed
    /// cross-implant. Verification tries each RSA-bearing CA in
    /// <paramref name="cas"/> because the list's provenance differs by
    /// deployment -- the enrollment chain (root first) or the pinned CA bundle
    /// -- and the signer is whichever CA issued this implant's leaf. An empty
    /// signature is unsigned and always rejected. A verified nonce-bearing
    /// task must also advance <paramref name="nonces"/>'s floor, and a
    /// nonce-less task is refused once the arm was negotiated.
    /// </summary>
    public static TaskingVerdict Verify(
        string implantId,
        TaskRequest task,
        IReadOnlyList<X509Certificate2> cas,
        TaskNonceTracker nonces)
    {
        if (task.Signature.Length == 0)
            return TaskingVerdict.RejectedSignature;

        var canonical = task.HasTaskNonce
            ? CanonicalBytes(implantId, task.TaskId, task.Verb, task.Arguments, task.TaskNonce)
            : CanonicalBytes(implantId, task.TaskId, task.Verb, task.Arguments);

        foreach (var ca in cas)
        {
            using var publicKey = ca.GetRSAPublicKey();
            if (publicKey is null)
                continue;
            if (!publicKey.VerifyData(canonical, task.Signature.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                continue;

            // The signature is good; the nonce arm has the final word.
            if (task.HasTaskNonce)
            {
                if (nonces.IsReplay(task.TaskNonce))
                    return TaskingVerdict.RejectedReplay;
                nonces.Observed(task.TaskNonce);
                return TaskingVerdict.Accepted;
            }
            return nonces.Negotiated
                ? TaskingVerdict.RejectedNoNonce
                : TaskingVerdict.Accepted;
        }
        return TaskingVerdict.RejectedSignature;
    }

    // The canonical signed encoding: for each tuple element, the little-endian
    // uint32 UTF-8 byte length followed by the bytes. The nonce -- when the
    // task carries one -- is the fifth element, its value the nonce's unsigned
    // decimal string, exactly the server-side encoding documented on
    // TaskRequest.
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

    private static byte[] CanonicalBytes(string implantId, string taskId, string verb, string arguments, ulong nonce)
    {
        var n = Encoding.UTF8.GetBytes(nonce.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var four = CanonicalBytes(implantId, taskId, verb, arguments);
        var buffer = new byte[four.Length + 4 + n.Length];
        four.CopyTo(buffer, 0);
        WriteField(buffer, four.Length, n);
        return buffer;
    }

    private static int WriteField(byte[] buffer, int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)value.Length);
        value.CopyTo(buffer, offset + 4);
        return offset + 4 + value.Length;
    }
}
