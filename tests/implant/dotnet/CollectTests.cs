using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// CollectTests ports collect_test.go from the Go reference implant to xUnit,
// covering the collect.* dispatch surface: argument parsing, collect.file
// read/missing/directory refusal, the large-file chunking path, and collect.cred
// source filtering plus the no-secret-material invariant. The AWS/SSH
// enumeration runs against a synthetic HOME so the test never touches the
// developer's own ~/.ssh or ~/.aws; cmdkey is Windows-only and its refusal is
// documented by the platform branch.
public class CollectTests
{
    private static Runner NewRunner() => new();

    [Fact]
    public void CollectFile_MissingFile_FailsWithCause()
    {
        using var dir = TempDir.Create();
        var runner = NewRunner();
        var (outcome, output, chunks) = runner.Dispatch(
            "collect.file", Path.Combine(dir.Path, "absent"));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("stat ", output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void CollectFile_EmptyPath_FailsWithCause()
    {
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("collect.file", "");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("collect.file expects", output);
    }

    [Fact]
    public void CollectFile_Directory_RefusesWithCause()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var (outcome, output, _) = runner.Dispatch("collect.file", dir.Path);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("directory", output);
    }

    [Fact]
    public void CollectFile_SucceedsWithContents()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "note.txt");
        const string want = "hello collect.file";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(want));

        var (outcome, output, chunks) = runner.Dispatch("collect.file", path);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Equal(want, output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void CollectFile_LargeFile_ProducesChunks()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "big.bin");
        // Just over the 1 MiB inline limit so the file streams as ExfilChunks.
        const int inlineLimit = 1 << 20;
        var payload = new byte[inlineLimit + 4096];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);

        var (outcome, output, chunks) = runner.Dispatch("collect.file", path);
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
    public void CollectCred_UnknownSource_FailsWithCause()
    {
        var runner = NewRunner();
        var (outcome, output, _) = runner.Dispatch("collect.cred", "kerberos");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("unknown source", output);
    }

    [Fact]
    public void CollectCred_ListsSSHProfiles_NoSecretMaterial()
    {
        // The synthetic ~/.ssh fixture relies on POSIX HOME; the Windows build
        // exercises the cred path end-to-end instead.
        if (!OperatingSystem.IsLinux()) return;

        using var home = TempDir.Create();
        using (new EnvScope("HOME", home.Path))
        {
            Directory.CreateDirectory(Path.Combine(home.Path, ".ssh"));
            // A bare private key (no .pub sibling) so the "private key, no .pub"
            // line appears. The handler reads private-key presence by name only,
            // never the bytes, so the body is a recognizable canary.
            File.WriteAllText(Path.Combine(home.Path, ".ssh", "id_bare"),
                "-----BEGIN OPENSSH PRIVATE KEY-----\nFAKEKEYBODY_DO_NOT_LEAK\n-----END OPENSSH PRIVATE KEY-----\n");
            // A public key; the handler fingerprints it (or skips on a parse
            // failure). Either way the bare-key line proves the no-secret rule.
            File.WriteAllText(Path.Combine(home.Path, ".ssh", "id_ed25519.pub"),
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKdTestKeyRodCollectCredSSH collect-test\n");

            var runner = NewRunner();
            var (outcome, output, _) = runner.Dispatch("collect.cred", "ssh");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("id_bare", output);
            Assert.Contains("no .pub sibling", output);
            // The private key body must never appear in the output.
            Assert.DoesNotContain("FAKEKEYBODY_DO_NOT_LEAK", output);
            Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", output);
        }
    }

    [Fact]
    public void CollectCred_ListsAWSProfiles_NoSecretMaterial()
    {
        if (!OperatingSystem.IsLinux()) return; // synthetic ~/.aws relies on POSIX HOME

        using var home = TempDir.Create();
        using (new EnvScope("HOME", home.Path))
        {
            Directory.CreateDirectory(Path.Combine(home.Path, ".aws"));
            File.WriteAllText(Path.Combine(home.Path, ".aws", "credentials"),
                "[default]\n" +
                "aws_access_key_id = AKIAFAKEKEYID1234\n" +
                "aws_secret_access_key = sUpErSeCrEtDoNoTlEaK1234567890\n" +
                "\n" +
                "[work]\n" +
                "aws_access_key_id = AKIAOTHERKEYID5678\n" +
                "aws_secret_access_key = aNoThErSeCrEtVaLuE0987654321\n");

            var runner = NewRunner();
            var (outcome, output, _) = runner.Dispatch("collect.cred", "aws");
            Assert.Equal(TaskOutcome.Succeeded, outcome);
            Assert.Contains("aws default", output);
            Assert.Contains("aws work", output);
            Assert.Contains("secret in file", output);
            // No secret access key value is ever surfaced.
            Assert.DoesNotContain("sUpErSeCrEtDoNoTlEaK", output);
            Assert.DoesNotContain("aNoThErSeCrEtVaLuE", output);
        }
    }

    // The ChunkFile unit tests exercise the chunker directly. An empty buffer
    // produces no chunks at all: nothing to stream, and the server drops empty
    // frames.

    [Fact]
    public void ChunkFile_EmptyInput_ProducesNoChunks()
    {
        var chunks = Collect.ChunkFile("empty.txt", "text/plain", Array.Empty<byte>());
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkFile_OneAndAHalfChunks_TwoChunksLastTerminal()
    {
        const int chunkSize = 512 * 1024;
        var data = new byte[chunkSize + 1024]; // 1.5 chunks
        var chunks = Collect.ChunkFile("blob.bin", "application/octet-stream", data);
        Assert.Equal(2, chunks.Count);
        Assert.False(chunks[0].Terminal);
        Assert.True(chunks[1].Terminal);
        Assert.Equal(0UL, chunks[0].Sequence);
        Assert.Equal(1UL, chunks[1].Sequence);
    }
}
