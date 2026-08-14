using Microsoft.Extensions.Hosting;
using Rod.CoreState.Application;

namespace Rod.Transport;

/// <summary>
/// The session staleness sweep options (architecture.md Sec 10.3), read from the
/// <c>Sessions:Staleness</c> configuration section. <see cref="Threshold"/> is
/// how long a session may go without a beacon frame before the sweep closes it;
/// <see cref="SweepInterval"/> is how often the check runs.
/// </summary>
public sealed record SessionStalenessOptions(TimeSpan Threshold, TimeSpan SweepInterval)
{
    /// <summary>The built-in defaults: 15 minutes of silence, checked every minute.</summary>
    public static SessionStalenessOptions Default { get; } = new(
        Threshold: TimeSpan.FromMinutes(15),
        SweepInterval: TimeSpan.FromMinutes(1));

    /// <summary>
    /// Binds the options from <paramref name="configuration"/>. A missing section
    /// keeps the defaults; a present-but-unparseable value fails loudly -- a
    /// silently disabled sweep would leave dead sessions on the roster forever.
    /// </summary>
    public static SessionStalenessOptions FromConfiguration(
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var threshold = Parse(configuration["Sessions:Staleness:Threshold"], "Sessions:Staleness:Threshold", Default.Threshold);
        var interval = Parse(configuration["Sessions:Staleness:SweepInterval"], "Sessions:Staleness:SweepInterval", Default.SweepInterval);
        if (threshold <= TimeSpan.Zero)
            throw new InvalidOperationException("Sessions:Staleness:Threshold must be positive.");
        if (interval <= TimeSpan.Zero)
            throw new InvalidOperationException("Sessions:Staleness:SweepInterval must be positive.");
        return new SessionStalenessOptions(threshold, interval);
    }

    private static TimeSpan Parse(string? value, string key, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!TimeSpan.TryParse(value, out var parsed))
            throw new InvalidOperationException($"'{key}' must be a TimeSpan string; got '{value}'.");
        return parsed;
    }
}

/// <summary>
/// The hosted staleness sweeper (architecture.md Sec 10.3): runs
/// <see cref="SessionSweepService.SweepStaleAsync"/> once per
/// <see cref="SessionStalenessOptions.SweepInterval"/> against the threshold, so
/// a beacon stream that dies silently -- no clean close, no more frames -- stops
/// holding its session Active forever. Closing the session is what drops the
/// implant off the online roster; the beacon stream's own reader ends the
/// connection on its next frame so a recovered implant re-handshakes and comes
/// back online.
/// </summary>
/// <remarks>
/// The first sweep runs immediately at startup (a restarted teamserver should
/// not wait a full interval before cleaning up the previous run's stale
/// sessions), then the loop sleeps one interval between passes.
/// <see cref="SweepOnceAsync"/> is public so tests drive a pass deterministically
/// instead of racing the timer.
/// </remarks>
public sealed class SessionStalenessSweeper : BackgroundService
{
    private readonly SessionSweepService _sweep;
    private readonly SessionStalenessOptions _options;
    private readonly TimeProvider _clock;

    public SessionStalenessSweeper(
        SessionSweepService sweep,
        SessionStalenessOptions options,
        TimeProvider clock)
    {
        _sweep = sweep;
        _options = options;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(_options.SweepInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one sweep pass: closes every Active session whose last-seen stamp is
    /// older than the configured threshold, fanning each close out to connected
    /// operators. Returns the closed sessions.
    /// </summary>
    public Task<IReadOnlyList<Rod.CoreState.Sessions.Session>> SweepOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow() - _options.Threshold;
        return _sweep.SweepStaleAsync(cutoff, cancellationToken);
    }
}
