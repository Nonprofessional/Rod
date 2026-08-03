namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The malleable transport profile baked into an implant at generation
/// (architecture.md Sec 7, Sec 8): the C2 endpoint the implant dials and the
/// URI shape it speaks over the wire. Per-implant so two implants do not look
/// the same; a burned endpoint is replaceable because the listener and the
/// public endpoint are decoupled.
/// </summary>
public sealed record TransportProfile(
    string Endpoint,
    string UriPath);

/// <summary>
/// The beacon profile baked into an implant at generation (architecture.md Sec 5.1,
/// Sec 7): the sleep interval, the jitter applied to each check-in, and the kill
/// date past which the implant self-terminates. These are embedded into the
/// artifact at build time so each implant is self-contained.
/// </summary>
public sealed record BeaconProfile(
    TimeSpan Sleep,
    TimeSpan Jitter,
    DateTimeOffset KillDate);

/// <summary>
/// The target the artifact is built for. Build params are produced at request
/// time so each artifact is unique (architecture.md Sec 6); the target OS/arch
/// selects the compilation target inside the build unit.
/// </summary>
public sealed record TargetProfile(
    string OperatingSystem,
    string Architecture);
