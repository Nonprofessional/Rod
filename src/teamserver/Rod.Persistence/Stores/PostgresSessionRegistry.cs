using Microsoft.EntityFrameworkCore;
using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Sessions;

namespace Rod.Persistence.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="ISessionRegistry"/> (ADR 0003). Each call
/// creates a short-lived <see cref="RodPersistenceDbContext"/> from the factory,
/// performs its work, and disposes the context; the context is never held across
/// calls so the singleton adapter stays safe for concurrent use.
/// </summary>
/// <remarks>
/// Mirrors the in-memory adapter's semantics exactly: a session is the
/// implant's live channel, not one TCP connection, so <see cref="OpenAsync"/>
/// reuses the implant's active session when one exists (refreshing capabilities
/// and last-seen) and only persists a new entity after the prior session
/// closed; <see cref="TouchAsync"/> and <see cref="CloseAsync"/> are silent
/// no-ops when there is nothing to act on (a stray keepalive after close, or a
/// duplicate close after a flap), so the entity's own "cannot be touched/closed
/// from Closed" exceptions are never reached through the adapter. The new
/// session id is generated server-side, as in the in-memory registry. The
/// "find, reuse-or-insert" sequence in <see cref="OpenAsync"/> runs inside one
/// transaction so two racing opens cannot leave two active sessions for the
/// same implant.
/// </remarks>
internal sealed class PostgresSessionRegistry : ISessionRegistry
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresSessionRegistry(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<Session> OpenAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Open-or-reuse: a reconnect refreshes the active session instead of
        // churning a new entity per connection (see the in-memory adapter).
        var priorActive = await db.Sessions
            .FirstOrDefaultAsync(s => s.ImplantId == implant.Id && s.Status == SessionStatus.Active, cancellationToken);
        if (priorActive is not null)
        {
            priorActive.Touch(capabilities, at);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return priorActive;
        }

        var session = Session.Open(SessionId.New(), implant.Id, implant.EngagementId, capabilities, at);
        db.Sessions.Add(session);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return session;
    }

    public async Task TouchAsync(
        ImplantId implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // No-op when the implant has no active session (a stray keepalive after
        // close), mirroring the in-memory adapter.
        var active = await db.Sessions
            .FirstOrDefaultAsync(s => s.ImplantId == implant && s.Status == SessionStatus.Active, cancellationToken);
        if (active is null)
            return;

        active.Touch(capabilities, at);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CloseAsync(SessionId session, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // No-op if unknown or already closed, so the entity's "cannot be closed
        // from Closed" exception is never reached through the adapter.
        var found = await db.Sessions.FindAsync(new object?[] { session }, cancellationToken);
        if (found is null || found.Status != SessionStatus.Active)
            return;

        found.Close(at);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Session?> FindAsync(SessionId session, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == session, cancellationToken);
    }

    public async Task<Session?> GetActiveAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ImplantId == implant && s.Status == SessionStatus.Active, cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> ListActiveAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // The online implants in an engagement are exactly their active sessions,
        // oldest-started first (the operator-visible "who is alive" view).
        return await db.Sessions
            .AsNoTracking()
            .Where(s => s.EngagementId == engagement && s.Status == SessionStatus.Active)
            .OrderBy(s => s.StartedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // All sessions (active and closed) for the implant, oldest first -- the
        // per-implant connection history.
        return await db.Sessions
            .AsNoTracking()
            .Where(s => s.ImplantId == implant)
            .OrderBy(s => s.StartedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> SweepStaleAsync(
        DateTimeOffset cutoff,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        // Every Active session that has gone silent past the cutoff. Loaded
        // tracked so the Close transitions persist in the same context; the
        // status filter makes a concurrent reconnect close harmless (the entity
        // refuses a second close, so a re-query would be needed -- the filter
        // keeps the sweep's own view correct instead).
        var stale = await db.Sessions
            .Where(s => s.Status == SessionStatus.Active && s.LastSeenAt < cutoff)
            .OrderBy(s => s.StartedAt)
            .ToArrayAsync(cancellationToken);
        foreach (var session in stale)
        {
            if (session.Status == SessionStatus.Active && session.LastSeenAt < cutoff)
                session.Close(at);
        }

        await db.SaveChangesAsync(cancellationToken);
        return stale.Where(s => s.Status == SessionStatus.Closed).ToArray();
    }
}
