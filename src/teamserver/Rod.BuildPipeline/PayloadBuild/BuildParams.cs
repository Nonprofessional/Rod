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
public sealed record BuildParams(
    EngagementId EngagementId,
    OperatorId RequestedBy,
    ImplantClass Class,
    TargetProfile Target,
    TransportProfile Transport,
    BeaconProfile Beacon);
