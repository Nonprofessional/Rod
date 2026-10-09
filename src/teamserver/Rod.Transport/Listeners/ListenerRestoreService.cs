using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rod.CoreState.Listeners;

namespace Rod.Transport.Listeners;

/// <summary>
/// Rebinds the persisted engagement-scoped listeners at startup. The restore
/// runs on <see cref="IHostApplicationLifetime.ApplicationStarted"/> -- after
/// the host's own listeners are bound: classic hosting starts user hosted
/// services before the web server, so restoring from <c>StartAsync</c> would
/// probe the Kestrel-riding endpoints before the reloader exists, time out,
/// and withdraw endpoints that were about to bind. A definition whose port
/// collides with a startup entry loses cleanly and is logged, not fatal: the
/// roster reports what is actually listening, and the operator frees the port
/// or deletes and recreates the listener.
/// </summary>
internal sealed class ListenerRestoreService(
    IListenerStore definitions,
    ListenerManager manager,
    IHostApplicationLifetime lifetime,
    ILogger<ListenerRestoreService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = RestoreStoredAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task RestoreStoredAsync()
    {
        try
        {
            var stored = await definitions.ListAsync(CancellationToken.None);
            if (stored.Count == 0)
                return;

            var restored = 0;
            foreach (var definition in stored)
            {
                if (await manager.RestoreAsync(definition, CancellationToken.None) is not null)
                    restored++;
            }

            logger.LogInformation(
                "Restored {Restored} of {Stored} engagement listeners from the store.", restored, stored.Count);
        }
        catch (Exception ex)
        {
            // The restore rides the started callback, not the host's startup
            // pipeline: an exception here must not die unobserved.
            logger.LogError(ex, "Listener restore failed.");
        }
    }
}
