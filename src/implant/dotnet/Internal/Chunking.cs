using Rod.V1;

namespace Rod.Implant.Internal;

// Shared exfil chunking for the collect.file and exfil.push handlers
// (architecture.md Sec 10.1 exfil): one chunker, one contract. Slices a byte
// buffer into ExfilChunk frames of a fixed size, stamps the terminal flag on
// the last chunk, and numbers chunks 0-origin in stream order -- the server
// reassembles strictly by sequence and materializes the artifact on the
// terminal chunk. An empty buffer produces no chunks: an empty file is nothing
// to stream (collect.file returns it inline; exfil.push reports zero chunks),
// and the server drops empty frames.
internal static class Chunking
{
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
