namespace Rod.Transport.Redirectors;

/// <summary>
/// The control channel's deployment configuration
/// (<c>Redirectors:...</c> in appsettings). Deliberately one knob: the
/// channel's whole posture is settled design (architecture.md Sec 8), and
/// the rule set a node runs is derived, never configured per node.
/// </summary>
public sealed class RedirectorControlOptions
{
    public const string SectionName = "Redirectors";

    /// <summary>
    /// The upstream face pushed rules name, for deployments where the
    /// listeners' bind hosts name no dialable address of the teamserver
    /// (NAT, a container bridge). Empty (the default) resolves per rule:
    /// the listener's own bind host when it is concrete, else the local
    /// address the node's control connection arrived on.
    /// </summary>
    public string? AdvertisedUpstreamHost { get; set; }
}
