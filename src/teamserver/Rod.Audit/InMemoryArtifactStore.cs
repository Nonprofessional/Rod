using System.Collections.Concurrent;

namespace Rod.Audit;

/// <summary>
/// In-memory <see cref="IArtifactStore"/> by default.
/// -- no Postgres/object store yet. Artifacts live in a process-local dictionary
/// keyed by <see cref="Artifact.ArtifactId"/>; task- and engagement-scoped
/// queries filter that dictionary, oldest first. No lock is needed: saving is a
/// single dictionary write with no cross-field atomicity to protect, so the
/// concurrent collection alone is enough (the same shape as the other read-mostly
/// in-memory adapters). State is lost on restart; the port keeps callers agnostic
/// to that.
/// </summary>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, Artifact> _artifacts = new();

    public Task SaveAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        _artifacts[artifact.ArtifactId] = artifact;
        return Task.CompletedTask;
    }

    public Task<Artifact?> FindAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        _artifacts.TryGetValue(artifactId, out var found);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Artifact>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var matches = _artifacts.Values
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.StoredAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Artifact>>(matches);
    }

    public Task<ArtifactPage> ForTaskPageAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        // The artifact id breaks stored-at ties so a page boundary is stable.
        var ordered = _artifacts.Values
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.StoredAt)
            .ThenBy(a => a.ArtifactId)
            .ToArray();
        var (items, next) = ListPageWindow.TakeNewest(
            ordered, limit, cursor, a => a.StoredAt, a => a.ArtifactId);
        return Task.FromResult(new ArtifactPage(items, next));
    }

    public Task<IReadOnlyList<Artifact>> ListAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        var matches = _artifacts.Values
            .Where(a => a.EngagementId == engagementId)
            .OrderBy(a => a.StoredAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Artifact>>(matches);
    }
}
