using Rod.CoreState.Engagements;

namespace Rod.CoreState.Live;

/// <summary>
/// A no-op <see cref="ILiveEventBus"/>. Publishes go nowhere; subscribing yields
/// nothing. Used as the default registration so the core layer and its unit
/// tests stay independent of the operator layer: a tasking round-trip works
/// without any live fan-out wired, and the operator layer's in-memory bus
/// (Rod.Operators) replaces this registration where live push is wanted.
///
/// This lives in core state (not Rod.Operators) deliberately -- the default must
/// be reachable from <c>AddRodTransport</c>, which cannot reference the operator
/// layer (architecture test LayerDependencyTests). The real implementation
/// overrides it from the composition root.
/// </summary>
public sealed class NullLiveEventBus : ILiveEventBus
{
    public Task PublishAsync(LiveEvent @event, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async IAsyncEnumerable<LiveEvent> SubscribeAsync(
        EngagementId engagement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Nothing is ever published; await cancellation so the subscriber does
        // not busy-spin. Yielding is unreachable in practice.
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
