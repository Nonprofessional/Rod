using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// FileOpsTests covers the core file-transfer dispatch surface: file.pull
// read/missing/directory refusal, the large-file chunking path, and file.push
// argument parsing, the base64 decode, the size cap, and the on-disk write
// (including parent-directory creation). Both verbs run through the registry so
// the tests pin dispatch, not just the handlers.
public class FileOpsTests
{
    private static HandlerRegistry NewRegistry() => HandlerRegistry.Default();

    [Fact]
    public void Pull_MissingFile_FailsWithCause()
    {
        using var dir = TempDir.Create();
        var registry = NewRegistry();
        var (outcome, output, chunks) = registry.Dispatch(
            "file.pull", Path.Combine(dir.Path, "absent"));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("stat ", output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Pull_EmptyPath_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("file.pull", "");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("file.pull expects", output);
    }

    [Fact]
    public void Pull_Directory_RefusesWithCause()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var (outcome, output, _) = registry.Dispatch("file.pull", dir.Path);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("directory", output);
    }

    [Fact]
    public void Pull_SucceedsWithContents()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "note.txt");
        const string want = "hello file.pull";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(want));

        var (outcome, output, chunks) = registry.Dispatch("file.pull", path);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal(want, output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Pull_LargeFile_ProducesChunks()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "big.bin");
        // Just over the 1 MiB inline limit so the file streams as ExfilChunks.
        const int inlineLimit = 1 << 20;
        var payload = new byte[inlineLimit + 4096];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);

        var (outcome, output, chunks) = registry.Dispatch("file.pull", path);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("chunks streamed", output);
        Assert.NotEmpty(chunks);

        var reassembled = new List<byte>();
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal((ulong)i, chunks[i].Sequence);
            reassembled.AddRange(chunks[i].Data.ToByteArray());
        }
        Assert.True(chunks[^1].Terminal, "last chunk should be terminal");
        Assert.Equal(payload, reassembled.ToArray());
    }

    [Fact]
    public void Push_MissingPayload_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("file.push", "/tmp/just-a-path");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("file.push expects", output);
    }

    [Fact]
    public void Push_InvalidBase64_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output, _) = registry.Dispatch("file.push", "/tmp/rod-target !!!not-base64!!!");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("base64", output);
    }

    [Fact]
    public void Push_OverTheCap_FailsNamingTheCap()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "over.bin");
        // 1 MiB + 1 byte decoded: over the single-task push cap.
        var oversized = new byte[(1 << 20) + 1];
        var (outcome, output, _) = registry.Dispatch(
            "file.push", path + " " + Convert.ToBase64String(oversized));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("cap", output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Push_WritesTheDecodedBytes()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "dropped", "tool.bin");
        var payload = Encoding.UTF8.GetBytes("uploaded file contents");

        var (outcome, output, _) = registry.Dispatch(
            "file.push", path + " " + Convert.ToBase64String(payload));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("wrote", output);
        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void Push_PathWithSpaces_WritesTheWholePath()
    {
        // The split is on the first space: a destination containing spaces
        // still lands whole, because the base64 tail never contains one.
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "with space", "file name.txt");
        var payload = Encoding.UTF8.GetBytes("spaced");

        var (outcome, _, _) = registry.Dispatch(
            "file.push", path + " " + Convert.ToBase64String(payload));
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    // The ChunkFile unit tests exercise the chunker directly. An empty buffer
    // produces no chunks at all: nothing to stream, and the server drops empty
    // frames.

    [Fact]
    public void ChunkFile_EmptyInput_ProducesNoChunks()
    {
        var chunks = Files.ChunkFile("empty.txt", "text/plain", Array.Empty<byte>());
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkFile_OneAndAHalfChunks_TwoChunksLastTerminal()
    {
        const int chunkSize = 512 * 1024;
        var data = new byte[chunkSize + 1024]; // 1.5 chunks
        var chunks = Files.ChunkFile("blob.bin", "application/octet-stream", data);
        Assert.Equal(2, chunks.Count);
        Assert.False(chunks[0].Terminal);
        Assert.True(chunks[1].Terminal);
        Assert.Equal(0UL, chunks[0].Sequence);
        Assert.Equal(1UL, chunks[1].Sequence);
    }

    // The staged push tests (architecture.md Sec 10, the typed arm): the bulk
    // payload arrives as the reassembled chunk run, the sha256 token rides the
    // signed arguments, and the handler verifies the hash before anything
    // touches disk. No size cap applies on this path -- the bytes never rode a
    // single frame.

    private static string ShaToken(byte[] data)
        => "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void PushStaged_WritesTheVerifiedBytes()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "dropped", "large.bin");
        var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10 * 1024 * 1024);

        var (outcome, output) = registry.DispatchStaged("file.push", path + " " + ShaToken(payload), payload);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("wrote", output);
        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void PushStaged_HashMismatch_RefusesToWrite()
    {
        var registry = NewRegistry();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "altered.bin");
        var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096);
        var wrongToken = ShaToken(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));

        var (outcome, output) = registry.DispatchStaged("file.push", path + " " + wrongToken, payload);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("hash mismatch", output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PushStaged_MalformedGrammar_FailsWithCause()
    {
        var registry = NewRegistry();

        var (outcomeNoToken, outputNoToken) = registry.DispatchStaged("file.push", "/tmp/dest", new byte[16]);
        Assert.Equal(TaskOutcome.Failed, outcomeNoToken);
        Assert.Contains("sha256", outputNoToken);

        var (outcomeBadToken, _) = registry.DispatchStaged("file.push", "/tmp/dest sha256:nothex", new byte[16]);
        Assert.Equal(TaskOutcome.Failed, outcomeBadToken);
    }

    [Fact]
    public void DispatchStaged_VerbWithoutStagedHandler_FailsWithCause()
    {
        var registry = NewRegistry();
        var (outcome, output) = registry.DispatchStaged("shell.exec", "whoami", new byte[16]);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("does not accept a staged payload", output);
    }
}
