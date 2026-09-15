using Rod.CoreState;
using Microsoft.EntityFrameworkCore;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.ShellSessions;

namespace Rod.Persistence.Stores;

/// <summary>
/// The durable <see cref="IShellSessionRegistry"/> pair (architecture.md
/// Sec 8): shell sessions live in Postgres, so the engagement's caught-shell
/// history survives a restart. Mutations reload the stored row and apply the
/// entity's own transition, so the same no-op-on-ended semantics the
/// in-memory registry guarantees hold here too; the returned entities are
/// detached copies, which is why the surface re-reads through
/// <see cref="FindAsync"/> wherever it needs current state rather than
/// holding an open-time reference.
/// </summary>
internal sealed class PostgresShellSessionRegistry : IShellSessionRegistry
{
    private readonly IDbContextFactory<RodPersistenceDbContext> _factory;

    public PostgresShellSessionRegistry(IDbContextFactory<RodPersistenceDbContext> factory)
        => _factory = factory;

    public async Task<ShellSession> OpenAsync(
        EngagementId engagement,
        Guid listenerId,
        string remoteAddress,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var session = ShellSession.Open(ShellSessionId.New(), engagement, listenerId, remoteAddress, at);
        db.ShellSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task MarkFingerprintAsync(
        ShellSessionId session,
        ShellOsGuess os,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.MarkFingerprint(os);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task NoteInputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.NoteInput(at);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task NoteOutputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.NoteOutput(at);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkLostAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.MarkLost(at);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task CloseAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.Close(at);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task BindUpgradeAsync(
        ShellSessionId session,
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken) is { } found)
        {
            found.BindUpgrade(implant);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<ShellSession?> FindAsync(
        ShellSessionId session,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.ShellSessions.FindAsync(new object[] { session }, cancellationToken);
    }

    public async Task<IReadOnlyList<ShellSession>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var listed = await db.ShellSessions
            .Where(s => s.EngagementId == engagement)
            .OrderBy(s => s.OpenedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        return listed;
    }
}
