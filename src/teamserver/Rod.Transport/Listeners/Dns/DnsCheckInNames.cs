using System.Collections.Concurrent;
using Rod.CoreState;

namespace Rod.Transport.Listeners.Dns;

// The DNS check-in name grammar (architecture.md Sec 8, the DNS listener's
// contract, documented for implant authors in extending/implants.md). Every
// check-in is a TXT query under the listener's zone -- the listener's public
// endpoint, a domain the teamserver answers for. The query NAME carries the
// implant's message, base32-encoded into labels (DNS labels are case-
// insensitive; the encoding is lowercase RFC 4648 without padding):
//
//   poll (presence + fetch next tasking):
//     p.<b32(implant id)>.<zone>
//
//   result chunk (report a task's outcome, short outputs only):
//     r.<b32(task id)>.<outcome s|f>.<seq>.<terminal t|m>.<b32(chunk)>.<b32(implant id)>.<zone>
//
// A poll is answered with zero or one TXT record whose strings concatenate to
// the base32 of a signed rod.v1 TaskRequest; a result is answered with an
// empty NOERROR answer. The chunk sequence is 0-origin decimal; the terminal
// flag closes the reassembly. Short-argument tasking only: a TaskRequest that
// does not fit the DNS budget is not claimed over this transport.

/// <summary>
/// Parses and renders the check-in query names. Pure grammar, no I/O -- the
/// listener service and the tests share one definition.
/// </summary>
internal static class DnsCheckInNames
{
    /// <summary>A poll: the implant's presence ping and task fetch.</summary>
    internal sealed record Poll(ImplantId Implant);

    /// <summary>One chunk of a task result the implant reports back.</summary>
    internal sealed record ResultChunk(
        ImplantId Implant,
        TaskId Task,
        Rod.CoreState.Tasks.TaskOutcome Outcome,
        int Sequence,
        bool Terminal,
        byte[] Chunk);

