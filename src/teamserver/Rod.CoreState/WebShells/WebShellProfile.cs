using Rod.CoreState.Engagements;
using Rod.CoreState.Operators;

namespace Rod.CoreState.WebShells;

/// <summary>
/// The connection profile of one web-shell endpoint (architecture.md
/// Sec 5.2's Web-shell class): everything the protocol adapter needs to
/// drive the script an operator placed in a target's web root. The profile
/// is the side table of the <c>ImplantClass.WebShell</c> implant row it is
/// keyed by -- the implant is the identity anchor tasks, audit, and the
/// roster already hang off (a web-shell never enrolls or handshakes; the
/// register use case creates its row directly), while the profile holds
/// what only the web-shell surface reads: where the script lives and how
/// to talk to it.
///
/// The fields are the classic web-shell manager vocabulary, kept
/// deliberately neutral: <see cref="Password"/> is the POST parameter the
/// one-liner evaluates (the "connection password"), and the encoder pair
/// is baked into both the generated script and every request the adapter
/// sends, so a profile and its script always agree.
/// </summary>
public sealed record WebShellProfile
{
    public required ImplantId ImplantId { get; init; }
    public required EngagementId EngagementId { get; init; }

    /// <summary>The web root the script was placed in, absolute http(s) URL.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// The protocol adapter that drives the script, by its registry id
    /// (e.g. <c>rod-php</c>). The transport layer owns the registry;
    /// the profile stores the choice.
    /// </summary>
    public required string AdapterId { get; init; }

    /// <summary>
    /// The POST parameter the script evaluates -- the shared secret of the
    /// classic managers, in the only shape their protocols have one.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>The request encoder the script was generated for (e.g. <c>base64</c>).</summary>
    public required string Encoder { get; init; }

    /// <summary>The response decoder baked into the script's output wrapper.</summary>
    public required string Decoder { get; init; }

    public required OperatorId RegisteredBy { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// When the endpoint was last probed and whether that probe succeeded --
    /// the web-shell's health answer, standing in for the beacon cadence an
    /// implant reports liveness with. Null before the first probe.
    /// </summary>
    public DateTimeOffset? LastProbeAt { get; private set; }

    /// <summary>Whether the last probe round-tripped; null before the first probe.</summary>
    public bool? LastProbeOk { get; private set; }

    /// <summary>
    /// Records a probe's outcome. A failed probe keeps the stamp -- a dead
    /// endpoint that answers nothing is exactly what the roster should say.
    /// </summary>
    public void NoteProbe(DateTimeOffset at, bool ok)
    {
        LastProbeAt = at;
        LastProbeOk = ok;
    }
}
