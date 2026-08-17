using System.Buffers.Binary;
using System.Text;

namespace Rod.CoreState.Pki;

/// <summary>
/// The canonical byte encoding a tasking signature covers (architecture.md
/// Sec 9 -- command signing): for each of the target implant id, task id,
/// verb, and arguments, in that order, the little-endian uint32 byte length of
/// its UTF-8 encoding followed by those bytes. Signing the canonical form
/// rather than the serialized <c>TaskRequest</c> keeps the contract
/// language-neutral -- a community implant verifies without depending on any
/// protobuf runtime's field-ordering behavior. The implant id is the intended
/// executor's own identity, so a frame signed for one implant does not verify
/// on another under the same CA. The wire shape is documented on the
/// <c>TaskRequest</c> message in rod.proto; this helper is the teamserver's
/// side of that contract, and the reference implant carries its own
/// independent copy (implants build against the protocol, not the
/// teamserver's assemblies).
/// </summary>
public static class TaskingCanonical
{
    /// <summary>
    /// Builds the canonical bytes for a task's signed tuple. The encoding is
    /// length-prefixed, so values containing each other (or the delimiter
    /// characters of any simpler scheme) cannot collide.
    /// </summary>
    public static byte[] Bytes(string implantId, string taskId, string verb, string arguments)
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
