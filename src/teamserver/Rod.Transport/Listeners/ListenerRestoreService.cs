using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;

namespace Rod.Transport.Listeners;

/// <summary>
/// Rebinds the persisted engagement-scoped listeners at startup. Runs after
/// the host's configuration listeners are bound, so a definition whose port
/// collides with a startup entry loses cleanly and is logged, not fatal: the
/// roster reports what is actually listening, and the operator frees the port
/// or deletes and recreates the listener.
/// </summary>
internal sealed class ListenerRestoreService(
    IListenerStore definitions,
    ListenerManager manager,
    ILogger<ListenerRestoreService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var stored = await definitions.ListAsync(cancellationToken);
        if (stored.Count == 0)
            return;

        var restored = 0;
        foreach (var definition in stored)
        {
            if (await manager.RestoreAsync(definition, cancellationToken) is not null)
                restored++;
        }

        logger.LogInformation(
            "Restored {Restored} of {Stored} engagement listeners from the store.", restored, stored.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
