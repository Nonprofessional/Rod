using System.Text.Json;

namespace Rod.Transport;

/// <summary>
/// The live session-staleness values: how long a session may go silent before
/// the sweep closes it (<see cref="Threshold"/>) and how often the sweep runs
/// (<see cref="SweepInterval"/>). An immutable pair so the settings holder can
/// swap values atomically -- the sweeper reads one consistent pair per pass.
/// </summary>
public sealed record SessionStalenessValues(TimeSpan Threshold, TimeSpan SweepInterval);

/// <summary>
/// The runtime-mutable session settings: boot defaults from configuration,
/// then live values the operator changes from the settings page -- without a
/// restart, because <see cref="SessionStalenessSweeper"/> reads
/// <see cref="Current"/> on every pass. Changes persist to a small JSON file
/// (the composition root decides the path) so a restart keeps the operator's
/// values; the persistence is best-effort, same posture as the advisory
/// last-seen stamp -- the in-memory value is authoritative while the process
/// lives, and a file that cannot be written or parsed never takes the
/// teamserver down with it.
/// </summary>
public sealed class SessionRuntimeSettings
{
    // The bounds the setter and the settings endpoint both enforce. The
    // threshold must sit above any implant's contact interval (a threshold
    // shorter than the sleep would flap every quiet period to offline) but
    // still vanish dead hosts within a day; the sweep interval trades prompt
    // drops against sweep cost.
    public static readonly TimeSpan MinThreshold = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxThreshold = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxSweepInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _persistencePath;
    private volatile SessionStalenessValues _values;

    public SessionRuntimeSettings(SessionStalenessOptions defaults, string? persistencePath)
    {
        _persistencePath = persistencePath;
        _values = new SessionStalenessValues(defaults.Threshold, defaults.SweepInterval);
        if (persistencePath is null || !File.Exists(persistencePath))
            return;

        // A persisted operator change outranks the boot defaults. A file that
        // cannot be parsed is ignored rather than fatal: the settings page
        // writes it atomically, so a mangled file means a hand edit gone
        // wrong, and failing startup over it would trade a recoverable
        // annoyance for an outage.
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedValues>(File.ReadAllText(persistencePath), JsonOptions);
            if (TimeSpan.TryParse(persisted?.Threshold, out var threshold)
                && TimeSpan.TryParse(persisted?.SweepInterval, out var interval))
                _values = Validate(new SessionStalenessValues(threshold, interval));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            // Fall back to the boot defaults; the next settings write repairs
            // the file.
        }
    }

    /// <summary>The values the next sweep pass uses.</summary>
    public SessionStalenessValues Current => _values;

    /// <summary>
    /// Replaces the live values and persists them. Throws
    /// <see cref="ArgumentOutOfRangeException"/> outside the documented
    /// bounds; the endpoint maps that to a 400.
    /// </summary>
    public SessionStalenessValues Change(TimeSpan threshold, TimeSpan sweepInterval)
    {
        var values = Validate(new SessionStalenessValues(threshold, sweepInterval));
        _values = values;
        Persist(values);
        return values;
    }

    private static SessionStalenessValues Validate(SessionStalenessValues values)
    {
        if (values.Threshold < MinThreshold || values.Threshold > MaxThreshold)
            throw new ArgumentOutOfRangeException(
                nameof(values.Threshold),
                $"The staleness threshold must be between {MinThreshold} and {MaxThreshold}.");
        if (values.SweepInterval < MinSweepInterval || values.SweepInterval > MaxSweepInterval)
            throw new ArgumentOutOfRangeException(
                nameof(values.SweepInterval),
                $"The sweep interval must be between {MinSweepInterval} and {MaxSweepInterval}.");
        return values;
    }

    // Write-then-move so a reader (the next boot) never observes a torn file;
    // failures are swallowed because the in-memory value is already applied --
    // the process keeps the change even if the disk refuses to remember it.
    private void Persist(SessionStalenessValues values)
    {
        if (_persistencePath is null)
            return;
        try
        {
            var json = JsonSerializer.Serialize(
                new PersistedValues(values.Threshold.ToString(), values.SweepInterval.ToString()), JsonOptions);
            var temporary = _persistencePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _persistencePath, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort persistence; the live value stands.
        }
    }

    // TimeSpan rides the file in its string form ("00:15:00"), the same shape
    // the configuration section uses -- System.Text.Json has no native
    // TimeSpan converter, and this keeps the file hand-editable.
    private sealed record PersistedValues(string? Threshold, string? SweepInterval);
}
