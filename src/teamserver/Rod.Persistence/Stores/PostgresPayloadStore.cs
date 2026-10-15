using Microsoft.EntityFrameworkCore;
using Rod.Audit;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IPayloadStore"/>: the built-payload library
/// as rows on <c>payloads</c>, so a stored artifact, its loader delivery
/// route, and its sealed-contact key binding all survive a teamserver
/// restart. Behind the same port the in-memory and file adapters serve; the
/// bake-time snapshot rides as a JSON column through <see cref="StoredPayload"/>'s
/// conversion. Deleting a payload removes the row and with it the envelope
/// key -- the artifact's envelopes stop being decodable, exactly the
/// lifetime the record documents.
/// </summary>
internal sealed class PostgresPayloadStore : IPayloadStore
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresPayloadStore(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task SaveAsync(PayloadRecord payload, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Payloads.FindAsync(new object?[] { payload.PayloadId }, cancellationToken);
        if (existing is null)
            db.Payloads.Add(StoredPayload.From(payload));
        else
            db.Entry(existing).CurrentValues.SetValues(StoredPayload.From(payload));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PayloadRecord?> FindAsync(
        Guid payloadId,
        Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Payloads.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PayloadId == payloadId && p.EngagementId == engagementId, cancellationToken);
        return stored?.ToRecord();
    }

    public async Task<PayloadRecord?> FindByIdAsync(Guid payloadId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Payloads.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PayloadId == payloadId, cancellationToken);
        return stored?.ToRecord();
    }

    public async Task<IReadOnlyList<PayloadRecord>> ListAsync(
        Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // The library view is metadata-shaped at the port level; the bytes
        // ride along (the bytea read is one column on an already-read row)
        // and the transport layer never materializes them for a listing.
        var stored = await db.Payloads.AsNoTracking()
            .Where(p => p.EngagementId == engagementId)
            .OrderByDescending(p => p.BuiltAt)
            .ThenByDescending(p => p.PayloadId)
            .ToArrayAsync(cancellationToken);
        return stored.Select(p => p.ToRecord()).ToArray();
    }

    public async Task<PayloadRecord?> FindByEnvelopeKeyAsync(
        Guid envelopeKeyId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Payloads.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EnvelopeKeyId == envelopeKeyId, cancellationToken);
        return stored?.ToRecord();
    }

    public async Task<PayloadRecord?> FindByTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Payloads.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TokenId == tokenId, cancellationToken);
        return stored?.ToRecord();
    }

    public async Task<bool> RemoveAsync(
        Guid payloadId,
        Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Payloads
            .FirstOrDefaultAsync(p => p.PayloadId == payloadId && p.EngagementId == engagementId, cancellationToken);
        if (stored is null)
            return false;
        db.Payloads.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
