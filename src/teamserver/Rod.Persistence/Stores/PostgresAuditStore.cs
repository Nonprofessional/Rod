using Microsoft.EntityFrameworkCore;
using Rod.Audit;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IAuditStore"/> (ADR 0003, roadmap M10.1 Phase 4).
/// Each appended event is stamped onto its engagement's hash chain and written
/// as one row to <c>audit_events</c>, so the trail outlives a teamserver restart.
/// The hash math stays in <see cref="AuditChain"/> (storage-agnostic); this
/// adapter only recovers the per-engagement chain head and persists the chained
/// event.
/// </summary>
/// <remarks>
/// <para>
/// Chain head recovery and the append+head-advance are the audit analogues of
/// the in-memory and file stores' locked append. The per-engagement chain head
/// is the <c>hash</c> of that engagement's highest-<c>append_sequence</c> row;
/// when no rows exist the head is <see cref="AuditChain.GenesisHash"/>. Each
/// append runs in one transaction that takes a transaction-scoped Postgres
/// advisory lock keyed on the engagement, so concurrent appends to one
/// engagement serialize exactly as the in-memory store's process-wide lock does
/// -- but per-engagement, so two engagements never block each other. Inside the
/// lock the adapter reads the current head, derives the next sequence, stamps
/// the event with <see cref="AuditChain.Chain"/>, and inserts it. A duplicate
/// <c>EventId</c> is refused inside the same lock, matching the existing stores.
/// </para>
/// <para>
/// The <c>append_sequence</c> column is what makes the head recoverable from any
/// row set (the file store gets "last" for free from append order on disk; the
/// durable store derives it from the max sequence). Reads (<see cref="ListAsync"/>,
/// <see cref="ForTaskAsync"/>) order by it so a reloaded trail matches append
/// order, which is what <see cref="AuditChain.VerifyTrail"/> walks.
/// </para>
/// </remarks>
internal sealed class PostgresAuditStore : IAuditStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresAuditStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task AppendAsync(AuditEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // A transaction-scoped advisory lock keyed on the engagement serializes
        // appends to one chain the way the in-memory store's process-wide lock
        // does, but per-engagement: two engagements append concurrently, two
        // appends to the same engagement serialize. The lock auto-releases on
        // commit/rollback. The key is the engagement Guid's stable hash so each
        // engagement maps to one lock class.
        var lockKey = StableEngagementKey(@event.EngagementId);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        // Append-only: an EventId already present is refused, exactly as the
        // in-memory and file stores do. The check runs under the lock so two
        // concurrent appends of the same id cannot both pass.
        var exists = await db.AuditEvents.AnyAsync(e => e.EventId == @event.EventId, cancellationToken);
        if (exists)
            throw new InvalidOperationException(
                $"Audit event {@event.EventId} is already appended; the audit trail is append-only.");

        // Recover the head: the hash of this engagement's highest-sequence row,
        // or the genesis hash when the chain is new. The sequence also advances
        // by one so the next append recovers this row as the head.
        var head = await db.AuditEvents
            .Where(e => e.EngagementId == @event.EngagementId)
            .OrderByDescending(e => EF.Property<long>(e, AppendSequenceShadow))
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(cancellationToken);
        var previousHash = head is null || head.Length == 0 ? AuditChain.GenesisHash : head;

        var maxSeq = await db.AuditEvents
            .Where(e => e.EngagementId == @event.EngagementId)
            .Select(e => (long?)EF.Property<long>(e, AppendSequenceShadow))
            .MaxAsync(cancellationToken) ?? 0;

        var chained = AuditChain.Chain(@event, previousHash);

        var entry = db.AuditEvents.Add(chained);
        entry.Property(AppendSequenceShadow).CurrentValue = maxSeq + 1;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<AuditEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // Append order (the sequence) is the chain order; ordering reads by it
        // keeps a reloaded trail in the order VerifyTrail walks.
        return await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => EF.Property<long>(e, AppendSequenceShadow))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId)
            .OrderBy(e => EF.Property<long>(e, AppendSequenceShadow))
            .ToArrayAsync(cancellationToken);
    }

    // The shadow property name on AuditEvent (see AuditEventConfiguration). Kept
    // here as a private constant so the queries reference one symbol.
    private const string AppendSequenceShadow = "AppendSequence";

    // A stable int4 key for the advisory lock, derived from the engagement id.
    // GetHashCode is not stable across processes/architectures, so hash the
    // Guid's bytes explicitly to a 32-bit value Npgsql's pg_advisory_xact_lock
    // (single int4 form) accepts.
    private static int StableEngagementKey(Guid engagementId)
    {
        var bytes = engagementId.ToByteArray();
        return BitConverter.ToInt32(bytes, 0) ^ BitConverter.ToInt32(bytes, 4)
            ^ BitConverter.ToInt32(bytes, 8) ^ BitConverter.ToInt32(bytes, 12);
    }
}
