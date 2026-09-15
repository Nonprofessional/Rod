using System.Collections.Concurrent;
using Rod.CoreState;
using Rod.CoreState.ShellSessions;

namespace Rod.Transport.Listeners.ShellCatch;

/// <summary>
/// The rendezvous between the operator endpoints and the caught shells the
/// shellcatch listeners hold (architecture.md Sec 8) -- the shell-facing
/// sibling of the beacon stream's channel hub, keyed by shell session id.
/// The pump adds a shell on accept and removes it when the connection ends;
/// the input, output, and close routes resolve their shell through here.
/// A shell that ended is gone from the hub, and its routes answer from the
/// durable session registry instead -- the hub holds live sockets only,
/// never history.
/// </summary>
public sealed class ShellCatchHub
{
    private readonly ConcurrentDictionary<ShellSessionId, CaughtShell> _shells = new();

    /// <summary>Registers a live shell. The pump's accept path; one shell per id.</summary>
    public void Add(CaughtShell shell) => _shells[shell.Session.Id] = shell;

    /// <summary>Removes a shell. The pump's exit path; a no-op when already removed.</summary>
    public void Remove(ShellSessionId session) => _shells.TryRemove(session, out _);

    /// <summary>The live shell for the session id, or null when it has ended.</summary>
    public CaughtShell? Find(ShellSessionId session) => _shells.TryGetValue(session, out var shell) ? shell : null;

    /// <summary>Every live shell across all engagements, for diagnostics.</summary>
    public IReadOnlyCollection<CaughtShell> List() => _shells.Values.ToArray();
}
