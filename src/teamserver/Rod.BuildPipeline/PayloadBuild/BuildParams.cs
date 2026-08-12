using Rod.CoreState;
using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;

namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The payload-build request schema -- the teamserver half of the build contract
/// (architecture.md Sec 6). Carries everything a build unit needs to compile a
/// self-contained, per-implant artifact: the engagement and operator it is
/// scoped and attributed to, the implant class, the target OS/arch, the
/// transport and beacon profiles, and the embedded per-implant key.
///
/// The per-implant material -- <see cref="Key"/> and <see cref="KillDate"/> -- is
/// generated at request time so each artifact is unique (architecture.md Sec 6,
/// Sec 5.1): no two implants share a key, and a lost implant self-terminates at
/// its kill date. The build contract is the language-neutrality boundary, so a
/// build unit consumes these params without any teamserver-language coupling.
/// </summary>
public sealed record BuildParams(
    EngagementId EngagementId,
    OperatorId RequestedBy,
    ImplantClass Class,
    TargetProfile Target,
    TransportProfile Transport,
    BeaconProfile Beacon,
    string Key);
