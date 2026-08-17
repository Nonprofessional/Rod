using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.Sessions;

/// <summary>
/// In-memory <see cref="ISessionRegistry"/> by default.
/// -- no Postgres yet. Sessions live in a process-local map keyed by session id;
/// implant- and engagement-scoped queries filter that map. An implant holds at
/// most one active session at a time, so opening a new one closes the prior
/// active session for that implant first. State is lost on restart; the port
/// keeps callers agnostic to that.
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
        // An implant holds at most one active session; a reconnect closes the
        // prior active session for that implant before opening the new one.
        var priorActive = _sessions.Values.FirstOrDefault(s =>
            s.ImplantId == implant.Id && s.Status == SessionStatus.Active);
        priorActive?.Close(at);

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
