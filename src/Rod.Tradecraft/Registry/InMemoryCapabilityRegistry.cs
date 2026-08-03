using System.Collections.Concurrent;
using Rod.Tradecraft.Capabilities;
using Rod.Tradecraft.Modules;

namespace Rod.Tradecraft.Registry;

/// <summary>
/// The walking-skeleton in-memory <see cref="ICapabilityRegistry"/>
/// (architecture.md Sec 4.1 layer 6). Backed by a concurrent dictionary keyed by
/// verb (case-insensitive); insertion order is preserved separately so
/// <see cref="ListAsync"/> reflects registration order despite the dictionary's
/// unordered enumeration.
/// </summary>
/// <remarks>
/// Single-process by design: the teamserver is a monolithic kernel
/// (architecture.md Sec 4), so an in-process registry is sufficient until a
/// durable store is warranted. All operations are allocation-free on the hot
/// read path (<see cref="FindAsync"/> is a dictionary lookup).
/// </remarks>
public sealed class InMemoryCapabilityRegistry : ICapabilityRegistry
{
    // Verb (case-insensitive) -> the module registered for it. A later
    // registration replaces the prior entry, so the last module loaded for a
    // verb is the authority.
    private readonly ConcurrentDictionary<string, ICapabilityModule> _byVerb = new(StringComparer.OrdinalIgnoreCase);

    // Registration order, distinct, so ListAsync is stable and deduplicated when
    // a re-registration replaces a verb. Guarded by a lock since ConcurrentDictionary
    // does not preserve insertion order.
    private readonly List<ICapabilityModule> _ordered = new();
    private readonly object _orderLock = new();

    public Task RegisterAsync(ICapabilityModule module, CancellationToken cancellationToken = default)
    {
        var verb = module.Descriptor.Verb;
        _byVerb[verb] = module;

        lock (_orderLock)
        {
            // Drop any prior entry for this verb first, then append, so the list
            // stays deduplicated and the latest registration is last.
            for (var i = _ordered.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_ordered[i].Descriptor.Verb, verb, StringComparison.OrdinalIgnoreCase))
                    _ordered.RemoveAt(i);
            }
            _ordered.Add(module);
        }

        return Task.CompletedTask;
    }

    public Task<ICapabilityModule?> FindAsync(string verb, CancellationToken cancellationToken = default)
        => Task.FromResult(_byVerb.TryGetValue(verb, out var module) ? module : null);

    public Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_orderLock)
        {
            return Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(
                _ordered.Select(m => m.Descriptor).ToArray());
        }
    }
}
