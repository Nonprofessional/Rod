using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The payload-build request schema -- the teamserver half of the build contract
/// (architecture.md Sec 6). Carries everything a build unit needs to compile a
/// self-contained, per-implant artifact: the engagement and operator it is
/// scoped and attributed to, the implant class, the target OS/arch, and the
/// transport and beacon profiles.
///
/// The per-implant material is the baked profile itself: each artifact is
/// unique because its profile is, and a lost implant self-terminates at its
/// kill date. No key material crosses this contract -- the implant's
/// cryptographic identity is the keypair it generates at first run, bound by
/// the CA-signed leaf at enroll (architecture.md Sec 9). The build contract is
/// the language-neutrality boundary, so a build unit consumes these params
/// without any teamserver-language coupling.
/// </summary>
/// <param name="Stage2">
/// The stage-2 payload a stager-class build fetches at run time
/// (architecture.md Sec 6): its id names the fetch path and its sha256 is the
/// integrity anchor the stager verifies the fetched bytes against. Null for
/// every other class -- only the stager output consumes it.
/// </param>
public sealed record BuildParams(
    EngagementId EngagementId,
    OperatorId RequestedBy,
    ImplantClass Class,
    TargetProfile Target,
    TransportProfile Transport,
    BeaconProfile Beacon,
    Stage2Payload? Stage2 = null);

/// <summary>
/// The stage-2 payload a stage-1 stager build references: the built-payload id
/// the stager fetches over the enroll listener, plus the payload's sha256
/// fingerprint baked in as the fetch's integrity check. The bytes themselves
/// stay server-side in the payload store -- only the reference crosses the
/// build contract.
/// </summary>
public sealed record Stage2Payload(
    Guid PayloadId,
    string Sha256);
