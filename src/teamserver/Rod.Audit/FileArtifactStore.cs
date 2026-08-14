using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Rod.Audit;

/// <summary>
/// Durable <see cref="IArtifactStore"/> for the walking skeleton (architecture.md
/// Sec 11; ). Each artifact's raw bytes are written to
/// <c>blobs/{artifactId}</c> under the data directory, and a metadata record (no
/// bytes) is appended to <c>artifacts.jsonl</c>, so evidence linked to a task
/// survives a teamserver restart and infrastructure teardown alongside the audit
/// trail. This is the file-backed stand-in for the eventual object store, behind
/// the same port the in-memory adapter serves.
///
/// Saving is a single blob write plus a single metadata-line append (no
/// cross-field atomicity to protect, so no lock is needed -- the same shape as
/// the in-memory adapter and the other read-mostly adapters). The metadata line
/// carries the engagement, task, operator, name, content type, size, and stored
/// time; the bytes are read back from the blob on demand. Engagement and task
/// scoping is the caller's discipline, filtered here as in memory.
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _dataDirectory;
    private readonly string _blobsDirectory;
    private readonly string _artifactsPath;

    // The metadata index, keyed by artifact id. Lazily recovered from
    // artifacts.jsonl on first read so a fresh process can serve artifacts a
    // previous one stored without re-scanning the file for every lookup.
    private readonly ConcurrentDictionary<Guid, Artifact> _index = new();
    private readonly Lock _recoverLock = new();
    private bool _indexRecovered;

    public FileArtifactStore(AuditPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            throw new ArgumentException(
                $"{nameof(AuditPersistenceOptions.DataDirectory)} must be set for the durable artifact store.",
                nameof(options));

        _dataDirectory = options.DataDirectory;
        _blobsDirectory = Path.Combine(_dataDirectory, "blobs");
        _artifactsPath = Path.Combine(_dataDirectory, "artifacts.jsonl");
    }

    public Task SaveAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        EnsureRecovered();

        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_blobsDirectory);

        // Write the blob first, then the metadata line. A reader that observes
        // the metadata line always finds the bytes already on disk; a crash
        // between the two leaves an orphan blob (no metadata pointing at it),
        // which is invisible to all reads and harmless.
        var blobPath = BlobPath(artifact.ArtifactId);
        File.WriteAllBytes(blobPath, artifact.Content);

        using var stream = new FileStream(
            _artifactsPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: false);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.WriteLine(JsonSerializer.Serialize(artifact, AuditJsonContext.Default.Artifact));
        writer.Flush();

        _index[artifact.ArtifactId] = artifact;
        return Task.CompletedTask;
    }

    public async Task<Artifact?> FindAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        if (_index.TryGetValue(artifactId, out var metadata))
            return await WithBytesAsync(metadata, cancellationToken).ConfigureAwait(false);

        return null;
    }

    public async Task<IReadOnlyList<Artifact>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        var matches = new List<Artifact>();
        foreach (var artifact in _index.Values.Where(a => a.TaskId == taskId).OrderBy(a => a.StoredAt))
            matches.Add(await WithBytesAsync(artifact, cancellationToken).ConfigureAwait(false));

        return matches;
    }

    public Task<ArtifactPage> ForTaskPageAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        // Metadata only -- the page surface carries no bytes, so this avoids
        // rehydrating every blob the way the full listing does.
        var ordered = _index.Values
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.StoredAt)
            .ThenBy(a => a.ArtifactId)
            .ToArray();
        var (items, next) = ListPageWindow.TakeNewest(
            ordered, limit, cursor, a => a.StoredAt, a => a.ArtifactId);
        return Task.FromResult(new ArtifactPage(items, next));
    }

    public async Task<IReadOnlyList<Artifact>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        var matches = new List<Artifact>();
        foreach (var artifact in _index.Values.Where(a => a.EngagementId == engagementId).OrderBy(a => a.StoredAt))
            matches.Add(await WithBytesAsync(artifact, cancellationToken).ConfigureAwait(false));

        return matches;
    }

    // Rehydrates an artifact from its metadata and the bytes in its blob. A
    // missing blob (orphaned by a crash between write and metadata append, or
    // removed out of band) yields an empty-content artifact rather than a throw
    // -- the evidence metadata is still on the trail.
    private async Task<Artifact> WithBytesAsync(Artifact metadata, CancellationToken cancellationToken)
    {
        var blobPath = BlobPath(metadata.ArtifactId);
        if (!File.Exists(blobPath))
            return metadata;

        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken).ConfigureAwait(false);
        return metadata with { Content = bytes };
    }

    private string BlobPath(Guid artifactId) => Path.Combine(_blobsDirectory, artifactId.ToString("N"));

    // Recovers the metadata index from artifacts.jsonl exactly once, on first
    // read or save. The bytes stay on disk and are loaded on demand, so only the
    // index is rebuilt here. File existence, not length, signals "never written".
    private void EnsureRecovered()
    {
        if (_indexRecovered)
            return;

        lock (_recoverLock)
        {
            if (_indexRecovered)
                return;

            if (File.Exists(_artifactsPath))
            {
                foreach (var line in File.ReadLines(_artifactsPath))
                {
                    if (line.Length == 0)
                        continue;

                    var artifact = JsonSerializer.Deserialize(line, AuditJsonContext.Default.Artifact);
                    if (artifact is not null)
                        _index[artifact.ArtifactId] = artifact;
                }
            }

            _indexRecovered = true;
        }
    }
}
