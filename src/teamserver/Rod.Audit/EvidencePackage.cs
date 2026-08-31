using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rod.Audit;

/// <summary>
/// The close-out evidence package (architecture.md Sec 11, Sec 2 step 10): the
/// finished engagement's evidence as one ZIP that survives infrastructure
/// teardown -- the hash-chained audit trail, the artifacts, and the report --
/// with a manifest that pins every file's bytes so the package re-verifies
/// offline on a host with no Rod infrastructure running.
/// </summary>
/// <remarks>
/// Layout, in a fixed order:
/// <list type="bullet">
/// <item><c>audit.jsonl</c> -- the engagement's full trail, oldest first, one
/// chained <see cref="AuditEvent"/> per line in the same encoding the file
/// store writes, so a package trail and a store trail are byte-identical
/// encodings of the same facts.</item>
/// <item><c>artifacts.jsonl</c> -- the engagement's artifacts, one
/// <see cref="Artifact"/> per line (content included), same encoding as the
/// artifact store's metadata index.</item>
/// <item>the documents the caller attaches -- the report as JSON and Markdown
/// -- each at the path the caller names.</item>
/// <item><c>manifest.json</c> -- written last: the engagement header, the
/// event/artifact counts, and every other file's size and SHA-256. The digest
/// of the manifest itself is what the <c>EvidenceExported</c> audit event
/// records, binding the exported package into the live trail.</item>
/// </list>
///
/// Verification recomputes every digest from the bytes, re-runs
/// <see cref="AuditChain.VerifyTrail"/> over the decoded trail, and checks the
/// artifact records against their own recorded sizes -- the same tamper checks
/// the live report path applies, plus package-level byte-exactness. The JSON
/// form is storage encoding only; the chain hash stays the hand-built canonical
/// join, so a package written by one build verifies on any other.
/// </remarks>
public static class EvidencePackage
{
    public const string ManifestPath = "manifest.json";
    public const string AuditPath = "audit.jsonl";
    public const string ArtifactsPath = "artifacts.jsonl";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Writes the package to <paramref name="output"/>. The trail and artifacts
    /// are encoded exactly as the durable stores encode them; the
    /// <paramref name="documents"/> (the report renderings) join under their
    /// given paths. Entry order and entry timestamps are fixed by
    /// <paramref name="header"/>, so two exports of identical state carry
    /// identical content under identical pinned digests.
    /// </summary>
    public static async Task<EvidencePackageManifest> WriteAsync(
        Stream output,
        EvidencePackageHeader header,
        IReadOnlyList<AuditEvent> trail,
        IReadOnlyList<Artifact> artifacts,
        IReadOnlyList<KeyValuePair<string, byte[]>> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(documents);

        var pending = new List<PendingFile>
        {
            FileEntry(AuditPath, EncodeTrail(trail)),
            FileEntry(ArtifactsPath, EncodeArtifacts(artifacts)),
        };
        foreach (var document in documents)
            pending.Add(FileEntry(document.Key, document.Value));

        // The manifest pins every other file's digest; the returned manifest
        // carries the same clean records the archive holds.
        var pinned = pending.Select(f => new EvidencePackageFile(f.Path, f.Size, f.Sha256)).ToArray();
        var manifest = new EvidencePackageManifest(header, trail.Count, artifacts.Count, pinned);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AuditJsonContext.Default.EvidencePackageManifest);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in pending)
            await WriteEntryAsync(archive, file.Path, file.Payload, header.ExportedAt, cancellationToken).ConfigureAwait(false);
        // The manifest goes in last, after every digest it pins is final.
        await WriteEntryAsync(archive, ManifestPath, manifestBytes, header.ExportedAt, cancellationToken).ConfigureAwait(false);

        return manifest;
    }

    /// <summary>
    /// Verifies a package byte-exact against its own manifest and chain. A
    /// package verifies only when every file matches its recorded digest and
    /// size, the archive holds exactly the manifest's files and nothing else,
    /// the decoded trail re-verifies through <see cref="AuditChain.VerifyTrail"/>
    /// and belongs to the header's engagement, and every artifact record agrees
    /// with its recorded size. The returned record names the first failure
    /// otherwise; on success it carries the manifest. A package that is not a
    /// readable archive at all -- truncated, corrupted, not a ZIP -- is a
    /// verification failure, not an exception: the caller is an operator-facing
    /// command that must say what went wrong, not stack-trace.
    /// </summary>
    public static async Task<EvidencePackageVerification> VerifyAsync(
        Stream package,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await VerifyContentsAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ObjectDisposedException)
        {
            return EvidencePackageVerification.Fail($"the package is not readable: {ex.Message}");
        }
    }

    private static async Task<EvidencePackageVerification> VerifyContentsAsync(
        Stream package,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);

        if (!entries.TryGetValue(ManifestPath, out var manifestEntry))
            return EvidencePackageVerification.Fail("the package carries no manifest.json");

        EvidencePackageManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            try
            {
                manifest = await JsonSerializer.DeserializeAsync(
                    manifestStream, AuditJsonContext.Default.EvidencePackageManifest, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("manifest.json is null");
            }
            catch (JsonException ex)
            {
                return EvidencePackageVerification.Fail($"manifest.json does not parse: {ex.Message}");
            }
        }

        // Exactly the manifest's files, plus the manifest itself -- nothing
        // extra smuggled in, nothing listed missing.
        var expected = manifest.Files.Select(f => f.Path).Append(ManifestPath).Order(StringComparer.Ordinal).ToArray();
        var actual = entries.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual))
            return EvidencePackageVerification.Fail(
                $"the archive contents do not match the manifest: expected [{string.Join(", ", expected)}], found [{string.Join(", ", actual)}]");

        foreach (var file in manifest.Files)
        {
            var entry = entries[file.Path];
            if (entry.Length != file.Size)
                return EvidencePackageVerification.Fail(
                    $"{file.Path}: size {entry.Length} does not match the manifest's {file.Size}");
            using var stream = entry.Open();
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (digest != file.Sha256)
                return EvidencePackageVerification.Fail(
                    $"{file.Path}: sha256 {digest} does not match the manifest's {file.Sha256}");
        }

        if (!entries.TryGetValue(AuditPath, out var auditEntry))
            return EvidencePackageVerification.Fail("the manifest lists no audit.jsonl");
        var trail = new List<AuditEvent>();
        using (var auditStream = auditEntry.Open())
        {
            using var reader = new StreamReader(auditStream, Utf8NoBom);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                    continue;
                AuditEvent? decoded;
                try
                {
                    decoded = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent);
                }
                catch (JsonException ex)
                {
                    return EvidencePackageVerification.Fail($"audit.jsonl: an event does not parse: {ex.Message}");
                }
                if (decoded is null)
                    return EvidencePackageVerification.Fail("audit.jsonl: an event line decodes to null");
                trail.Add(decoded);
            }
        }

        if (trail.Count != manifest.EventCount)
            return EvidencePackageVerification.Fail(
                $"audit.jsonl carries {trail.Count} events; the manifest records {manifest.EventCount}");

        // The same tamper check the live report path applies before rendering.
        if (AuditChain.VerifyTrail(trail) is { } chainBreak)
            return EvidencePackageVerification.Fail($"audit.jsonl: the hash chain breaks: {chainBreak}");

        if (trail.Any(e => e.EngagementId != manifest.Header.EngagementId))
            return EvidencePackageVerification.Fail(
                "audit.jsonl carries events from a different engagement than the manifest header");

        if (!entries.TryGetValue(ArtifactsPath, out var artifactsEntry))
            return EvidencePackageVerification.Fail("the manifest lists no artifacts.jsonl");
        var artifactCount = 0;
        using (var artifactsStream = artifactsEntry.Open())
        {
            using var reader = new StreamReader(artifactsStream, Utf8NoBom);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                    continue;
                Artifact? artifact;
                try
                {
                    artifact = JsonSerializer.Deserialize(line, AuditJsonContext.Default.Artifact);
                }
                catch (JsonException ex)
                {
                    return EvidencePackageVerification.Fail($"artifacts.jsonl: a record does not parse: {ex.Message}");
                }
                if (artifact is null)
                    return EvidencePackageVerification.Fail("artifacts.jsonl: a record line decodes to null");
                artifactCount++;
                if (artifact.Content.Length != artifact.Size)
                    return EvidencePackageVerification.Fail(
                        $"artifacts.jsonl: artifact {artifact.ArtifactId} records size {artifact.Size} but carries {artifact.Content.Length} bytes");
                if (artifact.EngagementId != manifest.Header.EngagementId)
                    return EvidencePackageVerification.Fail(
                        $"artifacts.jsonl: artifact {artifact.ArtifactId} belongs to a different engagement than the manifest header");
            }
        }

        if (artifactCount != manifest.ArtifactCount)
            return EvidencePackageVerification.Fail(
                $"artifacts.jsonl carries {artifactCount} records; the manifest records {manifest.ArtifactCount}");

        return new EvidencePackageVerification(true, null, manifest);
    }

    // The store encodings, reused verbatim so a package's audit.jsonl is the
    // same line-per-record form the file-backed trail writes.
    private static byte[] EncodeTrail(IReadOnlyList<AuditEvent> trail)
    {
        var sb = new StringBuilder();
        foreach (var @event in trail)
        {
            sb.Append(JsonSerializer.Serialize(@event, AuditJsonContext.Default.AuditEvent));
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] EncodeArtifacts(IReadOnlyList<Artifact> artifacts)
    {
        var sb = new StringBuilder();
        foreach (var artifact in artifacts)
        {
            sb.Append(JsonSerializer.Serialize(artifact, AuditJsonContext.Default.Artifact));
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static PendingFile FileEntry(string path, byte[] payload)
        => new(path, payload.Length, Convert.ToHexString(SHA256.HashData(payload)), payload);

    // The ZIP container cannot represent times before 1980-01-01; the export
    // time is clamped to that floor so any header date writes (deterministic:
    // identical state clamps identically).
    private static readonly DateTimeOffset ZipEpochFloor = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task WriteEntryAsync(
        ZipArchive archive, string path, byte[] payload, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = at < ZipEpochFloor ? ZipEpochFloor : at;
        await using var stream = entry.Open();
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    // Carries the payload alongside the fields the manifest pins, so the writer
    // both records the digest and emits the bytes from one pass.
    private sealed record PendingFile(string Path, long Size, string Sha256, byte[] Payload);
}

/// <summary>The engagement a package exports, and who exported it when.</summary>
public sealed record EvidencePackageHeader(
    Guid EngagementId,
    string EngagementName,
    Guid ExportedBy,
    DateTimeOffset ExportedAt);

/// <summary>One pinned file in the package: its path, byte size, and SHA-256 hex digest.</summary>
public sealed record EvidencePackageFile(string Path, long Size, string Sha256);

/// <summary>
/// The package manifest: the engagement header, the event and artifact counts,
/// and the digest of every other file in the archive. The manifest itself is
/// the last entry written; its own digest is what the
/// <c>EvidenceExported</c> audit event records.
/// </summary>
public sealed record EvidencePackageManifest(
    EvidencePackageHeader Header,
    int EventCount,
    int ArtifactCount,
    IReadOnlyList<EvidencePackageFile> Files);

/// <summary>
/// The offline verification result. <see cref="Verified"/> is the verdict;
/// <see cref="Failure"/> names the first broken check when it is false, and
/// <see cref="Manifest"/> carries the verified manifest on success.
/// </summary>
public sealed record EvidencePackageVerification(bool Verified, string? Failure, EvidencePackageManifest? Manifest)
{
    internal static EvidencePackageVerification Fail(string failure) => new(false, failure, null);
}
