namespace Rod.Transport.Campaigns;

/// <summary>
/// The send engine's knobs (architecture.md Sec 11.5) -- engine-level
/// cadence, never egress: the relay itself is named per campaign in the
/// campaign body, because which relay sends a campaign's mail is an OPSEC
/// decision the runbook owns, not a server default. Binds from the
/// <c>Campaigns</c> section; both knobs default standing alone when the
/// section is absent.
/// </summary>
public sealed class CampaignsOptions
{
    public const string SectionName = "Campaigns";

    /// <summary>
    /// The reconciler's scan cadence in seconds -- how long after a launch
    /// or a build completion the next send can land. Delivery is not
    /// latency-sensitive; the tick exists so the engine re-reads the store
    /// rather than holding state of its own.
    /// </summary>
    public int EngineTickSeconds { get; set; } = 5;

    /// <summary>
    /// The bound around one SMTP attempt (connect, auth, send) in seconds.
    /// A relay that answers nothing holds one tick's send slot for this
    /// long and no longer; the recipient fails with the timeout and the
    /// campaign moves on -- single-attempt, like every delivery this
    /// platform performs.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = 20;
}
