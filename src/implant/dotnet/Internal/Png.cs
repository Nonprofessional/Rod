using System.Buffers.Binary;
using System.IO.Compression;

namespace Rod.Implant.Internal;

// A minimal PNG writer for 8-bit RGBA images -- exactly the one shape the
// screenshot verb produces (architecture.md Sec 10.1 collect, Sec 11). The
// reference implant carries no image-library dependency: a screenshot needs
// IHDR + one IDAT + IEND over the BCL's deflate, and writing those ~100
// lines keeps the artifact lean and the encoding auditable. Nothing here
// decodes; the encoder is one-way by design.

internal static class Png
{
    // The eight-byte PNG signature every decoder checks first.
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // IHDR's color type 6: truecolor with alpha, at 8 bits per channel.
    private const byte ColorTypeRgba = 6;
    private const byte BitDepth = 8;

    /// <summary>
    /// Encodes a <paramref name="width"/> x <paramref name="height"/> RGBA
    /// image (4 bytes per pixel, row-major) as a PNG. Scanlines carry the
    /// filter type 0 (None): the raw bytes are already what the capture read,
    /// and filter prediction would buy little on photographic screen content.
    /// </summary>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("rgba must hold width*height*4 bytes", nameof(rgba));

        // The raw IDAT payload: each scanline is a filter byte followed by
        // the row's RGBA bytes.
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var rawRow = y * (1 + width * 4);
            raw[rawRow] = 0; // filter: None
            rgba.Slice(y * width * 4, width * 4)
                .CopyTo(raw.AsSpan(rawRow + 1));
        }

        using var zlib = new MemoryStream(1 << 16);
        WriteZlib(zlib, raw);

        using var png = new MemoryStream(1 << 18);
        png.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = BitDepth;
        ihdr[9] = ColorTypeRgba;
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method: adaptive (types 0-4; only 0 is written)
        ihdr[12] = 0; // interlace: none
        WriteChunk(png, "IHDR", ihdr);

        WriteChunk(png, "IDAT", zlib.ToArray());
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    // Writes one PNG chunk: length (big-endian), the four-byte type, the
    // data, then the CRC-32 of type + data (big-endian).
    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        png.Write(header);
        png.Write(data);

        var crc = Crc32(System.Text.Encoding.ASCII.GetBytes(type), data);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        png.Write(trailer);
    }

    // Wraps raw bytes in the zlib container PNG requires: the 2-byte header
    // (deflate, 32 KiB window), the BCL's raw deflate stream, and the
    // big-endian Adler-32 of the uncompressed bytes.
    private static void WriteZlib(Stream zlib, ReadOnlySpan<byte> raw)
    {
        zlib.WriteByte(0x78);
        zlib.WriteByte(0x01);
        using (var deflate = new DeflateStream(zlib, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
        zlib.Write(adler);
    }

    // The PNG chunk checksum: the reflected CRC-32 (IEEE 802.3) every PNG
    // decoder verifies. Table-driven, built once.
    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // The zlib stream checksum over the uncompressed bytes.
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        foreach (var byteValue in data)
        {
            a = (a + byteValue) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
