using System.Collections.Concurrent;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;

namespace Rod.CoreState.ShellSessions;

/// <summary>
/// In-memory <see cref="IShellSessionRegistry"/> by default -- the durable
/// Postgres pair replaces it when configured. Sessions live in a
/// process-local map keyed by session id; the mutation calls resolve the
/// stored entity and act on it (each a no-op for an unknown or ended
/// session, so the socket read and write halves can race a disconnect
/// without a guard on either side). State is lost on restart; the port
/// keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryShellSessionRegistry : IShellSessionRegistry
{
    private readonly ConcurrentDictionary<ShellSessionId, ShellSession> _sessions = new();

    public Task<ShellSession> OpenAsync(
        EngagementId engagement,
        Guid listenerId,
        string remoteAddress,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var session = ShellSession.Open(ShellSessionId.New(), engagement, listenerId, remoteAddress, at);
        _sessions[session.Id] = session;
        return Task.FromResult(session);
    }

    public Task MarkFingerprintAsync(
        ShellSessionId session,
        ShellOsGuess os,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found))
            found.MarkFingerprint(os);
        return Task.CompletedTask;
    }

    public Task NoteInputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found))
            found.NoteInput(at);
        return Task.CompletedTask;
    }

    public Task NoteOutputAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found))
            found.NoteOutput(at);
        return Task.CompletedTask;
    }

    public Task MarkLostAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found))
            found.MarkLost(at);
        return Task.CompletedTask;
    }

    public Task CloseAsync(
        ShellSessionId session,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(session, out var found))
            found.Close(at);
        return Task.CompletedTask;
    }

    public Task<ShellSession?> FindAsync(ShellSessionId session, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(session, out var found) ? found : null);

    public Task<IReadOnlyList<ShellSession>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var matches = _sessions.Values
            .Where(s => s.EngagementId == engagement)
            .OrderBy(s => s.OpenedAt)
            .ThenBy(s => s.Id.Value)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ShellSession>>(matches);
    }
}
