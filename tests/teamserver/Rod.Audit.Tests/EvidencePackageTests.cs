using System.IO.Compression;
using System.Text;

namespace Rod.Audit.Tests;

/// <summary>
/// The close-out evidence package (storage &amp; audit layer, architecture.md
/// Sec 11): a written package verifies offline -- byte-exact against its own
/// manifest, through the hash chain, and across the artifact records -- and
/// every way of tampering with it after the fact fails the verification naming
/// the file that changed.
/// </summary>
public class EvidencePackageTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static AuditEvent Linked(Guid engagement, int seed, string previousHash)
        => AuditChain.Chain(
            AuditEvent.Fact(
                eventId: Guid.NewGuid(),
                engagementId: engagement,
                operatorId: Guid.NewGuid(),
                implantId: Guid.NewGuid(),
                taskId: Guid.NewGuid(),
                verb: "shell.exec",
                kind: AuditEventKind.TaskCompleted,
                payload: $"arg-{seed}",
                output: "out",
                outcome: "Succeeded",
                at: T0.AddSeconds(seed)),
            previousHash);

    private static IReadOnlyList<AuditEvent> Trail(Guid engagement, int count)
    {
        var trail = new List<AuditEvent>(count);
        foreach (var seed in Enumerable.Range(0, count))
            trail.Add(Linked(engagement, seed, trail.Count == 0 ? AuditChain.GenesisHash : trail[^1].Hash));
        return trail;
    }

    private static Artifact Artifact(Guid engagement, int seed, byte[] content)
        => new(
            Guid.NewGuid(),
            engagement,
            Guid.NewGuid(),
            OperatorId: null,
            Name: $"loot-{seed}.txt",
            ContentType: "text/plain",
            Content: content,
            Size: content.Length,
            StoredAt: T0.AddMinutes(seed));

    private static EvidencePackageHeader Header(Guid engagement)
        => new(engagement, "rehearsal", Guid.NewGuid(), T0.AddDays(7));

    private static async Task<(byte[] Package, EvidencePackageManifest Manifest)> WritePackageAsync(
        Guid engagement, IReadOnlyList<AuditEvent>? trail = null, IReadOnlyList<Artifact>? artifacts = null)
    {
        var buffer = new MemoryStream();
        var manifest = await EvidencePackage.WriteAsync(
            buffer,
            Header(engagement),
            trail ?? Trail(engagement, 3),
            artifacts ?? new[] { Artifact(engagement, 0, Encoding.UTF8.GetBytes("evidence bytes")) },
            new List<KeyValuePair<string, byte[]>>
            {
                new("report.json", Encoding.UTF8.GetBytes("{\"report\":true}")),
                new("report.md", Encoding.UTF8.GetBytes("# report\n")),
            });
        return (buffer.ToArray(), manifest);
    }

    [Fact]
    public async Task Written_Package_Verifies_Offline()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        var verification = await EvidencePackage.VerifyAsync(new MemoryStream(package));

        Assert.True(verification.Verified, verification.Failure);
        Assert.NotNull(verification.Manifest);
        Assert.Equal(engagement, verification.Manifest!.Header.EngagementId);
        Assert.Equal(3, verification.Manifest.EventCount);
        Assert.Equal(1, verification.Manifest.ArtifactCount);
    }

    [Fact]
    public async Task Identical_State_Exports_Identical_Content()
    {
        var engagement = Guid.NewGuid();
        var trail = Trail(engagement, 3);
        var artifacts = new[] { Artifact(engagement, 0, Encoding.UTF8.GetBytes("evidence bytes")) };
        var header = Header(engagement);

        var first = new MemoryStream();
        var second = new MemoryStream();
        var firstManifest = await EvidencePackage.WriteAsync(first, header, trail, artifacts, Documents());
        var secondManifest = await EvidencePackage.WriteAsync(second, header, trail, artifacts, Documents());

        // Reproducibility at the evidence level: two exports of identical
        // state carry identical pinned digests (the manifest is the same
        // record), and each archive entry decompresses to the same bytes --
        // the deliverable can be diffed by content even where the ZIP
        // container framing itself is not byte-stable. (Files compares as a
        // sequence: the record's IReadOnlyList field would otherwise be
        // reference-compared.)
        Assert.Equal(firstManifest.Header, secondManifest.Header);
        Assert.Equal(firstManifest.EventCount, secondManifest.EventCount);
        Assert.Equal(firstManifest.ArtifactCount, secondManifest.ArtifactCount);
        Assert.Equal(firstManifest.Files, secondManifest.Files);
        await AssertEqualEntriesAsync(first, second);
    }

    private static async Task AssertEqualEntriesAsync(MemoryStream first, MemoryStream second)
    {
        first.Position = 0;
        second.Position = 0;
        using var left = new ZipArchive(first, ZipArchiveMode.Read);
        using var right = new ZipArchive(second, ZipArchiveMode.Read);
        Assert.Equal(left.Entries.Select(e => e.FullName), right.Entries.Select(e => e.FullName));
        foreach (var entry in left.Entries)
        {
            using var leftReader = entry.Open();
            using var leftBytes = new MemoryStream();
            await leftReader.CopyToAsync(leftBytes);
            using var peer = right.GetEntry(entry.FullName)!.Open();
            using var rightBytes = new MemoryStream();
            await peer.CopyToAsync(rightBytes);
            Assert.Equal(leftBytes.ToArray(), rightBytes.ToArray());
        }
    }

    [Fact]
    public async Task Flipped_Report_Byte_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        await using var tampered = await FlipByteAsync(package, "report.md");

        var verification = await EvidencePackage.VerifyAsync(tampered);
        Assert.False(verification.Verified);
        Assert.Contains("report.md", verification.Failure);
        Assert.Contains("sha256", verification.Failure);
    }

    [Fact]
    public async Task Rewritten_Trail_Event_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        // Rewrite an event line without recomputing its chain hash: the
        // manifest digest catches the byte change first; a matching digest with
        // a rewritten line is covered by the chain test below.
        await using var tampered = await ReplaceLineAsync(package, EvidencePackage.AuditPath, 1,
            line => line.Replace("arg-1", "arg-tampered"));

        var verification = await EvidencePackage.VerifyAsync(tampered);
        Assert.False(verification.Verified);
        Assert.Contains(EvidencePackage.AuditPath, verification.Failure);
    }

    [Fact]
    public async Task Resigned_Trail_Event_Breaks_The_Chain()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        // Tamper that also fixes the manifest: rebuild the package's audit
        // bytes but keep a forged event (a re-signed payload). Only the chain
        // check catches this -- the digest layer alone is not the evidence.
        var tampered = await ResignForgedTrailAsync(package, engagement);

        var verification = await EvidencePackage.VerifyAsync(tampered);
        Assert.False(verification.Verified);
        Assert.Contains("chain", verification.Failure);
    }

    [Fact]
    public async Task Missing_File_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        await using var stripped = await DropEntryAsync(package, "report.json");

        var verification = await EvidencePackage.VerifyAsync(stripped);
        Assert.False(verification.Verified);
        Assert.Contains("do not match the manifest", verification.Failure);
    }

    [Fact]
    public async Task Smuggled_Extra_File_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        await using var smuggled = await AddEntryAsync(package, "extra.exe", new byte[] { 1 });

        var verification = await EvidencePackage.VerifyAsync(smuggled);
        Assert.False(verification.Verified);
        Assert.Contains("do not match the manifest", verification.Failure);
    }

    [Fact]
    public async Task Artifact_Size_Mismatch_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("evidence bytes");
        var misrecorded = new[]
        {
            Artifact(engagement, 0, content) with { Size = content.Length + 1 },
        };

        var buffer = new MemoryStream();
        await EvidencePackage.WriteAsync(buffer, Header(engagement), Trail(engagement, 1), misrecorded, Documents());

        var verification = await EvidencePackage.VerifyAsync(buffer);
        Assert.False(verification.Verified);
        Assert.Contains("size", verification.Failure);
    }

    [Fact]
    public async Task Foreign_Engagement_Event_Fails_Verification()
    {
        var engagement = Guid.NewGuid();
        var foreign = Guid.NewGuid();

        // A trail whose links verify but whose middle event belongs to another
        // engagement: only the engagement-consistency check catches it, which
        // is why that check exists -- a chained trail is not, by itself, proof
        // the events are this engagement's.
        var buffer = new MemoryStream();
        await EvidencePackage.WriteAsync(
            buffer, Header(engagement), ForgedTrail(engagement, foreign), Array.Empty<Artifact>(), Documents());

        var verification = await EvidencePackage.VerifyAsync(buffer);
        Assert.False(verification.Verified);
        Assert.Contains("different engagement", verification.Failure);
    }

    // A chain that verifies link-by-link but carries another engagement's
    // event in the middle: the successor is re-chained onto the forged link so
    // the tamper check that remains is the engagement-consistency one.
    private static IReadOnlyList<AuditEvent> ForgedTrail(Guid engagement, Guid foreign)
    {
        var trail = Trail(engagement, 3);
        var middleFact = AuditEvent.Fact(
            trail[1].EventId, foreign, trail[1].OperatorId, trail[1].ImplantId,
            trail[1].TaskId, trail[1].Verb, trail[1].Kind, trail[1].Payload,
            trail[1].Output, trail[1].Outcome, trail[1].At);
        var middle = AuditChain.Chain(middleFact, trail[0].Hash);
        var lastFact = AuditEvent.Fact(
            trail[2].EventId, trail[2].EngagementId, trail[2].OperatorId, trail[2].ImplantId,
            trail[2].TaskId, trail[2].Verb, trail[2].Kind, trail[2].Payload,
            trail[2].Output, trail[2].Outcome, trail[2].At);
        return new[] { trail[0], middle, AuditChain.Chain(lastFact, middle.Hash) };
    }

    [Fact]
    public async Task Empty_Engagement_Package_Verifies()
    {
        // An engagement closed with no events and no artifacts still exports a
        // well-formed package: an empty trail verifies trivially.
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement, trail: Array.Empty<AuditEvent>(), artifacts: Array.Empty<Artifact>());

        var verification = await EvidencePackage.VerifyAsync(new MemoryStream(package));

        Assert.True(verification.Verified, verification.Failure);
        Assert.Equal(0, verification.Manifest!.EventCount);
        Assert.Equal(0, verification.Manifest.ArtifactCount);
    }

    [Theory]
    [InlineData(0)]    // truncated to nothing
    [InlineData(8)]    // truncated past the header
    public async Task Truncated_Package_Fails_Cleanly(int keep)
    {
        // A package that is not a readable archive is a verification failure,
        // not an exception: the verifier is an operator-facing command.
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        var verification = await EvidencePackage.VerifyAsync(
            new MemoryStream(package, 0, Math.Min(keep, package.Length)));

        Assert.False(verification.Verified);
        Assert.NotNull(verification.Failure);
    }

    [Fact]
    public async Task Corrupted_Entry_Bytes_Fail_Cleanly()
    {
        var engagement = Guid.NewGuid();
        var (package, _) = await WritePackageAsync(engagement);

        // Flip a byte inside the deflated payload region (middle of the
        // archive): the digest check fails, or the entry no longer decodes --
        // either way the verifier reports a failure rather than throwing.
        package[package.Length / 2] ^= 0xff;

        var verification = await EvidencePackage.VerifyAsync(new MemoryStream(package));

        Assert.False(verification.Verified);
        Assert.NotNull(verification.Failure);
    }

    private static List<KeyValuePair<string, byte[]>> Documents()
        => new()
        {
            new("report.json", Encoding.UTF8.GetBytes("{\"report\":true}")),
            new("report.md", Encoding.UTF8.GetBytes("# report\n")),
        };

    // Rebuilds the archive with one entry's bytes transformed.
    private static async Task<MemoryStream> WithEntryAsync(
        byte[] package, string entryName, Func<byte[], byte[]> transform)
    {
        var rebuilt = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(rebuilt, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = entry.Open();
                using var payload = new MemoryStream();
                await reader.CopyToAsync(payload);
                var bytes = entry.FullName == entryName ? transform(payload.ToArray()) : payload.ToArray();
                var written = target.CreateEntry(entry.FullName);
                await using var writer = written.Open();
                await writer.WriteAsync(bytes);
            }
        }
        rebuilt.Position = 0;
        return rebuilt;
    }

    private static Task<MemoryStream> FlipByteAsync(byte[] package, string entryName)
        => WithEntryAsync(package, entryName, bytes =>
        {
            bytes[0] ^= 0xff;
            return bytes;
        });

    private static Task<MemoryStream> ReplaceLineAsync(
        byte[] package, string entryName, int lineIndex, Func<string, string> rewrite)
        => WithEntryAsync(package, entryName, bytes =>
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            lines[lineIndex] = rewrite(lines[lineIndex]);
            return Encoding.UTF8.GetBytes(string.Join('\n', lines));
        });

    // Rewrites one event payload AND re-stamps its hash with the predecessor's,
    // so the manifest digests are recomputed too: only the successor link's
    // PreviousHash mismatch remains to catch the forgery.
    private static async Task<MemoryStream> ResignForgedTrailAsync(byte[] package, Guid engagement)
    {
        var trail = Trail(engagement, 3);
        var forged = trail.ToArray();
        var tamperedFact = AuditEvent.Fact(
            forged[1].EventId, forged[1].EngagementId, forged[1].OperatorId, forged[1].ImplantId,
            forged[1].TaskId, forged[1].Verb, forged[1].Kind, "tampered-payload",
            forged[1].Output, forged[1].Outcome, forged[1].At);
        forged[1] = AuditChain.Chain(tamperedFact, forged[0].Hash);

        var buffer = new MemoryStream();
        await EvidencePackage.WriteAsync(buffer, Header(engagement), forged, Array.Empty<Artifact>(), Documents());
        buffer.Position = 0;
        return buffer;
    }

    private static async Task<MemoryStream> DropEntryAsync(byte[] package, string entryName)
    {
        var rebuilt = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(rebuilt, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.Where(e => e.FullName != entryName))
            {
                var clone = target.CreateEntry(entry.FullName);
                await using var reader = entry.Open();
                await using var writer = clone.Open();
                await reader.CopyToAsync(writer);
            }
        }
        rebuilt.Position = 0;
        return rebuilt;
    }

    private static async Task<MemoryStream> AddEntryAsync(byte[] package, string entryName, byte[] bytes)
    {
        var rebuilt = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(rebuilt, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var clone = target.CreateEntry(entry.FullName);
                await using var reader = entry.Open();
                await using var writer = clone.Open();
                await reader.CopyToAsync(writer);
            }
            var smuggled = target.CreateEntry(entryName);
            await using var smuggleWriter = smuggled.Open();
            await smuggleWriter.WriteAsync(bytes);
        }
        rebuilt.Position = 0;
        return rebuilt;
    }
}
