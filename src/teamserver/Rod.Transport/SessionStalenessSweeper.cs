using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.CoreState.Application;
using Rod.CoreState.Sessions;

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
/// <see cref="SessionSweepService.SweepStaleAsync"/> once per sweep interval
/// against the threshold, so a beacon stream that dies silently -- no clean
/// close, no more frames -- stops holding its session Active forever. Closing
/// the session is what drops the implant off the online roster; the beacon
/// stream's own reader ends the connection on its next frame so a recovered
/// implant re-handshakes and comes back online. Each close also lands on the
/// audit trail as a system-attributed <see cref="AuditEventKind.SessionClosed"/>
/// fact (Sec 11.1) -- a session that died silently mid-watch is part of the
/// engagement's record, not just a roster change connected operators saw.
/// </summary>
/// <remarks>
/// Both the threshold and the interval are the live
/// <see cref="SessionRuntimeSettings"/> values, read on every pass -- an
/// operator's settings-page change applies on the next pass without a restart.
/// The first sweep runs immediately at startup (a restarted teamserver should
/// not wait a full interval before cleaning up the previous run's stale
/// sessions), then the loop sleeps one interval between passes.
/// <see cref="SweepOnceAsync"/> is public so tests drive a pass deterministically
/// instead of racing the timer.
/// </remarks>
public sealed class SessionStalenessSweeper : BackgroundService
{
    private readonly SessionSweepService _sweep;
    private readonly SessionRuntimeSettings _settings;
    private readonly TimeProvider _clock;
    private readonly IAuditStore _audit;

    public SessionStalenessSweeper(
        SessionSweepService sweep,
        SessionRuntimeSettings settings,
        TimeProvider clock,
        IAuditStore audit)
    {
        _sweep = sweep;
        _settings = settings;
        _clock = clock;
        _audit = audit;
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

            await Task.Delay(_settings.Current.SweepInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one sweep pass: closes every Active session whose last-seen stamp is
    /// older than the current threshold, fanning each close out to connected
    /// operators and recording it on the audit trail. Returns the closed
    /// sessions.
    /// </summary>
    public async Task<IReadOnlyList<Rod.CoreState.Sessions.Session>> SweepOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - _settings.Current.Threshold;
        var closed = await _sweep.SweepStaleAsync(cutoff, cancellationToken);

        foreach (var session in closed)
        {
            await _audit.AppendAsync(
                AuditEvent.Fact(
                    eventId: Guid.NewGuid(),
                    engagementId: session.EngagementId.Value,
                    operatorId: Guid.Empty,
                    implantId: session.ImplantId.Value,
                    taskId: Guid.Empty,
                    verb: "sweep",
                    kind: AuditEventKind.SessionClosed,
                    payload: $"swept: last seen {session.LastSeenAt:O}, silent for {now - session.LastSeenAt}",
                    output: null,
                    outcome: session.Id.ToString(),
                    at: now),
                cancellationToken);
        }

        return closed;
    }
}
