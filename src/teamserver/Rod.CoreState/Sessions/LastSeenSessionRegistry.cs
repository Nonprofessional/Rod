using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Sessions;

/// <summary>
/// The last-seen decorator over <see cref="ISessionRegistry"/>: every session
/// Open, Touch, and Close -- including the staleness sweep's implicit closes
/// -- also advances the implant's durable <see cref="Implant.LastSeenAt"/>
/// stamp. The registry's own session records answer "when did we last hear
/// from it" only while anyone remembers the session; the implant row's stamp
/// is what an operator reads after the beacon went dark ("when did we last
/// see this host").
/// </summary>
/// <remarks>
/// Composition wraps whichever registry the host runs (in-memory by default,
/// Postgres when configured), so every transport's check-in path records the
/// heartbeat without any of them knowing about it. The stamp is advisory: a
/// failure to persist it never fails the check-in it decorated -- the session
/// registry's own result is the authoritative one for presence.
/// </remarks>
public sealed class LastSeenSessionRegistry : ISessionRegistry
{
    private readonly ISessionRegistry _inner;
    private readonly IImplantRepository _implants;

    public LastSeenSessionRegistry(ISessionRegistry inner, IImplantRepository implants)
    {
        _inner = inner;
        _implants = implants;
    }

    public async Task<Session> OpenAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var session = await _inner.OpenAsync(implant, capabilities, at, cancellationToken);
        await NoteSeenAsync(implant.Id, at, cancellationToken);
        return session;
    }

    public async Task TouchAsync(
        ImplantId implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await _inner.TouchAsync(implant, capabilities, at, cancellationToken);
        await NoteSeenAsync(implant, at, cancellationToken);
    }

    public async Task CloseAsync(
        SessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await _inner.CloseAsync(session, at, cancellationToken);
        // The closed session record still names its implant; the close itself
        // was the last contact.
        var closed = await _inner.FindAsync(session, cancellationToken);
        if (closed is not null)
            await NoteSeenAsync(closed.ImplantId, at, cancellationToken);
    }

    public Task<Session?> FindAsync(SessionId session, CancellationToken cancellationToken = default)
        => _inner.FindAsync(session, cancellationToken);

    public Task<Session?> GetActiveAsync(ImplantId implant, CancellationToken cancellationToken = default)
        => _inner.GetActiveAsync(implant, cancellationToken);

    public Task<IReadOnlyList<Session>> ListActiveAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
        => _inner.ListActiveAsync(engagement, cancellationToken);

    public Task<IReadOnlyList<Session>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
        => _inner.ListByImplantAsync(implant, cancellationToken);

    public async Task<IReadOnlyList<Session>> SweepStaleAsync(
        DateTimeOffset cutoff,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var swept = await _inner.SweepStaleAsync(cutoff, at, cancellationToken);
        // A swept session died silently; the sweep moment is the best
        // last-heard stamp the implant will ever get.
        foreach (var session in swept)
            await NoteSeenAsync(session.ImplantId, at, cancellationToken);
        return swept;
    }

    // Loads, stamps, and saves -- only when the entity's own throttle let the
    // stamp move. Best-effort by design: presence does not depend on it.
    private async Task NoteSeenAsync(ImplantId implant, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            var record = await _implants.FindAsync(implant, cancellationToken);
            if (record is null || !record.NoteSeen(at))
                return;
            await _implants.SaveAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The durable stamp is advisory; the check-in it decorated
            // already succeeded.
        }
    }
}
