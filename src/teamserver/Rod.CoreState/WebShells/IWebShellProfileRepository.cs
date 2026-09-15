using Rod.CoreState.Engagements;

namespace Rod.CoreState.WebShells;

/// <summary>
/// Stores the engagement's web-shell profiles (architecture.md Sec 5.2's
/// Web-shell class). One profile per registered endpoint, keyed by the
/// WebShell-class implant row it hangs off; the implant repository owns
/// that row and the roster it appears in, this port owns the connection
/// details. Engagement-scoped like every other store, so cross-engagement
/// access stays impossible by construction (architecture.md Sec 3).
///
/// The default is an in-memory implementation; the port keeps callers
/// agnostic to that.
/// </summary>
public interface IWebShellProfileRepository
{
    /// <summary>Upserts the profile for its implant id (the register path writes it once).</summary>
    Task SaveAsync(WebShellProfile profile, CancellationToken cancellationToken = default);

    /// <summary>The profile for the WebShell-class implant, or null when it has none.</summary>
    Task<WebShellProfile?> FindAsync(ImplantId implant, CancellationToken cancellationToken = default);

    /// <summary>Every profile in the engagement, oldest registration first.</summary>
    Task<IReadOnlyList<WebShellProfile>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the profile. The implant row itself is retired through the
    /// implant repository by the use case, so the roster keeps the history.
    /// </summary>
    Task RemoveAsync(ImplantId implant, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IWebShellProfileRepository"/> by default -- the
/// durable Postgres pair replaces it when configured. Profiles live in a
/// process-local map keyed by implant id; state is lost on restart, and
/// the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryWebShellProfileRepository : IWebShellProfileRepository
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ImplantId, WebShellProfile> _profiles = new();

    public Task SaveAsync(WebShellProfile profile, CancellationToken cancellationToken = default)
    {
        _profiles[profile.ImplantId] = profile;
        return Task.CompletedTask;
    }

    public Task<WebShellProfile?> FindAsync(ImplantId implant, CancellationToken cancellationToken = default)
        => Task.FromResult(_profiles.TryGetValue(implant, out var found) ? found : null);

    public Task<IReadOnlyList<WebShellProfile>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default)
    {
        var matches = _profiles.Values
            .Where(p => p.EngagementId == engagement)
            .OrderBy(p => p.RegisteredAt)
            .ThenBy(p => p.ImplantId.Value)
            .ToArray();
        return Task.FromResult<IReadOnlyList<WebShellProfile>>(matches);
    }

    public Task RemoveAsync(ImplantId implant, CancellationToken cancellationToken = default)
    {
        _profiles.TryRemove(implant, out _);
        return Task.CompletedTask;
    }
}
