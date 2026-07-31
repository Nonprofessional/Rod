using System.Collections.Concurrent;

namespace Rod.CoreState.Implants;

/// <summary>
/// In-memory <see cref="IImplantRepository"/> for the walking skeleton
/// (roadmap M1 -- no Postgres yet). State lives in process and is lost on
/// restart; the port keeps callers agnostic to that.
/// </summary>
public sealed class InMemoryImplantRepository : IImplantRepository
{
    private readonly ConcurrentDictionary<ImplantId, Implant> _implants = new();

    public Task<Implant?> FindAsync(ImplantId id, CancellationToken cancellationToken = default)
        => Task.FromResult(_implants.TryGetValue(id, out var implant) ? implant : null);

    public async Task<Implant> GetOrThrowAsync(ImplantId id, CancellationToken cancellationToken = default)
        => await FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Implant {id} does not exist.");

    public Task SaveAsync(Implant implant, CancellationToken cancellationToken = default)
    {
        _implants[implant.Id] = implant;
        return Task.CompletedTask;
    }
}
