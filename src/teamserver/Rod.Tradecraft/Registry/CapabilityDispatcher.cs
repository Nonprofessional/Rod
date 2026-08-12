using Rod.Tradecraft.Capabilities;

namespace Rod.Tradecraft.Registry;

/// <summary>
/// Routes a <see cref="CapabilityInvocation"/> to the module registered for its
/// verb (architecture.md Sec 10.3). The single dispatch entry point: look the
/// verb up in the <see cref="ICapabilityRegistry"/>, hand the invocation to the
/// matching module, and return its result. A verb with no registered module is
/// a normal <see cref="CapabilityStatus.NotFound"/> result rather than a thrown
/// exception, so a future task-issuance gate can treat "unhandled verb" as a
/// value.
/// </summary>
/// <remarks>
/// The dispatcher holds no policy of its own: it does not authorize, audit, or
/// gate on the implant's advertised capabilities -- those concerns belong to the
/// task path that consumes dispatch, not to the tradecraft layer's skeleton
/// (architecture.md Sec 9/10). The skeleton proves the contract end to end;
/// wiring it onto the live task path arrives with the offensive-capability
/// milestones.
/// </remarks>
public sealed class CapabilityDispatcher
{
    private readonly ICapabilityRegistry _registry;

    public CapabilityDispatcher(ICapabilityRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Dispatches <paramref name="invocation"/> to its module. Returns
    /// <see cref="CapabilityResult.NotFoundFor"/> when no module is registered
    /// for the verb; otherwise the module's own result.
    /// </summary>
    public async Task<CapabilityResult> DispatchAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var module = await _registry.FindAsync(invocation.Verb, cancellationToken);
        if (module is null)
            return CapabilityResult.NotFoundFor(invocation.Verb);

        return await module.ExecuteAsync(invocation, cancellationToken);
    }
}
