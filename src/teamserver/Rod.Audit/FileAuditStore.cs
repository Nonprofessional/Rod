using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Rod.Audit;

/// <summary>
/// Durable <see cref="IAuditStore"/> by default. Each appended event is stamped onto its engagement's
/// hash chain and written as one JSON Lines record to <c>audit.jsonl</c> under
/// the data directory, so the trail outlives a teamserver restart and
/// infrastructure teardown -- the acceptance point. This is the file-backed
/// stand-in for the eventual Postgres-backed store, behind the same port the
/// in-memory adapter serves.
///
/// Append-only is honored exactly as in memory: the only mutation is
/// <see cref="AppendAsync"/>, the same EventId twice throws, and nothing removes
/// or rewrites a stored line. The append and the in-memory head advance are made
/// atomic by a lock (mirrors <see cref="InMemoryAuditStore"/>); the file line is
/// flushed within the lock so a crash after the lock releases cannot lose an
/// event the trail already committed to.
///
/// The JSON record is the *chained* event (it carries the stamped
/// <see cref="AuditEvent.PreviousHash"/> and <see cref="AuditEvent.Hash"/>), so a
/// reloaded trail round-trips through <see cref="AuditChain.VerifyTrail"/>
/// unchanged. The hash itself is never derived from JSON -- it is computed over
/// <see cref="AuditChain"/>'s hand-built canonical join before the line is
/// written -- so the storage encoding and the tamper-evident input stay
/// decoupled.
///
/// On first use the store recovers each engagement's chain head from any
/// existing <c>audit.jsonl</c>, so a restarted teamserver continues each
/// engagement's trail off its last stored event rather than restarting the chain.
/// Cross-engagement events never share a head, matching the per-engagement trail.
/// </summary>
public sealed class FileAuditStore : IAuditStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _dataDirectory;
    private readonly string _auditPath;
    private readonly Lock _appendLock = new();

    // The last hash per engagement, mirroring InMemoryAuditStore._heads. Lazily
    // recovered from audit.jsonl on first use so a fresh process continues each
    // engagement's chain off its stored predecessor.
    private readonly ConcurrentDictionary<Guid, string> _heads = new();
    // Every EventId this store has seen, recovered alongside the heads so a
    // duplicate EventId that landed before this process started is still refused.
    private readonly HashSet<Guid> _appendedIds = new();
    private bool _headsRecovered;

    public FileAuditStore(AuditPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            throw new ArgumentException(
                $"{nameof(AuditPersistenceOptions.DataDirectory)} must be set for the durable audit store.",
                nameof(options));

        _dataDirectory = options.DataDirectory;
        _auditPath = Path.Combine(_dataDirectory, "audit.jsonl");
    }

    public Task AppendAsync(AuditEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Append + head-advance + line-flush must be atomic: the chain commits
        // each event to its predecessor, so two concurrent appends to one
        // engagement must serialize, and the file must hold the line by the time
        // the lock releases. The directory is created lazily on first write.
        lock (_appendLock)
        {
            EnsureRecovered();

            if (_appendedIds.Contains(@event.EventId))
                throw new InvalidOperationException(
                    $"Audit event {@event.EventId} is already appended; the audit trail is append-only.");

            var previousHash = _heads.GetValueOrDefault(@event.EngagementId, AuditChain.GenesisHash);
            var chained = AuditChain.Chain(@event, previousHash);

            Directory.CreateDirectory(_dataDirectory);

            // Append the chained event as one JSON Lines record, flush inside the
            // lock so the trail on disk matches the in-memory head the moment the
            // lock releases. FileShare.Read lets a reviewer tail the file while
            // the teamserver runs; the writer leaves the stream open so the
            // FileShare applies to concurrent openers.
            using var stream = new FileStream(
                _auditPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: false);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.WriteLine(JsonSerializer.Serialize(chained, AuditJsonContext.Default.AuditEvent));
            writer.Flush();

            _heads[chained.EngagementId] = chained.Hash;
            _appendedIds.Add(chained.EventId);
        }

        return Task.CompletedTask;
    }

    public async Task<AuditEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await foreach (var @event in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (@event.EventId == eventId)
                return @event;
        }

        return null;
    }

    public async Task<IReadOnlyList<AuditEvent>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var matches = new List<AuditEvent>();
        await foreach (var @event in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (@event.TaskId == taskId)
                matches.Add(@event);
        }

        matches.Sort(static (a, b) => a.At.CompareTo(b.At));
        return matches;
    }

    public async Task<IReadOnlyList<AuditEvent>> ForImplantAsync(Guid implantId, CancellationToken cancellationToken = default)
    {
        var matches = new List<AuditEvent>();
        await foreach (var @event in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (@event.ImplantId == implantId)
                matches.Add(@event);
        }

        matches.Sort(static (a, b) => a.At.CompareTo(b.At));
        return matches;
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        // Refuse to serve a trail that failed chain verification: the evidence
        // deliverable must never silently present tampered records as intact.
        if (_brokenEngagements.Contains(engagementId))
            throw new InvalidOperationException(
                $"Audit chain verification failed for engagement {engagementId}; the trail is refused.");

        var matches = new List<AuditEvent>();
        await foreach (var @event in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (@event.EngagementId == engagementId)
                matches.Add(@event);
        }

        matches.Sort(static (a, b) => a.At.CompareTo(b.At));
        return matches;
    }

    public async Task<AuditPage> ListPageAsync(
        Guid engagementId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        EnsureRecovered();

        // Same refusal as ListAsync: a tampered trail is never served as
        // evidence, paged or not.
        if (_brokenEngagements.Contains(engagementId))
            throw new InvalidOperationException(
                $"Audit chain verification failed for engagement {engagementId}; the trail is refused.");

        var matches = new List<AuditEvent>();
        await foreach (var @event in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (@event.EngagementId == engagementId)
                matches.Add(@event);
        }

        // The event id breaks timestamp ties so a page boundary is stable even
        // when several events share one instant.
        matches.Sort(static (a, b) =>
        {
            var byAt = a.At.CompareTo(b.At);
            return byAt != 0 ? byAt : a.EventId.CompareTo(b.EventId);
        });
        var (items, next) = ListPageWindow.TakeNewest(
            matches.ToArray(), limit, cursor, e => e.At, e => e.EventId);
        return new AuditPage(items, next);
    }

    // Streams every stored event in append order (oldest first on disk). A
    // missing file is an empty trail, not an error -- the store has simply never
    // been written to. FileShare.ReadWrite lets a read coexist with an in-flight
    // append from this process.
    private async IAsyncEnumerable<AuditEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureRecovered();

        if (!File.Exists(_auditPath))
            yield break;

        using var stream = new FileStream(
            _auditPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
        {
            var @event = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent);
            if (@event is not null)
                yield return @event;
        }
    }

    // Recovers each engagement's chain head and the set of appended EventIds
    // from audit.jsonl exactly once, on first read or append. After this runs the
    // store behaves like the in-memory one -- new appends link off the recovered
    // head, and a duplicate EventId (one that landed before this process started)
    // is still refused. Recovery runs under the append lock so it cannot race an
    // append.
    //
    // Recovery also verifies every engagement's hash chain (architecture.md
    // Sec 11): the tamper-evidence property is exercised in production here, not
    // only in tests. A trail that fails verification is refused at read time --
    // serving tampered evidence would make the report deliverable itself
    // untrustworthy.
    private void EnsureRecovered()
    {
        if (_headsRecovered)
            return;

        lock (_appendLock)
        {
            if (_headsRecovered)
                return;

            if (File.Exists(_auditPath))
            {
                // The file is append-order, so a single pass collects each
                // engagement's trail oldest-first -- exactly the shape
                // AuditChain.VerifyTrail walks.
                var byEngagement = new Dictionary<Guid, List<AuditEvent>>();
                foreach (var line in File.ReadLines(_auditPath))
                {
                    if (line.Length == 0)
                        continue;

                    var @event = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent);
                    if (@event is null)
                        continue;

                    _heads[@event.EngagementId] = @event.Hash;
                    _appendedIds.Add(@event.EventId);

                    if (!byEngagement.TryGetValue(@event.EngagementId, out var trail))
                    {
                        trail = new List<AuditEvent>();
                        byEngagement[@event.EngagementId] = trail;
                    }
                    trail.Add(@event);
                }

                foreach (var (engagementId, trail) in byEngagement)
                {
                    if (AuditChain.VerifyTrail(trail) is { } breakAt)
                        _brokenEngagements.Add(engagementId);
                }
            }

            _headsRecovered = true;
        }
    }

    // Engagements whose recovered trail failed chain verification. Their reads
    // are refused (ListAsync throws) rather than served as evidence.
    private readonly HashSet<Guid> _brokenEngagements = new();
}
