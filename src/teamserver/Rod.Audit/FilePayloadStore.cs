using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Rod.Audit;

/// <summary>
/// Durable <see cref="IPayloadStore"/> (architecture.md Sec 6): each payload's
/// bytes are written to <c>payload-blobs/{payloadId}</c> under the data
/// directory and a metadata record (no bytes) is appended to
/// <c>payloads.jsonl</c>, so a built payload survives a teamserver restart
/// alongside the audit trail and artifacts. Same write shape as
/// <see cref="FileArtifactStore"/>: blob first, metadata line second, so a
/// reader that observes the metadata always finds the bytes on disk.
/// </summary>
public sealed class FilePayloadStore : IPayloadStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _dataDirectory;
    private readonly string _blobsDirectory;
    private readonly string _payloadsPath;

    // The metadata index, keyed by payload id. Lazily recovered from
    // payloads.jsonl on first read so a fresh process can serve payloads a
    // previous one stored without re-scanning the file for every lookup.
    private readonly ConcurrentDictionary<Guid, PayloadRecord> _index = new();
    private readonly Lock _recoverLock = new();
    private bool _indexRecovered;

    public FilePayloadStore(AuditPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            throw new ArgumentException(
                $"{nameof(AuditPersistenceOptions.DataDirectory)} must be set for the durable payload store.",
                nameof(options));

        _dataDirectory = options.DataDirectory;
        _blobsDirectory = Path.Combine(_dataDirectory, "payload-blobs");
        _payloadsPath = Path.Combine(_dataDirectory, "payloads.jsonl");
    }

    public Task SaveAsync(PayloadRecord payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        EnsureRecovered();

        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_blobsDirectory);

        var blobPath = BlobPath(payload.PayloadId);
        File.WriteAllBytes(blobPath, payload.Content);

        using var stream = new FileStream(
            _payloadsPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: false);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.WriteLine(JsonSerializer.Serialize(payload, AuditJsonContext.Default.PayloadRecord));
        writer.Flush();

        _index[payload.PayloadId] = payload;
        return Task.CompletedTask;
    }

    public async Task<PayloadRecord?> FindAsync(Guid payloadId, Guid engagementId, CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        if (!_index.TryGetValue(payloadId, out var metadata) || metadata.EngagementId != engagementId)
            return null;

        var blobPath = BlobPath(metadata.PayloadId);
        if (!File.Exists(blobPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken).ConfigureAwait(false);
        return metadata with { Content = bytes };
    }

    private string BlobPath(Guid payloadId) => Path.Combine(_blobsDirectory, payloadId.ToString("N"));

    // Recovers the metadata index from payloads.jsonl exactly once, on first
    // read or save. The bytes stay on disk and are loaded on demand, so only
    // the index is rebuilt here. File existence, not length, signals "never
    // written".
    private void EnsureRecovered()
    {
        if (_indexRecovered)
            return;

        lock (_recoverLock)
        {
            if (_indexRecovered)
                return;

            if (File.Exists(_payloadsPath))
            {
                foreach (var line in File.ReadLines(_payloadsPath))
                {
                    if (line.Length == 0)
                        continue;

                    var payload = JsonSerializer.Deserialize(line, AuditJsonContext.Default.PayloadRecord);
                    if (payload is not null)
                        _index[payload.PayloadId] = payload;
                }
            }

            _indexRecovered = true;
        }
    }
}