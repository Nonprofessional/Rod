using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// Covers collect.screenshot end to end on the implant side (architecture.md
// Sec 10.1 collect, Sec 11): the PNG encoder's structure, the verb's
// capture-to-chunks pipeline (driven through the capture seam, so no live
// display is needed), and the clean refusal a headless host owes its
// operator. The structure assertions decode what the encoder wrote -- chunks,
// CRCs, the zlib stream -- rather than trusting it opaquely.
public class ScreenshotTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void EncodeRgba_WritesAValidPngStructure()
    {
        const int width = 3;
        const int height = 2;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                rgba[at] = (byte)(x * 40);
                rgba[at + 1] = (byte)(y * 50);
                rgba[at + 2] = (byte)(255 - x);
                rgba[at + 3] = 255;
            }
        }

        var png = Png.EncodeRgba(width, height, rgba);

        Assert.Equal(PngSignature, png[..8]);
        var chunks = ParseChunks(png);
        Assert.Equal(3, chunks.Count);

        // IHDR: size, 8-bit RGBA, no interlace.
        var ihdr = chunks[0];
        Assert.Equal("IHDR", ihdr.Type);
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(ihdr.Data));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(ihdr.Data[4..]));
        Assert.Equal(8, ihdr.Data[8]);
        Assert.Equal(6, ihdr.Data[9]);

        // IDAT: the zlib stream inflates back to filter-prefixed scanlines
        // carrying the exact pixels handed in.
        Assert.Equal("IDAT", chunks[1].Type);
        using var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal(height * (1 + width * 4), bytes.Length);
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            Assert.Equal(0, bytes[row]); // filter: None
            Assert.Equal(
                rgba.AsSpan(y * width * 4, width * 4).ToArray(),
                bytes[(row + 1)..(row + 1 + width * 4)]);
        }

        // IEND closes the image.
        Assert.Equal("IEND", chunks[2].Type);
        Assert.Empty(chunks[2].Data);

        // Every chunk's CRC matches its type + data.
        foreach (var chunk in chunks)
            Assert.Equal(CrcOf(chunk.TypeAscii, chunk.Data), chunk.Crc);
    }

    [Fact]
    public void EncodeRgba_RejectsMismatchedPixelBuffers()
    {
        Assert.Throws<ArgumentException>(() => Png.EncodeRgba(2, 2, new byte[7]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Png.EncodeRgba(-1, 2, new byte[8]));
    }

    [Fact]
    public void Screenshot_WithACapture_StreamsPngChunks()
    {
        const int width = 4;
        const int height = 3;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251);
            rgba[i + 1] = 0x40;
            rgba[i + 2] = (byte)(i % 199);
            rgba[i + 3] = 255;
        }

        var (outcome, output, chunks) = Collect.ScreenshotWithCapture(
            () => new CapturedScreen(width, height, rgba));

        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains($"captured {width}x{height}", output);
        Assert.Contains("screenshot-", output);

        // One terminal chunk carrying the whole PNG (a 4x3 frame is far under
        // the chunk ceiling), typed as a PNG artifact.
        var chunk = Assert.Single(chunks);
        Assert.True(chunk.Terminal);
        Assert.Equal("image/png", chunk.ContentType);
        Assert.EndsWith(".png", chunk.Name);
        var png = chunk.Data.ToByteArray();
        Assert.Equal(PngSignature, png[..8]);
        var parsed = ParseChunks(png);
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(parsed[0].Data));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(parsed[0].Data[4..]));
    }

    [Fact]
    public void Screenshot_WhenCaptureThrows_FailsWithTheCause()
    {
        var (outcome, output, chunks) = Collect.ScreenshotWithCapture(
            () => throw new InvalidOperationException("no capture device"));

        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("no capture device", output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Screenshot_OnTheLocalHost_EitherCapturesOrRefusesCleanly()
    {
        // The dispatch path over the real platform capture: on a host with a
        // display (Windows, or X11 with DISPLAY set) the verb captures and
        // streams; on a headless host it refuses with the cause on the task.
        // Both halves are the contract; which one runs is the host's.
        var registry = HandlerRegistry.Default();
        var (outcome, output, chunks) = registry.Dispatch("collect.screenshot", "");
        var hasDisplay = OperatingSystem.IsWindows()
                         || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        if (hasDisplay)
        {
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.NotEmpty(chunks);
        }
        else
        {
            Assert.Equal(TaskOutcome.Failed, outcome);
            Assert.Contains("display", output, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(chunks);
        }
    }

    // Splits a PNG into its chunks, verifying each declared length fits.
    private static List<PngChunk> ParseChunks(byte[] png)
    {
        var chunks = new List<PngChunk>();
        var at = 8; // past the signature
        while (at < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at)));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png[(at + 8)..(at + 8 + length)];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            chunks.Add(new PngChunk(type, data, crc, png[(at + 4)..(at + 8)]));
            at += 12 + length;
        }
        return chunks;
    }

    private sealed record PngChunk(string Type, byte[] Data, uint Crc, byte[] TypeAscii);

    // The PNG chunk checksum, recomputed independently of the encoder.
    private static uint CrcOf(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
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
}
