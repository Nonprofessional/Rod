using Rod.CoreState.Engagements;

namespace Rod.CoreState.Launchers;

/// <summary>
/// The engagement-scoped launcher registry (architecture.md Sec 8): the
/// rendered launcher rows an operator can return to -- re-copy, watch, and
/// revoke. Engagement-scoped by construction like every other operator
/// surface; a foreign engagement's rows are indistinguishable from absent
/// ones.
/// </summary>
public interface ILauncherStore
{
    Task SaveAsync(Launcher launcher, CancellationToken cancellationToken = default);

    Task<Launcher?> FindAsync(LauncherId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Launcher>> ListByEngagementAsync(
        EngagementId engagement,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the row. Returns false when it was already gone.</summary>
    Task<bool> RemoveAsync(LauncherId id, CancellationToken cancellationToken = default);
}
