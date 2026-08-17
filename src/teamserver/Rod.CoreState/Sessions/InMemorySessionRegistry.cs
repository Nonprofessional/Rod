using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Sessions;

/// <summary>
/// In-memory <see cref="ISessionRegistry"/> by default -- the durable Postgres
/// pair replaces it when configured. Sessions live in a process-local map keyed
/// by session id; implant- and engagement-scoped queries filter that map. A
/// session is the implant's live channel, not one TCP connection:
/// <see cref="OpenAsync"/> reuses the implant's active session when one exists
/// (a reconnect -- a poll check-in or a flapped stream -- refreshes capabilities
/// and last-seen) and only opens a new entity after the prior session closed.
/// State is lost on restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemorySessionRegistry : ISessionRegistry
{
    private readonly ConcurrentDictionary<SessionId, Session> _sessions = new();

    public Task<Session> OpenAsync(
        Implant implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // Open-or-reuse: a session is the implant's live channel, not one TCP
        // connection, so a reconnect (poll check-in, flapped stream) reuses the
        // active session -- refreshing its capabilities and last-seen -- instead
        // of churning a new session entity and a SessionOpened audit record per
        // connection. Only a closed session (staleness sweep, retirement,
        // explicit close) makes the next open a genuinely new one.
        var priorActive = _sessions.Values.FirstOrDefault(s =>
            s.ImplantId == implant.Id && s.Status == SessionStatus.Active);
        if (priorActive is not null)
        {
            priorActive.Touch(capabilities, at);
            return Task.FromResult(priorActive);
        }

        var session = Session.Open(SessionId.New(), implant.Id, implant.EngagementId, capabilities, at);
        _sessions[session.Id] = session;
        return Task.FromResult(session);
    }

    public Task TouchAsync(
        ImplantId implant,
        IReadOnlyCollection<string> capabilities,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var active = _sessions.Values.FirstOrDefault(s =>
            s.ImplantId == implant && s.Status == SessionStatus.Active);
        active?.Touch(capabilities, at);
        return Task.CompletedTask;
    }

    public Task CloseAsync(SessionId session, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found) && found.Status == SessionStatus.Active)
            found.Close(at);
        return Task.CompletedTask;
    }

    public Task<Session?> FindAsync(SessionId session, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(session, out var found) ? found : null);

    public Task<Session?> GetActiveAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        var active = _sessions.Values.FirstOrDefault(s =>
            s.ImplantId == implant && s.Status == SessionStatus.Active);
        return Task.FromResult(active);
    }

    public Task<IReadOnlyList<Session>> ListActiveAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var matches = _sessions.Values
            .Where(s => s.EngagementId == engagement && s.Status == SessionStatus.Active)
            .OrderBy(s => s.StartedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Session>>(matches);
    }

    public Task<IReadOnlyList<Session>> ListByImplantAsync(
        ImplantId implant,
        CancellationToken cancellationToken = default)
    {
        var matches = _sessions.Values
            .Where(s => s.ImplantId == implant)
            .OrderBy(s => s.StartedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Session>>(matches);
    }

    public Task<IReadOnlyList<Session>> SweepStaleAsync(
        DateTimeOffset cutoff,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // Close every Active session that has gone silent past the cutoff. Each
        // close is idempotent at the entity level; the status filter here makes
        // the sweep itself safe to run concurrently with a reconnect close.
        var closed = _sessions.Values
            .Where(s => s.Status == SessionStatus.Active && s.LastSeenAt < cutoff)
            .OrderBy(s => s.StartedAt)
            .ToArray();
        foreach (var session in closed)
            session.Close(at);

        return Task.FromResult<IReadOnlyList<Session>>(closed);
    }
}