    /// <summary>
    /// Parses a query name against <paramref name="zone"/>. Returns the poll
    /// or result-chunk view, or null when the name is not a check-in under
    /// this zone (other names in the zone are answered NXDOMAIN by the
    /// listener, not parsed here).
    /// </summary>
    public static Poll? TryParsePoll(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 2 || labels[0] != "p")
            return null;
        if (!TryDecodeId(labels[1], out var implant))
            return null;
        return new Poll(implant);
    }

    public static ResultChunk? TryParseResult(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 7 || labels[0] != "r")
            return null;
        if (!TryDecodeId(labels[6], out var implant))
            return null;
        if (!TryDecode(labels[1], out var taskBytes)
            || !TaskId.TryParse(System.Text.Encoding.UTF8.GetString(taskBytes), out var task))
            return null;

        var outcome = labels[2] switch
        {
            "s" => Rod.CoreState.Tasks.TaskOutcome.Succeeded,
            "f" => Rod.CoreState.Tasks.TaskOutcome.Failed,
            _ => (Rod.CoreState.Tasks.TaskOutcome?)null,
        };
        var terminal = labels[4] switch
        {
            "t" => true,
            "m" => false,
            _ => (bool?)null,
        };
        if (outcome is null || terminal is null || !int.TryParse(labels[3], out var sequence) || sequence < 0)
            return null;

        // An empty chunk rides as the bare label "e": base32 of zero bytes
        // would render an empty label, which a DNS name cannot carry.
        var chunk = labels[5] == "e"
            ? Array.Empty<byte>()
            : TryDecode(labels[5], out var decoded) ? decoded : null;
        if (chunk is null)
            return null;

        return new ResultChunk(implant, task, outcome.Value, sequence, terminal.Value, chunk);
    }

    /// <summary>Renders a poll name (the implant-side twin of the parser).</summary>
    public static string PollName(ImplantId implant, string zone)
        => $"p.{Encode(implant.ToString())}.{zone}";

    /// <summary>Renders a result-chunk name under <paramref name="zone"/>.</summary>
    public static string ResultName(
        ImplantId implant, TaskId task, bool succeeded, int sequence, bool terminal, byte[] chunk, string zone)
        => "r." + Encode(task.ToString())
            + "." + (succeeded ? "s" : "f")
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + Encode(implant.ToString())
            + "." + zone;

    /// <summary>The zone label suffix a name must end with, case-insensitive.</summary>
    private static bool TryStripZone(string name, string zone, out string[] labels)
    {
        labels = Array.Empty<string>();
        if (!name.EndsWith(zone, StringComparison.OrdinalIgnoreCase))
            return false;
        var head = name[..^zone.Length].TrimEnd('.');
        if (head.Length == 0)
            return false;
        labels = head.Split('.');
        return labels.Length > 0 && labels[0] is "p" or "r";
    }

    private static bool TryDecodeId(string label, out ImplantId implant)
    {
        implant = default;
        if (!TryDecode(label, out var raw))
            return false;
        if (!Guid.TryParse(System.Text.Encoding.UTF8.GetString(raw), out var value))
            return false;
        implant = new ImplantId(value);
        return true;
    }

    /// <summary>Lowercase RFC 4648 base32 without padding.</summary>
    public static string Encode(byte[] bytes)
        => Base32Lower(bytes);

    /// <summary>Encodes a UTF-8 string's bytes.</summary>
    public static string Encode(string text)
        => Base32Lower(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Decodes base32 (either case, padding tolerated).</summary>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        try
        {
            bytes = System.Convert.FromHexString(ToHex(text));
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    // Base32 decode without System.Buffers.Text base32 support (the BCL has
    // none): map the RFC 4648 alphabet back to bits via a hex intermediate.
    private static string ToHex(string text)
    {
        var clean = text.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var bitCount = 0;
        var hex = new System.Text.StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            var value = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= '2' and <= '7' => c - '2' + 26,
                _ => throw new FormatException("Not base32."),
            };
            bits = (bits << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                hex.Append(((bits >> bitCount) & 0xFF).ToString("X2"));
            }
        }
        // Trailing partial bits are padding, not data; the encoder never emits
        // them, so a clean multiple of 40 bits decodes losslessly.
        return hex.ToString();
    }

    private static string Base32Lower(byte[] bytes)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var sb = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        var bits = 0;
        var bitCount = 0;
        foreach (var b in bytes)
        {
            bits = (bits << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                sb.Append(alphabet[(bits >> bitCount) & 0x1F]);
            }
        }
        if (bitCount > 0)
            sb.Append(alphabet[(bits << (5 - bitCount)) & 0x1F]);
        return sb.ToString();
    }

    /// <summary>
    /// The bounded reassembly buffer for result chunks: per-task chunk maps
    /// with a total-size cap and an entry cap so a spoofed flood cannot grow
    /// the listener without bound. Entries vanish on completion or when the
    /// caps push them out.
    /// </summary>
    internal sealed class ResultReassembler
    {
        private readonly ConcurrentDictionary<TaskId, ConcurrentDictionary<int, byte[]>> _byTask = new();
        private readonly ConcurrentDictionary<TaskId, int> _bytesByTask = new();

        public const int MaxTaskBytes = 4 * 1024;
        public const int MaxTasks = 256;

        /// <summary>
        /// Adds a chunk; on the terminal chunk, returns the concatenated
        /// output when the sequence is contiguous 0..n, else null (the
        /// reassembly is dropped). Non-terminal adds return null.
        /// </summary>
        public byte[]? Add(TaskId task, int sequence, bool terminal, byte[] chunk)
        {
            var chunks = _byTask.GetOrAdd(task, _ => new ConcurrentDictionary<int, byte[]>());
            var total = _bytesByTask.AddOrUpdate(task, chunk.Length, (_, soFar) => soFar + chunk.Length);
            if (total > MaxTaskBytes || _byTask.Count > MaxTasks)
            {
                _byTask.TryRemove(task, out _);
                _bytesByTask.TryRemove(task, out _);
                return null;
            }
            chunks[sequence] = chunk;

            if (!terminal)
                return null;

            _byTask.TryRemove(task, out _);
            _bytesByTask.TryRemove(task, out _);
            if (!TryConcatenate(chunks, out var output))
                return null;
            return output;
        }

        private static bool TryConcatenate(ConcurrentDictionary<int, byte[]> chunks, out byte[] output)
        {
            output = Array.Empty<byte>();
            var ordered = chunks.Keys.OrderBy(k => k).ToArray();
            if (ordered.Length == 0 || ordered[0] != 0 || ordered[^1] != ordered.Length - 1)
                return false; // not contiguous from zero: a chunk went missing
            var total = ordered.Sum(k => chunks[k].Length);
            var buffer = new byte[total];
            var offset = 0;
            foreach (var k in ordered)
            {
                chunks[k].CopyTo(buffer, offset);
                offset += chunks[k].Length;
            }
            output = buffer;
            return true;
        }
    }
}
